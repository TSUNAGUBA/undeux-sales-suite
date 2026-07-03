<script setup lang="ts">
/**
 * 全社サマリー（/mart）— AIレポート風の経営サマリー。
 *
 * tokutake-ai-platform の「AIレポート」の構成（Hero ボトムライン → KPI → エグゼクティブ
 * サマリー＋観点別所見(SWOT) → 売上構成 → 週次推移）に倣う。undeux は明るいテーマのため、
 * 視覚言語は既存（slate/indigo）を踏襲しつつ、レポート的な情報設計を採用する。
 *
 * 絞り込みは「業態タブ（＝AIレポートのセグメント）＋ 部門チップ（＝サブセグメント）」を主軸とし、
 * いずれも「すべて」を持つ単一選択。期間は年度で指定する。選択は共有フィルタ state
 * （'mart-filter'）の businessTypes/departments/year に単一要素として反映し、他ページへの
 * ドリル時にも絞り込みが引き継がれる。
 *
 * エグゼクティブサマリー（要点＋SWOT）は LLM ではなく主要指標からルールベースで自動生成する
 * （undeux にLLM基盤が無いため。生成ロジックは utils/executiveSummary が SoT）。
 */
import {
  AlertTriangle,
  Boxes,
  CircleDollarSign,
  Database,
  Gauge,
  Layers,
  Lightbulb,
  Package,
  Percent,
  RefreshCw,
  ShieldAlert,
  ShoppingCart,
  Sparkles,
  ThumbsUp,
  TrendingUp,
} from 'lucide-vue-next'
import type { Component } from 'vue'
import type {
  BreakdownRow,
  InventoryActionsResponse,
  InventoryActionTargetTab,
  KpiCardItem,
  MartBreakdownResponse,
  MartSummaryResponse,
  TrendPoint,
} from '~/types/api'
import type { ExecutiveSummary, MonthlyComparison, YoYPair } from '~/utils/executiveSummary'

useHead({ title: '全社サマリー（月次） | UndeuxSales' })

// mart 専用のフィルタスコープ。既存 sales 系（'sales-filter'）とは分離する。
const MART_SCOPE = 'mart-filter'
const { filter, options, optionsError, loadOptions, toQuery, years } = useFilters(MART_SCOPE)
const { get } = useApi()
const { status, isBuilt, rebuilding, rebuildMessage, refreshStatus, rebuild } = useMart()

const summary = ref<MartSummaryResponse | null>(null)
// 前年同期（YoY 算出用。補助情報のため取得失敗時は null）。
const summaryPrev = ref<MartSummaryResponse | null>(null)
const breakdown = ref<MartBreakdownResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const dimension = ref('department')
const metric = ref('amount')

const dimensionOptions = [
  { value: 'department', label: '部門別' },
  { value: 'businessType', label: '業態別' },
  { value: 'season', label: '季節別' },
  { value: 'product', label: '品番CD（服種）別' },
  { value: 'brand', label: 'ブランド別' },
]

const metricOptions = [
  { value: 'amount', label: '売上金額' },
  { value: 'quantity', label: '売上数量' },
  { value: 'grossProfit', label: '粗利' },
]

const metricLabel = computed(
  () => metricOptions.find((item) => item.value === metric.value)?.label ?? '売上金額',
)
/** 内訳の集計軸ラベル（「○○別」から「別」を除いた表記。所見・見出しで使う）。 */
const dimensionLabel = computed(
  () => (dimensionOptions.find((d) => d.value === dimension.value)?.label ?? '部門別').replace(/別$/, ''),
)

function metricValue(row: BreakdownRow): number {
  if (metric.value === 'quantity') return row.quantity
  if (metric.value === 'grossProfit') return row.grossProfit
  return row.amount
}

// ---------------------------------------------------------------
// 絞り込み: 業態タブ（セグメント）＋ 部門チップ（サブセグメント）＋ 年度。
// 単一選択を共有フィルタの配列（businessTypes/departments）に単一要素で反映する。
// ---------------------------------------------------------------
const activeBusinessType = computed(() => filter.value.businessTypes[0] ?? null)
const activeDepartment = computed(() => filter.value.departments[0] ?? null)

