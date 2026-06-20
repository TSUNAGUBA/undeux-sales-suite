/**
 * バイヤー向け OTB（Open To Buy）サマリーのモックデータ生成（ルールベース・SoT）。
 *
 * 本プロダクトは AI を後続で本格導入する想定のため、現時点では LLM ではなく**ルールベース**で
 * 数値・所見を決定的に生成する（mart/index の executiveSummary と同じ思想）。乱数は使わず、
 * 固定のカテゴリ定義から全社集計・ウォーターフォール・推移・推奨アクションを導出する。
 *
 * OTB 定義（要件）:
 *   OTB ＝ 目標在庫 － 期首在庫 － 発注残 ＋ 目標売上
 *   例: 期首在庫200・発注残500・目標売上500・目標在庫1000 → OTB 800（単位は任意スケール）
 *
 * 発注残の状態内訳（簡易版）:
 *   未出荷 ＝ 発注 － 出荷EDI ／ 輸送中 ＝ 出荷EDI － 検収 ／ 検収済 ＝ 在庫計上済
 *
 * 予算（売上予算・仕入予算）は `composables/useBudget.ts` から渡せる。登録があれば目標売上・
 * 仕入予算に反映し、OTB 残高・OTB 利用率が連動して変わる（「登録した値を他ページで活用」）。
 */

/** 円換算用（基礎値は百万円単位で保持し、出力時に円へ換算する）。 */
const YEN_PER_UNIT = 1_000_000

/** 推奨アクション種別。 */
export type OtbActionType = 'add' | 'stop' | 'hold'

/** カテゴリ別 OTB 行（ランキング⑦・ウォーターフォール内訳の素材）。値は円。 */
export interface OtbCategory {
  code: string
  name: string
  /** 期首在庫（円）。 */
  openingInventory: number
  /** 発注残（円）。 */
  backlog: number
  /** 目標売上（円）。予算スケール後。 */
  targetSales: number
  /** 目標在庫（円・固定の目標）。 */
  targetInventory: number
  /** OTB 残高（円）＝ 目標在庫 － 期首在庫 － 発注残 ＋ 目標売上。 */
  otb: number
  /** OTB 利用率（0..1）＝ 発注残 ÷（発注残＋OTB）。 */
  otbUsageRate: number
  /** Weeks of Supply（週）。 */
  wos: number
  /** 欠品リスク SKU 数。 */
  stockoutRiskSku: number
  /** 過剰在庫 SKU 数。 */
  excessStockSku: number
  /** 平均リードタイム（日）。 */
  leadTimeDays: number
  /** 推奨アクション。 */
  action: OtbActionType
  /** 推奨発注額（円・add のみ > 0）。 */
  recommendedOrder: number
}

/** ウォーターフォールの1ステップ。 */
export interface OtbWaterfallStep {
  label: string
  /** 寄与額（円）。add は加算、sub は減算、base/total は到達点。 */
  value: number
  kind: 'base' | 'add' | 'sub' | 'total'
}

/** 発注残の状態別内訳（円）。 */
export interface OtbBacklogBreakdown {
  /** 未出荷 ＝ 発注 － 出荷EDI。 */
  unshipped: number
  /** 輸送中 ＝ 出荷EDI － 検収。 */
  inTransit: number
  /** 検収済 ＝ 在庫計上済。 */
  received: number
}

/** OTB 推移の1点（週次）。 */
export interface OtbTrendPoint {
  week: string
  otb: number
  backlog: number
  forecastInventory: number
}

