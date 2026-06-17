/**
 * 全社サマリー（AIレポート風）の所見をルールベースで生成する純粋ロジック。
 *
 * undeux のバックエンドに LLM 基盤が無いため、主要指標（KPI・前年同期比・週次モメンタム・
 * 内訳の集中度）から定型の所見（エグゼクティブサマリー＋強み/弱み/機会/リスク）を機械的に
 * 組み立てる。Vue 非依存の純粋関数のみで構成する（テスト容易性・再利用性。utils/ranking と同じ思想）。
 *
 * 判定閾値（HIGH_GROSS_MARGIN 等）は本ファイルに集約する（SoT）。
 */
import type { BreakdownRow, MartKpi, TrendPoint } from '~/types/api'
import { formatCurrency, formatNumber, formatRatioAsPercent } from './format'

/** 強み・弱み・機会・脅威（リスク）の4観点。 */
export interface ExecutiveSwot {
  strengths: string[]
  weaknesses: string[]
  opportunities: string[]
  threats: string[]
}

/** ルールベースで生成したエグゼクティブサマリー。 */
export interface ExecutiveSummary {
  /** Hero に出す1文の結論（ボトムライン）。 */
  bottomLine: string
  /** 箇条書きの要点。 */
  highlights: string[]
  /** 観点別の所見。 */
  swot: ExecutiveSwot
}

/** エグゼクティブサマリー生成の入力。 */
export interface ExecutiveSummaryInput {
  kpi: MartKpi
  /** 前年同期の KPI（YoY 算出用。無ければ null）。 */
  previousKpi: MartKpi | null
  /** 週次トレンド（直近モメンタム算出用）。 */
  trend: TrendPoint[]
  /** 集計軸別の内訳（最大構成・集中度算出用）。 */
  breakdown: BreakdownRow[]
  /** 内訳の集計軸ラベル（例: 「部門」）。 */
  breakdownDimensionLabel: string
}

// 判定閾値（ルールベースの基準。SoT としてここに集約）。
const HIGH_GROSS_MARGIN = 0.4
const LOW_GROSS_MARGIN = 0.2
const GOOD_SELL_THROUGH = 0.6
const LOW_SELL_THROUGH = 0.3
const HIGH_CONCENTRATION = 0.5 // 最大構成区分が全体の50%超
const MOMENTUM_EPSILON = 0.05 // ±5%未満は横ばい扱い

/** 相対成長率（cur vs prev）。prev が無い／0 なら null。 */
function relativeGrowth(cur: number, prev: number | null | undefined): number | null {
  if (prev === null || prev === undefined || prev === 0) return null
  return (cur - prev) / Math.abs(prev)
}

/** 符号付きパーセント表記（比率 → "+12.3%"）。 */
function signedPercent(ratio: number): string {
  const pct = ratio * 100
  const sign = pct > 0 ? '+' : pct < 0 ? '−' : '±'
  return `${sign}${Math.abs(pct).toFixed(1)}%`
}

/** 週次トレンドの直近モメンタム（後半平均 vs 前半平均の相対差）。点が少ない場合 null。 */
function trendMomentum(trend: TrendPoint[]): number | null {
  if (trend.length < 4) return null
  const mid = Math.floor(trend.length / 2)
  const avg = (xs: TrendPoint[]): number =>
    xs.length === 0 ? 0 : xs.reduce((sum, p) => sum + p.amount, 0) / xs.length
  const earlier = avg(trend.slice(0, mid))
  const recent = avg(trend.slice(mid))
  if (earlier === 0) return null
  return (recent - earlier) / Math.abs(earlier)
}

/**
 * 主要指標から所見をルールベースで組み立てる。
 * 各観点は該当シグナルが無ければ中立のフォールバック文を1件入れ、UI が空にならないようにする。
 */