function businessTypeLabel(code: string): string {
  const opt = options.value?.businessTypes.find((b) => b.code === code)
  return opt?.shortName ?? opt?.name ?? code
}
function departmentLabel(code: string): string {
  const opt = options.value?.departments.find((d) => d.code === code)
  return opt?.name ?? code
}

const scopeLabel = computed(() => {
  const bt = activeBusinessType.value ? businessTypeLabel(activeBusinessType.value) : '全業態'
  const dept = activeDepartment.value ? departmentLabel(activeDepartment.value) : '全部門'
  return `${bt} × ${dept}`
})

// 業態/部門は共通の ScopeFilterTags（全社サマリー標準・単一選択）から受け取る。
function onBusinessTypesChange(codes: string[]): void {
  filter.value.businessTypes = codes
  void load()
}
function onDepartmentsChange(codes: string[]): void {
  filter.value.departments = codes
  void load()
}

// ---------------------------------------------------------------
// 前年同月比（締まった直近月 vs 前年同月）
// 期間（年度）で見ると当年トレンドに未来（データ無し）月度が混じり、期間全体どうしの
// 前年同期比は当年が過小に見える。そこで「締まった直近月」を1つ選び前年同月と比較する。
// ---------------------------------------------------------------
const DELTA_DISPLAY_EPSILON = 0.05

interface MonthlyTotal {
  amount: number
  quantity: number
  grossProfit: number
}

/** 週次トレンドを月別合計（12要素・金額/数量/粗利）へ集計する。 */
function monthlyTotals(trend: TrendPoint[] | undefined): MonthlyTotal[] {
  const arr: MonthlyTotal[] = Array.from({ length: 12 }, () => ({ amount: 0, quantity: 0, grossProfit: 0 }))
  for (const p of trend ?? []) {
    const m = Number.parseInt(p.date.slice(5, 7), 10)
    if (m >= 1 && m <= 12) {
      const t = arr[m - 1]!
      t.amount += p.amount
      t.quantity += p.quantity
      t.grossProfit += p.grossProfit
    }
  }
  return arr
}

/** トレンドに含まれる最新のデータ月（0始まり）。データ無しは -1。 */
function latestDataMonthIndex(trend: TrendPoint[] | undefined): number {
  let max = -1
  for (const p of trend ?? []) {
    const m = Number.parseInt(p.date.slice(5, 7), 10)
    if (m >= 1 && m <= 12 && m - 1 > max) max = m - 1
  }
  return max
}

/**
 * 締まった直近月（0始まり）。最新データ月は取込途中で不完全になり得るため、その1つ前
 * （＝確実に締まった前月）を比較対象にする。最新データ月が1月なら締まった前月は無い（-1）。
 */
const closedMonthIndex = computed<number>(() => latestDataMonthIndex(summary.value?.weeklyTrend) - 1)

/** 締まった直近月の当年・前年 KPI ペア（前年トレンドが揃う年度選択時のみ）。 */
const monthlyYoY = computed<{ monthLabel: string; amount: YoYPair; quantity: YoYPair; grossProfit: YoYPair } | null>(() => {
  const idx = closedMonthIndex.value
  if (idx < 0 || filter.value.year === null || !summaryPrev.value) return null
  const cur = monthlyTotals(summary.value?.weeklyTrend)[idx]
  const prev = monthlyTotals(summaryPrev.value.weeklyTrend)[idx]
  // 締まった直近月に当年売上が無い（データ欠損の月間ギャップ等）場合は、−100% の誤表示を避けるため
  // 比較不能として null にする（当月の売上が真に 0 のときも比較を出さない方が誤解を招かない）。
  if (!cur || !prev || cur.amount <= 0) return null
  // 前年が 0（＝前年同月にデータ無し）の指標は比較不能として previous=null にする。
  const pair = (c: number, p: number): YoYPair => ({ current: c, previous: p || null })
  return {
    monthLabel: MONTH_LABELS[idx] ?? `${idx + 1}月`,
    amount: pair(cur.amount, prev.amount),
    quantity: pair(cur.quantity, prev.quantity),
    grossProfit: pair(cur.grossProfit, prev.grossProfit),
  }
})