/** OTB サマリー全体（画面が描画する素材一式）。 */
export interface OtbSummary {
  /** OTB 残高（円）。 */
  otbBalance: number
  /** OTB 利用率（0..1）。 */
  otbUsageRate: number
  /** 発注残（円）。 */
  backlog: number
  /** 予測月末在庫（円）。 */
  forecastEomInventory: number
  /** WOS（週）。 */
  wos: number
  /** 欠品リスク SKU 数。 */
  stockoutRiskSku: number
  /** 過剰在庫 SKU 数。 */
  excessStockSku: number
  /** 平均リードタイム（日）。 */
  averageLeadTime: number
  /** 期首在庫（円）。 */
  openingInventory: number
  /** 目標在庫（円）。 */
  targetInventory: number
  /** 目標売上（円）。 */
  targetSales: number
  /** 仕入予算（円）。 */
  purchaseBudget: number
  /** 推奨発注総額（円）。 */
  recommendedOrderTotal: number
  categories: OtbCategory[]
  waterfall: OtbWaterfallStep[]
  backlogBreakdown: OtbBacklogBreakdown
  trend: OtbTrendPoint[]
  aiComment: string
}

/**
 * カテゴリ基礎定義（百万円単位）。目標在庫は固定の目標、目標売上は基礎値（予算未登録時の既定）。
 * 全社集計が要件の例（OTB 8億・発注残 5億・期首 2億・目標在庫 10億・欠品351・過剰573・LT21日）に
 * 一致するよう配分している。
 */
interface OtbCategoryBase {
  code: string
  name: string
  openingInventory: number
  backlog: number
  baseSales: number
  targetInventory: number
  stockoutRiskSku: number
  excessStockSku: number
  leadTimeDays: number
}

const CATEGORY_BASE: readonly OtbCategoryBase[] = [
  { code: '11', name: 'カテゴリ11 アウター', openingInventory: 18, backlog: 60, baseSales: 70, targetInventory: 158, stockoutRiskSku: 60, excessStockSku: 25, leadTimeDays: 18 },
  { code: '21', name: 'カテゴリ21 ニット', openingInventory: 65, backlog: 70, baseSales: 60, targetInventory: 85, stockoutRiskSku: 5, excessStockSku: 170, leadTimeDays: 28 },
  { code: '33', name: 'カテゴリ33 シャツ', openingInventory: 16, backlog: 55, baseSales: 65, targetInventory: 146, stockoutRiskSku: 52, excessStockSku: 20, leadTimeDays: 17 },
  { code: '42', name: 'カテゴリ42 パンツ', openingInventory: 18, backlog: 70, baseSales: 70, targetInventory: 158, stockoutRiskSku: 48, excessStockSku: 28, leadTimeDays: 20 },
  { code: '55', name: 'カテゴリ55 ワンピース', openingInventory: 8, backlog: 55, baseSales: 75, targetInventory: 108, stockoutRiskSku: 108, excessStockSku: 6, leadTimeDays: 16 },
  { code: '63', name: 'カテゴリ63 スカート', openingInventory: 18, backlog: 60, baseSales: 60, targetInventory: 148, stockoutRiskSku: 45, excessStockSku: 34, leadTimeDays: 19 },
  { code: '74', name: 'カテゴリ74 服飾小物', openingInventory: 12, backlog: 40, baseSales: 40, targetInventory: 117, stockoutRiskSku: 25, excessStockSku: 60, leadTimeDays: 22 },
  { code: '88', name: 'カテゴリ88 シューズ', openingInventory: 45, backlog: 90, baseSales: 60, targetInventory: 80, stockoutRiskSku: 8, excessStockSku: 230, leadTimeDays: 30 },
]

/** 基礎の目標売上合計（円）。予算未登録時の既定値（＝5億）。 */
const BASE_SALES_TOTAL_YEN
  = CATEGORY_BASE.reduce((sum, c) => sum + c.baseSales, 0) * YEN_PER_UNIT

/** 仕入予算の既定値（円・10桁規模）。OTB 残高8億に対し利用率約62%になるよう設定。 */
const DEFAULT_PURCHASE_BUDGET_YEN = 2_100_000_000

/** WOS 算出の期間週数（四半期＝13週基準）。 */
const WEEKS_IN_PERIOD = 13

