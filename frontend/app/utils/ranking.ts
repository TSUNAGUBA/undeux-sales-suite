/**
 * ランキング分析の純粋計算ロジック。
 *
 * バックエンドは集計素材（行ごとの主期間／比較期間の指標）のみ返し、
 * 順位・複合スコア・構成比・累積構成比・ABC ランク・成長率はここで算出する。
 * 並び替え指標や重みはユーザーが対話的に変更するため、サーバ往復なしで再計算できるよう
 * フロント側の表示射影とする（クロス集計の表示モード切替と同じ思想）。
 *
 * 本モジュールは Vue 非依存の純粋関数のみで構成する（テスト容易性・再利用性のため）。
 */
import type { RankingDimensionKey, RankingMetricKey, RankingMetricValues } from '~/types/api'
import { formatCurrency, formatDecimal, formatNumber, formatPercent } from './format'

/** ランキング集計軸のカタログ情報。 */
export interface RankingDimensionInfo {
  key: RankingDimensionKey
  label: string
}

/**
 * 集計軸カタログ（SoT）。バックエンドの BreakdownDimension に対応する。
 * 並び順は要件に従い 商品記号 → 単品 → 品番3桁 を先頭に置き、以降は属性軸を続ける。
 */
export const RANKING_DIMENSIONS: readonly RankingDimensionInfo[] = [
  { key: 'shohinKigo', label: '商品記号' },
  { key: 'product', label: '単品（品番-単品）' },
  { key: 'hinban', label: '品番3桁' },
  { key: 'businessType', label: '業態' },
  { key: 'department', label: '部門' },
  { key: 'season', label: '季節区分' },
  { key: 'color', label: 'カラー' },
  { key: 'size', label: 'サイズ' },
  { key: 'chohyoKubun', label: '帳票区分' },
  { key: 'tanawari1', label: '棚割1' },
  { key: 'tanawari2', label: '棚割2' },
]

const DIMENSION_BY_KEY: ReadonlyMap<RankingDimensionKey, RankingDimensionInfo> = new Map(
  RANKING_DIMENSIONS.map((d) => [d.key, d]),
)

/** 集計軸キーから表示名を取得する（未知のキーはそのまま返す）。 */
export function rankingDimensionLabel(key: RankingDimensionKey): string {
  return DIMENSION_BY_KEY.get(key)?.label ?? key
}

/** ランキング指標のカタログ情報。 */
export interface RankingMetricInfo {
  key: RankingMetricKey
  label: string
  format: 'currency' | 'number' | 'percent' | 'decimal'
  /** 値が大きいほど「良い」か小さいほど「良い」か。複合スコアの正規化方向に使う。 */
  direction: 'higher' | 'lower'
  /** 合算可能（構成比・累積・ABC・パレートの基準にできる）か。率系は不可。 */
  additive: boolean
  /** 複合スコアの初期重みに含める既定指標か。 */
  defaultComposite: boolean
}

/** 指標カタログ（SoT）。フロントの選択肢・表示順・正規化方向はこれを参照する。 */
export const RANKING_METRICS: readonly RankingMetricInfo[] = [
  { key: 'amount', label: '売上金額', format: 'currency', direction: 'higher', additive: true, defaultComposite: true },
  { key: 'quantity', label: '売上数量', format: 'number', direction: 'higher', additive: true, defaultComposite: false },
  { key: 'grossProfit', label: '粗利', format: 'currency', direction: 'higher', additive: true, defaultComposite: true },
  { key: 'grossProfitRate', label: '粗利率', format: 'percent', direction: 'higher', additive: false, defaultComposite: false },
  { key: 'sellThroughRate', label: '消化率', format: 'percent', direction: 'higher', additive: false, defaultComposite: true },
  { key: 'stockDays', label: '在日（平均）', format: 'decimal', direction: 'lower', additive: false, defaultComposite: false },
  { key: 'stock', label: '在庫数', format: 'number', direction: 'higher', additive: true, defaultComposite: false },
]

const METRIC_BY_KEY: ReadonlyMap<RankingMetricKey, RankingMetricInfo> = new Map(
  RANKING_METRICS.map((m) => [m.key, m]),
)

/** 指標キーからカタログ情報を取得する。 */
export function rankingMetricInfo(key: RankingMetricKey): RankingMetricInfo {
  const info = METRIC_BY_KEY.get(key)
  if (!info) {
    throw new Error(`不明なランキング指標です: ${key}`)
  }
  return info
}

/**
 * 1期間ぶんの集計値から指標の生値を取り出す。
 * 粗利率は amount / grossProfit から導出する。値が無い（スナップショット欠落・期間外）は null。
 */
export function metricRawValue(
  values: RankingMetricValues | null,
  key: RankingMetricKey,
): number | null {
  if (!values) {
    return null
  }
  switch (key) {
    case 'amount':
      return values.amount
    case 'quantity':
      return values.quantity
    case 'grossProfit':
      return values.grossProfit
    case 'grossProfitRate':
      return values.amount > 0 ? (values.grossProfit / values.amount) * 100 : null
    case 'sellThroughRate':
      return values.sellThroughRate
    case 'stockDays':
      return values.stockDays
    case 'stock':
      return values.stock
    default:
      return null
  }
}