/** エグゼクティブサマリーへ渡す前年同月比素材（金額・粗利）。 */
const monthlyComparison = computed<MonthlyComparison | null>(() => {
  const my = monthlyYoY.value
  if (!my) return null
  return { monthLabel: my.monthLabel, amount: my.amount, grossProfit: my.grossProfit }
})

/** 前年同月比（相対%）の表示テキスト。前年データが無い／0 なら表示しない。 */
function monthlyYoYText(pair: YoYPair, monthLabel: string): string | undefined {
  if (pair.previous === null || pair.previous === 0) return undefined
  const pct = ((pair.current - pair.previous) / Math.abs(pair.previous)) * 100
  if (Math.abs(pct) < DELTA_DISPLAY_EPSILON) return `${monthLabel}度 前年同月比 ±0.0%`
  return `${monthLabel}度 前年同月比 ${pct > 0 ? '+' : '−'}${Math.abs(pct).toFixed(1)}%`
}

// ---------------------------------------------------------------
// 同月まで累計（当期 vs 前期）
// 当期と前期それぞれの「1月〜締まった直近月」の累計（数量・金額）を並べて比較する。
// 単月 YoY（monthlyYoY）と同じく締まった直近月（closedMonthIndex）を基準にし、当年の未確定月
// （取込途中）を含めない。締まった直近月が無い（1月データのみ等）場合は、部分月 vs 満了月の
// 不公平な比較（−100% 近傍の誤表示）を避けるため、単月 YoY と同様に比較を出さない（null）。
// ---------------------------------------------------------------
/** 当期・前期の同月まで累計（数量・金額）。年度選択かつ前年トレンドが揃い、締まった直近月があるときのみ。 */
const cumulativeYoY = computed<{ monthLabel: string; amount: YoYPair; quantity: YoYPair } | null>(() => {
  const idx = closedMonthIndex.value
  if (idx < 0 || filter.value.year === null || !summaryPrev.value) return null
  const cur = monthlyTotals(summary.value?.weeklyTrend)
  const prev = monthlyTotals(summaryPrev.value.weeklyTrend)
  let curAmt = 0, curQty = 0, prevAmt = 0, prevQty = 0
  for (let m = 0; m <= idx; m++) {
    curAmt += cur[m]!.amount
    curQty += cur[m]!.quantity
    prevAmt += prev[m]!.amount
    prevQty += prev[m]!.quantity
  }
  // 当期累計が 0（データ欠損）なら −100% の誤表示を避けるため比較を出さない。
  if (curAmt <= 0) return null
  const pair = (c: number, p: number): YoYPair => ({ current: c, previous: p || null })
  return {
    monthLabel: MONTH_LABELS[idx] ?? `${idx + 1}月`,
    amount: pair(curAmt, prevAmt),
    quantity: pair(curQty, prevQty),
  }
})

/** 同月まで累計カードの表示行（金額・数量。当期/前期のバー幅と前年比を持つ）。 */
const cumulativeRows = computed(() => {
  const c = cumulativeYoY.value
  if (!c) return []
  const build = (label: string, pair: YoYPair, fmt: (n: number) => string) => {
    const prev = pair.previous ?? 0
    const max = Math.max(pair.current, prev, 1)
    const pct = pair.previous && pair.previous !== 0
      ? ((pair.current - pair.previous) / Math.abs(pair.previous)) * 100
      : null
    let yoyText: string | undefined
    let yoyClass = 'text-slate-400'
    if (pct !== null) {
      if (Math.abs(pct) < DELTA_DISPLAY_EPSILON) {
        yoyText = '前年比 ±0.0%'
      } else {
        yoyText = `前年比 ${pct > 0 ? '+' : '−'}${Math.abs(pct).toFixed(1)}%`
        yoyClass = pct > 0 ? 'text-emerald-600' : 'text-rose-600'
      }
    }
    return {
      label,
      currentText: fmt(pair.current),
      previousText: pair.previous === null ? '—' : fmt(pair.previous),
      currentPct: (pair.current / max) * 100,
      previousPct: (prev / max) * 100,
      yoyText,
      yoyClass,
    }
  }
  return [
    build('売上金額', c.amount, formatCurrency),
    build('売上数量', c.quantity, (n) => `${formatNumber(n)} 点`),
  ]
})