export function buildExecutiveSummary(input: ExecutiveSummaryInput): ExecutiveSummary {
  const { kpi, previousKpi, trend, breakdown, breakdownDimensionLabel } = input

  const growth = relativeGrowth(kpi.amount, previousKpi?.amount ?? null)
  const grossGrowth = relativeGrowth(kpi.grossProfit, previousKpi?.grossProfit ?? null)
  const momentum = trendMomentum(trend)

  // 最大構成区分と集中度（売上金額ベース）。
  const sorted = breakdown.slice().sort((a, b) => b.amount - a.amount)
  const top = sorted[0] ?? null
  const totalAmount = sorted.reduce((sum, r) => sum + r.amount, 0)
  const topShare = top && totalAmount > 0 ? top.amount / totalAmount : null

  // ---- ボトムライン（1文の結論） ----
  const bottomLineParts: string[] = [`売上 ${formatCurrency(kpi.amount)}`]
  if (growth !== null) bottomLineParts.push(`前年同期比 ${signedPercent(growth)}`)
  bottomLineParts.push(`粗利率 ${formatRatioAsPercent(kpi.grossProfitRate)}`)
  bottomLineParts.push(`消化率 ${formatRatioAsPercent(kpi.sellThroughRate)}`)
  const bottomLine = `${bottomLineParts.join(' ／ ')}。`

  // ---- ハイライト（要点） ----
  const highlights: string[] = []
  highlights.push(
    `売上金額は ${formatCurrency(kpi.amount)}${growth !== null ? `（前年同期比 ${signedPercent(growth)}）` : '（前年同期データなし）'}。`,
  )
  highlights.push(
    `粗利は ${formatCurrency(kpi.grossProfit)}（粗利率 ${formatRatioAsPercent(kpi.grossProfitRate)}）${grossGrowth !== null ? `、前年同期比 ${signedPercent(grossGrowth)}` : ''}。`,
  )
  highlights.push(
    `消化率は ${formatRatioAsPercent(kpi.sellThroughRate)}、在庫数は ${formatNumber(kpi.currentStock)} 点。`,
  )
  if (top && topShare !== null) {
    highlights.push(
      `${breakdownDimensionLabel}別では「${top.label}」が売上構成の ${formatRatioAsPercent(topShare)} を占め最大です。`,
    )
  }
  if (momentum !== null) {
    const trendWord = momentum > MOMENTUM_EPSILON ? '増勢' : momentum < -MOMENTUM_EPSILON ? '減勢' : '横ばい'
    highlights.push(`週次売上は期間後半が前半比 ${signedPercent(momentum)} で、直近は${trendWord}傾向です。`)
  }

  // ---- SWOT（観点別所見） ----
  const strengths: string[] = []
  const weaknesses: string[] = []
  const opportunities: string[] = []
  const threats: string[] = []

  // 粗利率
  if (kpi.grossProfitRate >= HIGH_GROSS_MARGIN) {
    strengths.push(`高い粗利率（${formatRatioAsPercent(kpi.grossProfitRate)}）で収益性が良好。`)
  } else if (kpi.grossProfitRate < LOW_GROSS_MARGIN) {
    weaknesses.push(`粗利率が低め（${formatRatioAsPercent(kpi.grossProfitRate)}）。値引き・原価構成の見直し余地。`)
  }

  // 消化率
  if (kpi.sellThroughRate >= GOOD_SELL_THROUGH) {
    strengths.push(`良好な消化率（${formatRatioAsPercent(kpi.sellThroughRate)}）で在庫が順調に捌けている。`)
    opportunities.push('消化が速い区分は追加供給・横展開で機会を取り込める。')
  } else if (kpi.sellThroughRate < LOW_SELL_THROUGH) {
    weaknesses.push(`消化率が低め（${formatRatioAsPercent(kpi.sellThroughRate)}）で在庫が滞留しやすい。`)
    threats.push('低消化は値下げ・滞留在庫の増加につながるリスク。')
  }

  // 前年比成長
  if (growth !== null) {
    if (growth > 0) {
      strengths.push(`前年同期比プラス成長（${signedPercent(growth)}）。`)
    } else if (growth < 0) {
      weaknesses.push(`前年同期比マイナス（${signedPercent(growth)}）。要因分析が必要。`)
      threats.push('前年割れの継続は売上基盤の縮小リスク。')
    }
  }

  // 週次モメンタム
  if (momentum !== null) {
    if (momentum > MOMENTUM_EPSILON) {
      opportunities.push(`直近の増勢（${signedPercent(momentum)}）を捉え、在庫・販促を厚くする好機。`)
    } else if (momentum < -MOMENTUM_EPSILON) {
      threats.push(`直近の減勢（${signedPercent(momentum)}）。失速要因の早期把握が必要。`)
    }
  }

  // 集中度
  if (top && topShare !== null && topShare >= HIGH_CONCENTRATION) {
    opportunities.push(`「${top.label}」への集中（${formatRatioAsPercent(topShare)}）。他${breakdownDimensionLabel}への横展開余地。`)
    threats.push(`「${top.label}」依存（${formatRatioAsPercent(topShare)}）。単一区分の不振が全体に波及するリスク。`)
  }

  // 各観点のフォールバック（空表示を避ける）。
  if (strengths.length === 0) strengths.push('主要指標は平均的で、突出した強みは検出されませんでした。')
  if (weaknesses.length === 0) weaknesses.push('主要指標に明確な弱みは検出されませんでした。')
  if (opportunities.length === 0) opportunities.push('指標の偏りが小さく、特筆すべき機会は検出されませんでした。')
  if (threats.length === 0) threats.push('指標上の差し迫ったリスクは検出されませんでした。')

  return {
    bottomLine,
    highlights,
    swot: { strengths, weaknesses, opportunities, threats },
  }
}
