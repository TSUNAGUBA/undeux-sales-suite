/**
 * ホーム（ダッシュボード）の「本日のインサイト & おすすめ」をルールベースで生成する純粋ロジック。
 *
 * undeux のバックエンドに LLM 基盤が無いため、主要指標（急上昇アイテム・予算達成率）から
 * 定型の一言コメントを機械的に組み立てる（全社サマリーの executiveSummary と同じ思想）。
 * Vue 非依存の純粋関数のみで構成する（テスト容易性・再利用性）。
 */
import type { ProductRow } from '~/types/api'
import { formatPercent } from './format'

/** 急上昇アイテム1件（TOP3 表示用）。 */
export interface RisingItem {
  /** 一意キー（業態|記号|品番|単品）。 */
  key: string
  /** 表示名（商品マスタ名 → 品名 fallback）。 */
  name: string
  /** 代表画像URL（無ければ null）。 */
  imageUrl: string | null
  /** 前年同期比の伸び率（%）。前年実績が無い／算出不能なら null。 */
  growthPercent: number | null
  /** 当期売上数量。 */
  quantity: number
  /** 品番3桁。 */
  hinban: string
  /** 商品記号。 */
  shohinKigou: string
  /** 商品マスタ ID（詳細遷移用。無ければ null）。 */
  masterProductId: string | null
}

/** ProductRow の自然キー（業態×記号×品番×単品）。 */
function rowKey(row: ProductRow): string {
  return `${row.gyotaiCode}|${row.shohinKigou}|${row.hinbanCode}|${row.tanpinCode}`
}

function displayName(row: ProductRow): string {
  return row.productName?.trim() || row.hinmei?.trim() || row.hinbanCode
}

/**
 * 「急上昇アイテム」を算出する。
 * - 前年同期の実績（previous）が渡された場合: 前年同期比の伸び率が大きい順（前年実績あり・当期数量が
 *   一定以上の行のみ）。伸び率が正の行を優先し、TOP `limit` 件を返す。
 * - previous が null/空の場合: 当期売上数量の多い順（伸び率は null）。
 *
 * 画像のある行を優先する（ホームの「チラ見せ」は画像ありきのため）。ただし画像付きが `limit` 件に
 * 満たない場合は画像なしの行で補完する（0件表示を避ける）。
 */
export function computeRisingItems(
  current: readonly ProductRow[],
  previous: readonly ProductRow[] | null,
  limit = 3,
  minQuantity = 1,
): RisingItem[] {
  const prevByKey = new Map<string, ProductRow>()
  for (const row of previous ?? []) {
    prevByKey.set(rowKey(row), row)
  }

  const enriched = current
    .filter((row) => row.salesQuantity >= minQuantity)
    .map((row) => {
      const prev = prevByKey.get(rowKey(row))
      const growthPercent =
        prev && prev.salesQuantity > 0
          ? ((row.salesQuantity - prev.salesQuantity) / prev.salesQuantity) * 100
          : null
      return {
        key: rowKey(row),
        name: displayName(row),
        imageUrl: row.primaryImageUrl,
        growthPercent,
        quantity: row.salesQuantity,
        hinban: row.hinbanCode,
        shohinKigou: row.shohinKigou,
        masterProductId: row.masterProductId,
      }
    })

  const hasComparison = (previous?.length ?? 0) > 0
  const sorted = enriched.slice().sort((a, b) => {
    if (hasComparison) {
      // 伸び率降順（null は最下位）。同率は当期数量降順。
      const ag = a.growthPercent ?? Number.NEGATIVE_INFINITY
      const bg = b.growthPercent ?? Number.NEGATIVE_INFINITY
      if (ag !== bg) return bg - ag
    }
    return b.quantity - a.quantity
  })

  // 画像ありを優先しつつ、不足分は画像なしで補完（順序は上のソートを維持）。
  const withImage = sorted.filter((i) => i.imageUrl)
  const withoutImage = sorted.filter((i) => !i.imageUrl)
  const picked = [...withImage, ...withoutImage].slice(0, limit)
  // 比較ありのときは「伸びている（growth>0）」ものだけに絞る余地があるが、
  // 0件表示を避けるため当期数量の多い行で必ず埋める（コメント側で基準を明示する）。
  return picked
}

/** 符号付きパーセント表記（"+12.3%" / "−4.5%"）。 */
export function signedPercentText(pct: number): string {
  const sign = pct > 0 ? '+' : pct < 0 ? '−' : '±'
  return `${sign}${Math.abs(pct).toFixed(1)}%`
}

/**
 * 急上昇アイテムの一言コメント（AIコメント風・ルールベース）。
 * 伸び率が算出できる（前年同期比あり）場合はその基準を明示し、無い場合は「売れ筋」ベースにする。
 */
export function risingComment(items: readonly RisingItem[]): string {
  if (items.length === 0) {
    return '対象期間に売上実績のあるアイテムが見つかりませんでした。フィルタや取込データを確認してください。'
  }
  const top = items[0]!
  const hasGrowth = items.some((i) => i.growthPercent !== null)
  if (hasGrowth && top.growthPercent !== null && top.growthPercent > 0) {
    return `前年同期比で「${top.name}」が ${signedPercentText(top.growthPercent)} と伸長。上位アイテムはディープダイブして横展開の余地を探りましょう。`
  }
  return `直近の売れ筋は「${top.name}」（売上数量 ${top.quantity.toLocaleString('ja-JP')} 点）。上位アイテムを起点に品番・SKU 単位で深掘りしましょう。`
}

/** 予算達成状況（全社売上）。 */
export interface BudgetAchievement {
  /** 実績（円）。 */
  actual: number
  /** 予算（円）。未登録は null。 */
  budget: number | null
  /** 達成率（%）。予算未登録／0 は null。 */
  percent: number | null
  /** 残り（%、100% までの不足分。達成済みは 0）。予算未登録は null。 */
  remainingPercent: number | null
  /** 達成したか。 */
  reached: boolean
}

/** 予算達成状況を算出する（予算未登録・0 は percent=null）。 */
export function computeBudgetAchievement(actual: number, budget: number | null): BudgetAchievement {
  if (budget === null || budget <= 0) {
    return { actual, budget, percent: null, remainingPercent: null, reached: false }
  }
  const percent = (actual / budget) * 100
  const reached = percent >= 100
  return {
    actual,
    budget,
    percent,
    remainingPercent: reached ? 0 : 100 - percent,
    reached,
  }
}

/** 予算達成率の一言コメント（AIコメント風・ルールベース）。 */
export function budgetComment(a: BudgetAchievement): string {
  if (a.percent === null) {
    return '全社売上予算が未登録です。「データ管理 › 予算管理」で年度の売上予算を登録すると、達成率メーターが表示されます。'
  }
  if (a.reached) {
    return `目標達成！達成率 ${formatPercent(a.percent)}。好調要因を分析し、次の目標設定に活かしましょう。`
  }
  if (a.percent >= 80) {
    return `達成まであと ${formatPercent(a.remainingPercent ?? 0)}。ラストスパートです。上位アイテムの供給・販促を厚くして押し上げましょう。`
  }
  if (a.percent >= 50) {
    return `達成率 ${formatPercent(a.percent)}。折り返し地点です。伸びているアイテムへリソースを寄せて加速させましょう。`
  }
  return `達成率 ${formatPercent(a.percent)}。目標との差が大きめです。売れ筋の深掘りと滞留在庫の対策を並行しましょう。`
}