const kpiItems = computed<KpiCardItem[]>(() => {
  const kpi = summary.value?.kpi
  if (!kpi) return []
  const my = monthlyYoY.value
  return [
    {
      label: '売上金額',
      value: formatCurrency(kpi.amount),
      icon: CircleDollarSign,
      accentClass: 'bg-indigo-50 text-indigo-600',
      sub: my ? monthlyYoYText(my.amount, my.monthLabel) : undefined,
    },
    {
      label: '売上数量',
      value: `${formatNumber(kpi.quantity)} 点`,
      icon: ShoppingCart,
      accentClass: 'bg-sky-50 text-sky-600',
      sub: my ? monthlyYoYText(my.quantity, my.monthLabel) : undefined,
    },
    {
      label: '粗利',
      value: formatCurrency(kpi.grossProfit),
      icon: TrendingUp,
      accentClass: 'bg-emerald-50 text-emerald-600',
      sub: my ? monthlyYoYText(my.grossProfit, my.monthLabel) : undefined,
    },
    {
      label: '粗利率',
      value: formatRatioAsPercent(kpi.grossProfitRate),
      icon: Percent,
      accentClass: 'bg-amber-50 text-amber-600',
    },
    {
      label: '在庫数',
      value: `${formatNumber(kpi.currentStock)} 点`,
      icon: Boxes,
      accentClass: 'bg-rose-50 text-rose-600',
    },
    {
      label: '消化率',
      value: formatRatioAsPercent(kpi.sellThroughRate),
      icon: Gauge,
      accentClass: 'bg-teal-50 text-teal-600',
    },
    {
      label: '商品数',
      value: `${formatNumber(kpi.productCount)} 品`,
      icon: Package,
      accentClass: 'bg-violet-50 text-violet-600',
    },
    {
      label: 'SKU数',
      value: `${formatNumber(kpi.skuCount)} 件`,
      icon: Layers,
      accentClass: 'bg-cyan-50 text-cyan-600',
    },
  ]
})

// ---------------------------------------------------------------
// エグゼクティブサマリー（ルールベース。utils/executiveSummary）
// ---------------------------------------------------------------
const execSummary = computed<ExecutiveSummary | null>(() => {
  const s = summary.value
  if (!s) return null
  return buildExecutiveSummary({
    kpi: s.kpi,
    previousKpi: summaryPrev.value?.kpi ?? null,
    monthlyComparison: monthlyComparison.value,
    trend: s.weeklyTrend,
    breakdown: breakdown.value?.rows ?? [],
    breakdownDimensionLabel: dimensionLabel.value,
  })
})

interface SwotPanel {
  key: string
  title: string
  icon: Component
  items: string[]
  cardClass: string
  iconClass: string
}
const swotPanels = computed<SwotPanel[]>(() => {
  const s = execSummary.value?.swot
  if (!s) return []
  return [
    { key: 'strengths', title: '強み', icon: ThumbsUp, items: s.strengths, cardClass: 'border-emerald-200 bg-emerald-50/50', iconClass: 'text-emerald-600' },
    { key: 'weaknesses', title: '弱み', icon: AlertTriangle, items: s.weaknesses, cardClass: 'border-rose-200 bg-rose-50/50', iconClass: 'text-rose-600' },
    { key: 'opportunities', title: '機会', icon: Lightbulb, items: s.opportunities, cardClass: 'border-sky-200 bg-sky-50/50', iconClass: 'text-sky-600' },
    { key: 'threats', title: 'リスク', icon: ShieldAlert, items: s.threats, cardClass: 'border-amber-200 bg-amber-50/50', iconClass: 'text-amber-600' },
  ]
})

// ---------------------------------------------------------------
// チャート・テーブル
// ---------------------------------------------------------------
// 月次売上推移（本年度 vs 前年度・前年同月比）。週次トレンドを月へ集計して2本の曲線を並べる。
const MONTH_LABELS = ['1月', '2月', '3月', '4月', '5月', '6月', '7月', '8月', '9月', '10月', '11月', '12月']