/** 指標の生値を表示用文字列にフォーマットする。 */
export function formatRankingMetric(
  value: number | null | undefined,
  key: RankingMetricKey,
): string {
  if (value === null || value === undefined) {
    return '—'
  }
  switch (rankingMetricInfo(key).format) {
    case 'currency':
      return formatCurrency(value)
    case 'percent':
      return formatPercent(value)
    case 'decimal':
      return formatDecimal(value, 1)
    case 'number':
    default:
      return formatNumber(value)
  }
}

/** 複合スコアの重み（指標キーと 0 以上の重み）。 */
export interface CompositeWeight {
  key: RankingMetricKey
  weight: number
}

/** 並び替えキー（単一指標 または 複合スコア）。 */
export type RankingSortKey = RankingMetricKey | 'composite'

/** 並び順（上位＝指標の良い方から / 下位＝悪い方から）。 */
export type RankingOrder = 'top' | 'bottom'

/** 期間比較モード。 */
export type RankingComparisonMode = 'none' | 'previousYear' | 'customYear'

/**
 * ランキング画面の操作状態。
 * dimensionKey / comparisonMode / customYear はサーバ再取得が必要、それ以外は
 * クライアント側の表示射影で即時反映する（フィルタは別途「適用」ボタンで再取得）。
 */
export interface RankingControls {
  dimensionKey: RankingDimensionKey
  sortKey: RankingSortKey
  order: RankingOrder
  /** 表示する上位（下位）件数。 */
  topN: number
  /** 複合スコアの重み。 */
  weights: CompositeWeight[]
  comparisonMode: RankingComparisonMode
  /** 任意比較年（comparisonMode='customYear' 時）。 */
  customYear: number | null
  /** ABC の A 上限累積%。 */
  thresholdA: number
  /** ABC の B 上限累積%。 */
  thresholdB: number
  /** テーブルに表示する指標列。 */
  displayMetrics: RankingMetricKey[]
}

/** 1行の集計素材（キー + 当該期間の値）。複合スコア・ABC の入力に使う。 */
export interface PeriodRow {
  key: string
  values: RankingMetricValues | null
}

/**
 * 複合スコア（0..1）を算出する。
 * 各指標を母集団内で min-max 正規化（direction を考慮し、'lower' は反転）し、
 * 正規化した重み（合計 1）で加重平均する。値が null の指標はその行で 0（最低）として扱う。
 * values=null の行（その期間に存在しない）はスコア null。
 */
export function compositeScores(
  rows: readonly PeriodRow[],
  weights: readonly CompositeWeight[],
): Map<string, number | null> {
  const active = weights.filter((w) => w.weight > 0)
  const weightSum = active.reduce((sum, w) => sum + w.weight, 0)
  const result = new Map<string, number | null>()

  if (active.length === 0 || weightSum <= 0) {
    for (const row of rows) {
      result.set(row.key, null)
    }
    return result
  }

  // 指標ごとの min/max を母集団から求める（null は除外）。
  const ranges = new Map<RankingMetricKey, { min: number; max: number }>()
  for (const w of active) {
    let min = Number.POSITIVE_INFINITY
    let max = Number.NEGATIVE_INFINITY
    for (const row of rows) {
      const v = metricRawValue(row.values, w.key)
      if (v === null) continue
      if (v < min) min = v
      if (v > max) max = v
    }
    ranges.set(w.key, { min, max })
  }

  for (const row of rows) {
    if (!row.values) {
      result.set(row.key, null)
      continue
    }
    let acc = 0
    for (const w of active) {
      const info = rankingMetricInfo(w.key)
      const range = ranges.get(w.key)!
      const v = metricRawValue(row.values, w.key)
      let norm: number
      if (v === null) {
        norm = 0
      } else if (!Number.isFinite(range.min) || range.max <= range.min) {
        // 全行同値（範囲ゼロ）でその指標が差を生まない場合は中立(0.5)に揃える。
        // 最高評価(1)にすると、有効値が1件だけ（他は null）のときその1件を過大評価してしまうため。
        norm = 0.5
      } else {
        const ratio = (v - range.min) / (range.max - range.min)
        norm = info.direction === 'lower' ? 1 - ratio : ratio
      }
      acc += (w.weight / weightSum) * norm
    }
    result.set(row.key, acc)
  }
  return result
}

/** 順位付けの入力行。 */
export interface SortableRow {
  key: string
  /** 並び替え対象の値。null は順位対象外（除外）。 */
  value: number | null
  /** 決定的タイブレーク用の補助値（通常は売上金額）。大きいほど上位。 */
  tieBreak: number
}

/**
 * 値で順位付けし、キー→順位（1..n）の Map を返す。
 * direction 'higher' は降順、'lower' は昇順。null 値は順位対象外。
 * 同値は補助値（降順）→キー（昇順）で決定的に並べ、連番順位を割り当てる。
 */