/** 推奨アクション判定（ルールベース）。WOS と OTB 余力から決定的に分類する。 */
function classifyAction(wos: number, otb: number): OtbActionType {
  // 過剰・OTB枯渇 → 発注停止。
  if (wos >= 9 || otb < 20 * YEN_PER_UNIT) return 'stop'
  // 高回転かつ OTB 余力あり → 追加発注。
  if (wos < 6 && otb > 80 * YEN_PER_UNIT) return 'add'
  return 'hold'
}

function clamp01(value: number): number {
  return Math.max(0, Math.min(1, value))
}

/**
 * OTB サマリー（モック）を生成する。
 * @param options.salesBudget 売上予算（円）。登録があれば目標売上に反映（カテゴリへ按分）。
 * @param options.purchaseBudget 仕入予算（円）。登録があれば OTB 利用率の分母に反映。
 */
export function buildOtbSummary(options?: {
  salesBudget?: number | null
  purchaseBudget?: number | null
}): OtbSummary {
  const salesTotalYen
    = options?.salesBudget && options.salesBudget > 0 ? options.salesBudget : BASE_SALES_TOTAL_YEN
  const purchaseBudgetYen
    = options?.purchaseBudget && options.purchaseBudget > 0
      ? options.purchaseBudget
      : DEFAULT_PURCHASE_BUDGET_YEN
  const salesScale = salesTotalYen / BASE_SALES_TOTAL_YEN

  const categories: OtbCategory[] = CATEGORY_BASE.map((base) => {
    const openingInventory = base.openingInventory * YEN_PER_UNIT
    const backlog = base.backlog * YEN_PER_UNIT
    const targetInventory = base.targetInventory * YEN_PER_UNIT
    const targetSales = Math.round(base.baseSales * YEN_PER_UNIT * salesScale)
    const otb = targetInventory - openingInventory - backlog + targetSales
    const wos = targetSales > 0 ? (WEEKS_IN_PERIOD * openingInventory) / targetSales : 0
    const action = classifyAction(wos, otb)
    const recommendedOrder = action === 'add' ? Math.round(Math.max(0, otb) * 0.42) : 0
    const usageDenom = backlog + Math.max(0, otb)
    return {
      code: base.code,
      name: base.name,
      openingInventory,
      backlog,
      targetSales,
      targetInventory,
      otb,
      otbUsageRate: usageDenom > 0 ? clamp01(backlog / usageDenom) : 1,
      wos,
      stockoutRiskSku: base.stockoutRiskSku,
      excessStockSku: base.excessStockSku,
      leadTimeDays: base.leadTimeDays,
      action,
      recommendedOrder,
    }
  })

  const sum = (pick: (c: OtbCategory) => number): number =>
    categories.reduce((total, c) => total + pick(c), 0)

  const openingInventory = sum((c) => c.openingInventory)
  const backlog = sum((c) => c.backlog)
  const targetSales = sum((c) => c.targetSales)
  const targetInventory = sum((c) => c.targetInventory)
  const otbBalance = sum((c) => c.otb)
  const recommendedOrderTotal = sum((c) => c.recommendedOrder)
  const stockoutRiskSku = sum((c) => c.stockoutRiskSku)
  const excessStockSku = sum((c) => c.excessStockSku)
  const averageLeadTime
    = categories.length > 0 ? sum((c) => c.leadTimeDays) / categories.length : 0

  // 予測月末在庫 ＝ 期首在庫 ＋ 発注残（入荷予定）＋ 推奨追加発注 － 目標売上（消化）。
  const forecastEomInventory = openingInventory + backlog + recommendedOrderTotal - targetSales
  const wos = targetSales > 0 ? (WEEKS_IN_PERIOD * openingInventory) / targetSales : 0
  // OTB 利用率 ＝ （仕入予算 － OTB残高）÷ 仕入予算（既コミット割合）。
  const otbUsageRate = purchaseBudgetYen > 0 ? clamp01((purchaseBudgetYen - otbBalance) / purchaseBudgetYen) : 0

  const waterfall: OtbWaterfallStep[] = [
    { label: '目標在庫', value: targetInventory, kind: 'base' },
    { label: '＋ 目標売上', value: targetSales, kind: 'add' },
    { label: '－ 期首在庫', value: -openingInventory, kind: 'sub' },
    { label: '－ 発注残', value: -backlog, kind: 'sub' },
    { label: 'OTB残高', value: otbBalance, kind: 'total' },
  ]

  // 発注残の状態内訳（未出荷45%・輸送中30%・検収済25%）。端数は検収済で吸収し合計を一致させる。
  const unshipped = Math.round(backlog * 0.45)
  const inTransit = Math.round(backlog * 0.3)
  const backlogBreakdown: OtbBacklogBreakdown = {
    unshipped,
    inTransit,
    received: backlog - unshipped - inTransit,
  }

  const trend = buildTrend(otbBalance, backlog, forecastEomInventory)

  const aiComment = buildAiComment(categories)

  return {
    otbBalance,
    otbUsageRate,
    backlog,
    forecastEomInventory,
    wos,
    stockoutRiskSku,
    excessStockSku,
    averageLeadTime,
    openingInventory,
    targetInventory,
    targetSales,
    purchaseBudget: purchaseBudgetYen,
    recommendedOrderTotal,
    categories,
    waterfall,
    backlogBreakdown,
    trend,
    aiComment,
  }
}