/** 週次トレンド（TrendPoint[]）を月別売上金額（12要素）へ集計する。 */
function monthlyAmounts(trend: TrendPoint[] | undefined): number[] {
  const byMonth = new Array<number>(12).fill(0)
  for (const point of trend ?? []) {
    const month = Number.parseInt(point.date.slice(5, 7), 10)
    if (month >= 1 && month <= 12) {
      byMonth[month - 1] = (byMonth[month - 1] ?? 0) + point.amount
    }
  }
  return byMonth
}

const hasTrend = computed(() => (summary.value?.weeklyTrend?.length ?? 0) > 0)
const trendLabels = MONTH_LABELS
const trendSeries = computed(() => {
  const year = filter.value.year
  const series = [
    {
      label: year !== null ? `${year}年（本年度）` : '本期間',
      data: monthlyAmounts(summary.value?.weeklyTrend),
      color: '#4f46e5',
    },
  ]
  // 前年同月比較は年度選択時のみ。前年サマリー（補助・非ブロッキング取得）が揃ったら2本目を追加。
  if (year !== null && summaryPrev.value) {
    series.push({
      label: `${year - 1}年（前年度）`,
      data: monthlyAmounts(summaryPrev.value.weeklyTrend),
      color: '#94a3b8',
    })
  }
  return series
})

const breakdownLabels = computed(() => (breakdown.value?.rows ?? []).map((r) => r.label))
const breakdownData = computed(() => (breakdown.value?.rows ?? []).map(metricValue))

const tableColumns = [
  { key: 'label', label: '区分', frozen: true },
  { key: 'quantity', label: '数量', align: 'right' as const, format: (row: BreakdownRow) => formatNumber(row.quantity) },
  { key: 'amount', label: '売上金額', align: 'right' as const, format: (row: BreakdownRow) => formatCurrency(row.amount) },
  { key: 'grossProfit', label: '粗利', align: 'right' as const, format: (row: BreakdownRow) => formatCurrency(row.grossProfit) },
  { key: 'sharePercent', label: '構成比', align: 'right' as const, format: (row: BreakdownRow) => formatPercent(row.sharePercent) },
]

// 在庫アクションダイジェスト（今週のアクション）。補助コンテンツのため、取得失敗・遅延が
// サマリー本体の表示をブロックしないよう load() からは待たずに起動する。
const inventoryActions = ref<InventoryActionsResponse | null>(null)
const inventoryActionsFailed = ref(false)
let inventoryDigestRequestSeq = 0

async function loadInventoryDigest(): Promise<void> {
  const seq = ++inventoryDigestRequestSeq
  inventoryActionsFailed.value = false
  try {
    const response = await get<InventoryActionsResponse>('/api/mart/inventory/actions', toQuery())
    if (seq !== inventoryDigestRequestSeq) return
    inventoryActions.value = response
  } catch (error) {
    if (seq !== inventoryDigestRequestSeq) return
    console.error('[mart] 在庫アクションダイジェストの取得に失敗しました:', error)
    inventoryActions.value = null
    inventoryActionsFailed.value = true
  }
}

/** ダイジェストからの遷移は push（戻るでサマリーへ帰れる）。既定タブは素の URL に正規化する。 */
function handleDigestNavigate(tab: InventoryActionTargetTab): void {
  void navigateTo(tab === 'dashboard' ? '/mart/inventory' : { path: '/mart/inventory', query: { tab } })
}

// 前年同期サマリー（YoY）。期間（年度）指定時のみ。補助情報のため非ブロッキング＋世代ガード。
let prevSeq = 0
async function loadPreviousYear(): Promise<void> {
  const seq = ++prevSeq
  const year = filter.value.year
  if (year === null) {
    summaryPrev.value = null
    return
  }
  try {
    const prevQuery = { ...toQuery(), from: `${year - 1}-01-01`, to: `${year - 1}-12-31` }
    const response = await get<MartSummaryResponse>('/api/mart/summary', prevQuery)
    if (seq !== prevSeq) return
    summaryPrev.value = response
  } catch (error) {
    if (seq !== prevSeq) return
    // YoY は補助。失敗してもサマリー本体は止めない（前年比を出さないだけ）。
    console.error('[mart] 前年同期サマリーの取得に失敗しました:', error)
    summaryPrev.value = null
  }
}