export function assignRanks(
  rows: readonly SortableRow[],
  direction: 'higher' | 'lower',
): Map<string, number> {
  const ranked = rows
    .filter((r) => r.value !== null)
    .slice()
    .sort((a, b) => {
      const av = a.value as number
      const bv = b.value as number
      if (av !== bv) return direction === 'lower' ? av - bv : bv - av
      if (a.tieBreak !== b.tieBreak) return b.tieBreak - a.tieBreak
      return a.key < b.key ? -1 : a.key > b.key ? 1 : 0
    })
  const map = new Map<string, number>()
  ranked.forEach((row, index) => map.set(row.key, index + 1))
  return map
}

/** ABC ランク。 */
export type AbcTier = 'A' | 'B' | 'C'

/** 構成比・累積構成比・ABC ランクの算出結果。 */
export interface AbcResult {
  /** キー→構成比（%）。 */
  share: Map<string, number>
  /** キー→累積構成比（%、当該行を含む）。 */
  cumulative: Map<string, number>
  /** キー→ABC ランク。 */
  tier: Map<string, AbcTier>
  /** 基準指標の総和。 */
  total: number
}

/**
 * 構成比・累積構成比・ABC ランクを算出する（パレート分析）。
 * metric は合算可能指標（売上金額・数量・粗利・在庫数）を想定。
 * thresholdA / thresholdB は A / B の上限累積%（既定 70 / 90）。累積が閾値を跨ぐ行は
 * 跨ぐ前の累積が閾値未満であれば下位文字（A 寄り）に含める（標準的な ABC 分類）。
 */
export function computeAbc(
  rows: readonly PeriodRow[],
  metric: RankingMetricKey,
  thresholdA: number,
  thresholdB: number,
): AbcResult {
  const entries = rows
    .map((r) => ({ key: r.key, value: metricRawValue(r.values, metric) ?? 0 }))
    .filter((e) => e.value > 0)
    .sort((a, b) => b.value - a.value)

  const total = entries.reduce((sum, e) => sum + e.value, 0)
  const share = new Map<string, number>()
  const cumulative = new Map<string, number>()
  const tier = new Map<string, AbcTier>()

  let cumBefore = 0
  for (const e of entries) {
    const sharePct = total > 0 ? (e.value / total) * 100 : 0
    const t: AbcTier = cumBefore < thresholdA ? 'A' : cumBefore < thresholdB ? 'B' : 'C'
    cumBefore += sharePct
    share.set(e.key, sharePct)
    cumulative.set(e.key, cumBefore)
    tier.set(e.key, t)
  }
  return { share, cumulative, tier, total }
}

/** 成長率（%）。当期・前期いずれかが null、または前期が 0 なら算出不能で null。 */
export function growthPercent(current: number | null, previous: number | null): number | null {
  if (current === null || previous === null || previous === 0) {
    return null
  }
  return ((current - previous) / Math.abs(previous)) * 100
}

/**
 * 表示用に集約したランキング1行（順位・複合スコア・構成比・ABC・順位変動・成長率を含む）。
 * テーブル・スロープチャートはこの型を共有する。当期に存在する行のみ生成するため
 * <see cref="values"/> は非 null。
 */
export interface RankingViewRow {
  key: string
  label: string
  /** 今期順位（1始まり）。 */
  rank: number
  /** 当期の集計値。 */
  values: RankingMetricValues
  /** 複合スコア（0..1）。複合スコア並び替え時のみ意味を持つ（それ以外は null）。 */
  compositeScore: number | null
  /** 構成比（%、基準指標）。 */
  share: number
  /** 累積構成比（%、基準指標）。 */
  cumulative: number
  /** ABC ランク。 */
  tier: AbcTier
  /** 前期順位（比較なし／新規・圏外は null）。 */
  prevRank: number | null
  /** 順位変動（前期順位 − 今期順位。正=上昇）。算出不能は null。 */
  rankDelta: number | null
  /** 新規（比較期間に存在しない）。 */
  isNew: boolean
  /** 比較期間の集計値（比較なし／比較期間に存在しないは null）。 */
  comparisonValues: RankingMetricValues | null
  /** 成長率（%、基準指標）。算出不能は null。 */
  growth: number | null
}

/** パレート図の1棒（基準指標値 + 累積構成比 + ABC ランク）。 */
export interface ParetoBar {
  label: string
  value: number
  cumulative: number
  tier: AbcTier
}

/** 順位変動スロープの1アイテム。 */
export interface MoverItem {
  key: string
  label: string
  /** 今期順位（1始まり）。 */
  rank: number
  /** 前期順位（1始まり）。新規・圏外は null。 */
  prevRank: number | null
  /** 新規（比較期間に存在しない）か。 */
  isNew: boolean
  /** 順位変動（前期順位 − 今期順位。正=上昇）。算出不能は null。 */
  delta: number | null
}