/** 直近8週の固定ラベル（モックのため決定的。時刻依存にしない）。 */
const TREND_WEEKS = ['5/5', '5/12', '5/19', '5/26', '6/2', '6/9', '6/16', '6/23'] as const

/**
 * OTB 推移（週次）を決定的に生成する。OTB は枠消化が進み当週へ向けて減少、発注残は緩やかに減少、
 * 月末予測在庫は入荷で増加、という典型パターンを線形補間＋微小な波で表現する。
 */
function buildTrend(otbNow: number, backlogNow: number, forecastNow: number): OtbTrendPoint[] {
  const n = TREND_WEEKS.length
  return TREND_WEEKS.map((week, i) => {
    const t = i / (n - 1) // 0..1
    const wave = Math.sin(i * 1.1) * 0.03
    return {
      week,
      // 期初は OTB が多く、消化が進んで当週値へ収束。
      otb: Math.round(otbNow * (1.28 - 0.28 * t) * (1 + wave)),
      backlog: Math.round(backlogNow * (0.82 + 0.18 * t) * (1 - wave)),
      forecastInventory: Math.round(forecastNow * (0.72 + 0.28 * t) * (1 + wave)),
    }
  })
}

/** AI コメント（ルールベース）。最も追加発注余地の大きいカテゴリと最も過剰なカテゴリから文章を組む。 */
function buildAiComment(categories: OtbCategory[]): string {
  const topAdd = [...categories]
    .filter((c) => c.action === 'add')
    .sort((a, b) => b.otb - a.otb)[0]
  const topExcess = [...categories].sort((a, b) => b.wos - a.wos)[0]
  const parts: string[] = []
  if (topAdd) {
    parts.push(
      `${topAdd.name}は販売速度が高くWOS ${topAdd.wos.toFixed(1)}週と回転良好です。`
      + `OTB余力は約${formatOku(topAdd.otb)}あり、追加発注を推奨します。`,
    )
  }
  if (topExcess && topExcess.wos >= 9) {
    parts.push(
      `一方${topExcess.name}はWOS ${topExcess.wos.toFixed(1)}週と過剰在庫傾向のため、発注の一時停止と消化策を検討してください。`,
    )
  }
  return parts.join('')
}

/** 円を「約N.N億円」表記にする（AI コメント本文用）。 */
function formatOku(yen: number): string {
  return `${(yen / 100_000_000).toFixed(1)}億円`
}