// 業態タブ・部門チップ・年度・集計軸の連続変更で古い応答が後着しても上書きしないリクエスト世代。
let summaryLoadSeq = 0

async function load(): Promise<void> {
  const seq = ++summaryLoadSeq
  loading.value = true
  errorMessage.value = null
  try {
    await refreshStatus()
    if (seq !== summaryLoadSeq) return
    if (!isBuilt.value) {
      summary.value = null
      summaryPrev.value = null
      breakdown.value = null
      inventoryActions.value = null
      return
    }
    const query = toQuery()
    void loadInventoryDigest()
    const [summaryResult, breakdownResult] = await Promise.all([
      get<MartSummaryResponse>('/api/mart/summary', query),
      get<MartBreakdownResponse>('/api/mart/breakdown', {
        ...query,
        dimension: dimension.value,
        metric: metric.value,
        limit: 15,
      }),
    ])
    if (seq !== summaryLoadSeq) return
    summary.value = summaryResult
    breakdown.value = breakdownResult
    // YoY は補助表示のため非ブロッキングで取得（本体の表示を待たせない）。
    void loadPreviousYear()
  } catch (error) {
    if (seq === summaryLoadSeq) {
      errorMessage.value = apiErrorMessage(error)
    }
  } finally {
    if (seq === summaryLoadSeq) {
      loading.value = false
    }
  }
}

async function handleRebuild(): Promise<void> {
  errorMessage.value = null
  const error = await rebuild(load)
  if (error) {
    errorMessage.value = error
  }
}

onMounted(async () => {
  await loadOptions()
  await load()
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">全社サマリー（月次）</h1>
      <p class="text-sm text-slate-500">
        業態・部門を選んで、主要指標とルールベースの所見（エグゼクティブサマリー）を月次・前年同月比でレポート確認します。
        分析用ディメンショナルモデル（mart）から集計しています。
      </p>
    </div>

    <!-- mart 構築状態と再構築 -->
    <div class="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-200 bg-white p-3">
      <div class="flex items-center gap-2 text-sm text-slate-600">
        <Database class="h-4 w-4 shrink-0 text-indigo-500" />
        <span v-if="status?.status === 'running'" class="font-medium text-indigo-600">
          再構築中...（完了まで数分かかる場合があります）
        </span>
        <span v-else-if="status?.rebuiltAt">
          最終再構築: {{ formatDateTime(status.rebuiltAt) }}
          ／ ファクト {{ formatNumber(status.factRows) }} 行
          <template v-if="status.earliestWeek && status.latestWeek">
            （{{ status.earliestWeek }} 〜 {{ status.latestWeek }}）
          </template>
        </span>
        <span v-else class="text-slate-400">mart は未構築です</span>
      </div>
      <button
        type="button"
        class="inline-flex items-center gap-2 rounded-lg bg-indigo-600 px-3 py-1.5 text-sm font-medium text-white transition-colors hover:bg-indigo-500 disabled:opacity-50"
        :disabled="rebuilding"
        @click="handleRebuild"
      >
        <RefreshCw class="h-4 w-4" :class="rebuilding ? 'animate-spin' : ''" />
        {{ rebuilding ? '再構築中...' : 'mart を再構築' }}
      </button>
    </div>
    <p v-if="rebuildMessage" class="text-xs text-emerald-600">{{ rebuildMessage }}</p>

    <p v-if="optionsError" class="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700">
      フィルタ選択肢の取得に失敗しました: {{ optionsError }}
    </p>

    <!-- 業態（タグ）＋ 部門（タグ）＋ 期間（年度）。全社サマリーの標準フィルタ（ScopeFilterTags／単一選択）。 -->
    <ScopeFilterTags
      :business-types="options?.businessTypes ?? []"
      :departments="options?.departments ?? []"
      :selected-business-types="filter.businessTypes"
      :selected-departments="filter.departments"
      @update:selected-business-types="onBusinessTypesChange"
      @update:selected-departments="onDepartmentsChange"
    >
      <template #trailing>
        <label class="text-xs font-medium text-slate-400">期間（年度）</label>
        <select
          v-model="filter.year"
          class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm"
          @change="load"
        >
          <option :value="null">全期間</option>
          <option v-for="y in years" :key="y" :value="y">{{ y }}年</option>
        </select>
      </template>
    </ScopeFilterTags>

    <StatusBlock :loading="loading" :error="errorMessage">
      <div
        v-if="!isBuilt"
        class="rounded-xl border border-amber-200 bg-amber-50 p-8 text-center text-sm text-amber-700"
      >
        mart がまだ構築されていません。上の「mart を再構築」を実行すると、スタースキーマ集計が表示されます。
      </div>
      <div v-else class="space-y-4">
        <!-- Hero: ボトムライン -->
        <div class="rounded-xl border border-indigo-200 bg-gradient-to-br from-indigo-50 to-white p-5">
          <p class="text-xs font-semibold uppercase tracking-wider text-indigo-500">エグゼクティブサマリー</p>
          <h2 class="mt-1 text-lg font-bold text-slate-800">
            {{ scopeLabel }}・{{ filter.year !== null ? `${filter.year}年` : '全期間' }} の販売サマリー
          </h2>
          <p v-if="execSummary" class="mt-2 text-sm text-slate-700">{{ execSummary.bottomLine }}</p>
          <p class="mt-1 text-xs text-slate-400">
            主要指標から自動生成した所見です（最新取込週: {{ summary?.kpi.latestWeek ?? '—' }}）。
          </p>
        </div>

        <!-- KPI -->
        <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
          <KpiCard
            v-for="item in kpiItems"
            :key="item.label"
            :label="item.label"
            :value="item.value"
            :icon="item.icon"
            :accent-class="item.accentClass"
            :sub="item.sub"
          />
        </div>

        <!-- 要点 ＋ 観点別所見（SWOT） -->
        <div v-if="execSummary" class="grid grid-cols-1 gap-4 lg:grid-cols-3">
          <div class="rounded-xl border border-slate-200 bg-white p-4 lg:col-span-1">
            <div class="mb-2 flex items-center gap-1.5">
              <Sparkles class="h-4 w-4 text-indigo-500" />
              <h3 class="text-sm font-bold text-slate-800">要点</h3>
            </div>
            <ul class="space-y-1.5">
              <li
                v-for="(h, i) in execSummary.highlights"
                :key="i"
                class="flex gap-2 text-sm text-slate-600"
              >
                <span class="mt-1 h-1.5 w-1.5 shrink-0 rounded-full bg-indigo-400" aria-hidden="true" />
                <span>{{ h }}</span>
              </li>
            </ul>
          </div>

          <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:col-span-2">
            <div
              v-for="panel in swotPanels"
              :key="panel.key"
              class="rounded-xl border p-3"
              :class="panel.cardClass"
            >
              <div class="mb-1.5 flex items-center gap-1.5">
                <component :is="panel.icon" class="h-4 w-4" :class="panel.iconClass" />
                <h4 class="text-sm font-bold text-slate-800">{{ panel.title }}</h4>
              </div>
              <ul class="space-y-1">
                <li v-for="(item, i) in panel.items" :key="i" class="text-xs text-slate-600">
                  {{ item }}
                </li>
              </ul>
            </div>
          </div>
        </div>

        <!-- 今週のアクション（在庫ダイジェスト）。気づき → 在庫マネジメントの該当タブへの導線。 -->
        <div
          v-if="inventoryActions && inventoryActions.actions.length > 0"
          class="rounded-xl border border-slate-200 bg-white p-4"
        >
          <div class="mb-1 flex items-baseline justify-between gap-2">
            <h2 class="text-sm font-bold text-slate-800">今週のアクション（在庫）</h2>
            <NuxtLink
              to="/mart/inventory"
              class="shrink-0 text-xs font-medium text-indigo-600 hover:text-indigo-800"
            >
              在庫マネジメントで全て見る →
            </NuxtLink>
          </div>
          <InventoryActionFeed
            :actions="inventoryActions.actions"
            compact
            @navigate="handleDigestNavigate"
          />
        </div>
        <p v-else-if="inventoryActionsFailed" class="text-xs text-slate-400">
          在庫アクションの取得に失敗しました（サマリーの表示には影響ありません）。
        </p>

        <!-- 同月まで累計売上（当期 vs 前期）。数量・金額を当期/前期のバーで比較する。 -->
        <div v-if="cumulativeYoY" class="rounded-xl border border-slate-200 bg-white p-4">
          <div class="mb-3 flex items-baseline justify-between gap-2">
            <h2 class="text-sm font-bold text-slate-800">同月まで累計売上（当期 vs 前期）</h2>
            <span class="text-xs text-slate-400">1月〜{{ cumulativeYoY.monthLabel }} 累計</span>
          </div>
          <div class="space-y-3">
            <div v-for="row in cumulativeRows" :key="row.label">
              <div class="mb-1 flex items-baseline justify-between gap-2">
                <span class="text-xs font-medium text-slate-500">{{ row.label }}</span>
                <span v-if="row.yoyText" class="text-xs font-semibold" :class="row.yoyClass">
                  {{ row.yoyText }}
                </span>
              </div>
              <div class="flex items-center gap-2">
                <span class="w-8 shrink-0 text-[10px] text-slate-400">当期</span>
                <div class="h-4 flex-1 overflow-hidden rounded bg-slate-100">
                  <div class="h-4 rounded bg-indigo-500" :style="{ width: `${row.currentPct}%` }" />
                </div>
                <span class="w-32 shrink-0 text-right text-xs font-semibold tabular-nums text-slate-700">{{ row.currentText }}</span>
              </div>
              <div class="mt-1 flex items-center gap-2">
                <span class="w-8 shrink-0 text-[10px] text-slate-400">前期</span>
                <div class="h-4 flex-1 overflow-hidden rounded bg-slate-100">
                  <div class="h-4 rounded bg-slate-400" :style="{ width: `${row.previousPct}%` }" />
                </div>
                <span class="w-32 shrink-0 text-right text-xs tabular-nums text-slate-500">{{ row.previousText }}</span>
              </div>
            </div>
          </div>
          <p class="mt-2 text-xs text-slate-400">
            期間（年度）選択時に、当期と前期それぞれの1月から締まった直近月までの累計売上（数量・金額）を比較します。
          </p>
        </div>

        <!-- 月次売上推移（本年度 vs 前年度・前年同月比） -->
        <LineChartCard
          v-if="hasTrend"
          title="月次売上推移グラフ（本年度 vs 前年度・前年同月比）"
          :labels="trendLabels"
          :series="trendSeries"
        />

        <!-- 売上構成 -->
        <div class="space-y-2">
          <div class="flex flex-wrap items-end gap-3">
            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">集計軸</label>
              <select
                v-model="dimension"
                class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                @change="load"
              >
                <option v-for="opt in dimensionOptions" :key="opt.value" :value="opt.value">
                  {{ opt.label }}
                </option>
              </select>
            </div>
            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">指標</label>
              <select
                v-model="metric"
                class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                @change="load"
              >
                <option v-for="opt in metricOptions" :key="opt.value" :value="opt.value">
                  {{ opt.label }}
                </option>
              </select>
            </div>
          </div>

          <BarChartCard
            v-if="breakdownLabels.length > 0"
            :title="`${dimensionLabel}別 ${metricLabel}（上位15）`"
            :labels="breakdownLabels"
            :data="breakdownData"
            :series-label="metricLabel"
            color="#4f46e5"
            horizontal
          />

          <DataTable
            :columns="tableColumns"
            :rows="breakdown?.rows ?? []"
            :row-key="(row: BreakdownRow) => row.key"
          />

          <p
            v-if="breakdownLabels.length === 0"
            class="rounded-xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-400"
          >
            選択した条件に該当するデータがありません。
          </p>
        </div>
      </div>
    </StatusBlock>
  </div>
</template>
