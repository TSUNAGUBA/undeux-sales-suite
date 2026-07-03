<script setup lang="ts">
/**
 * 日次分析：特定品番（/mart/weekly-daily）— 週間モニタリング配下のタブ。
 *
 * 週間モニタリング（週次）に対し、特定品番に絞って「日次」で深掘りする画面。
 * 構成: 品番フィルタ → SWOT（全社サマリーと同じルールベース所見）→ 日次推移グラフ →
 * 日次明細（平均気温(東京)つき）→ 帳票区分の前週比較テーブル（品番3桁×単品4桁・在庫一覧相当）。
 *
 * データ源: /api/mart/summary・/api/mart/breakdown（SWOT用）／/api/mart/daily-series（日次）／
 * /api/mart/chohyo-transition（帳票区分の前週遷移）。フィルタスコープは 'mart-filter'。
 */
import {
  AlertTriangle,
  CircleDollarSign,
  Gauge,
  Lightbulb,
  ShieldAlert,
  ShoppingCart,
  Sparkles,
  ThumbsUp,
  TrendingUp,
} from 'lucide-vue-next'
import type { Component } from 'vue'
import type {
  ChohyoTransitionKind,
  ChohyoTransitionResponse,
  ChohyoTransitionRow,
  DailySeriesPoint,
  DailySeriesResponse,
  KpiCardItem,
  MartBreakdownResponse,
  MartIntroductionOptions,
  MartSummaryResponse,
} from '~/types/api'
import type { ComboChartAxis, ComboChartSeries } from '~/components/ComboChartCard.vue'
import type { ExecutiveSummary } from '~/utils/executiveSummary'

useHead({ title: '日次分析：特定品番 | UndeuxSales' })

const MART_SCOPE = 'mart-filter'
const { filter, toQuery, loadOptions } = useFilters(MART_SCOPE)
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()

// ---- 品番3桁フィルタ（特定品番に絞る） ----
const hinbanOptions = ref<string[]>([])
const selectedHinban = computed<string>(() => filter.value.hinbans[0] ?? '')

function onHinbanChange(event: Event): void {
  const value = (event.target as HTMLSelectElement).value
  filter.value.hinbans = value ? [value] : []
  void load()
}

async function loadHinbanOptions(): Promise<void> {
  try {
    const opts = await get<MartIntroductionOptions>('/api/mart/introduction-options')
    hinbanOptions.value = opts.hinbans
  } catch (error) {
    // 品番候補は補助。取得失敗でも本体は動かす（原則4: 非ブロッキング）。
    console.error('[weekly-daily] 品番候補の取得に失敗しました:', error)
  }
}

// ---- データ ----
const summary = ref<MartSummaryResponse | null>(null)
const breakdown = ref<MartBreakdownResponse | null>(null)
const daily = ref<DailySeriesResponse | null>(null)
const transition = ref<ChohyoTransitionResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const points = computed<DailySeriesPoint[]>(() => daily.value?.points ?? [])

// ---- SWOT（全社サマリーと同じルールベース所見を週次推移グラフの上に表示） ----
const execSummary = computed<ExecutiveSummary | null>(() => {
  const s = summary.value
  if (!s) return null
  return buildExecutiveSummary({
    kpi: s.kpi,
    previousKpi: null,
    trend: s.weeklyTrend,
    breakdown: breakdown.value?.rows ?? [],
    breakdownDimensionLabel: '単品',
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

// ---- KPI（期間合計） ----
const kpiItems = computed<KpiCardItem[]>(() => {
  const kpi = summary.value?.kpi
  if (!kpi) return []
  return [
    { label: '売上金額', value: formatCurrency(kpi.amount), icon: CircleDollarSign, accentClass: 'bg-indigo-50 text-indigo-600' },
    { label: '売上数量', value: `${formatNumber(kpi.quantity)} 点`, icon: ShoppingCart, accentClass: 'bg-sky-50 text-sky-600' },
    { label: '粗利', value: formatCurrency(kpi.grossProfit), icon: TrendingUp, accentClass: 'bg-emerald-50 text-emerald-600' },
    { label: '消化率', value: formatRatioAsPercent(kpi.sellThroughRate), icon: Gauge, accentClass: 'bg-teal-50 text-teal-600' },
  ]
})

// ---- 日次推移グラフ（売上金額/数量＝折れ線、平均気温(東京)＝折れ線） ----
const trendLabels = computed(() => points.value.map((p) => p.date))
const trendSeries = computed<ComboChartSeries[]>(() => {
  const ps = points.value
  return [
    { label: '売上数量', data: ps.map((p) => p.quantity), color: '#0ea5e9', type: 'line', yAxisId: 'y' },
    { label: '売上金額', data: ps.map((p) => p.amount), color: '#4f46e5', type: 'line', yAxisId: 'y1' },
    { label: `平均気温（${daily.value?.areaCity ?? '東京'}）`, data: ps.map((p) => p.tempAvg), color: '#dc2626', type: 'line', yAxisId: 'y2' },
  ]
})
const trendAxes: ComboChartAxis[] = [
  { id: 'y', position: 'left', label: '点（数量）' },
  { id: 'y1', position: 'right', label: '円（売上金額）', gridOff: true },
  { id: 'y2', position: 'right', label: '℃（気温）', beginAtZero: false, gridOff: true },
]

// ---- 日次明細（平均気温(東京)列を追加） ----
const dailyColumns = [
  { key: 'date', label: '日付', frozen: true },
  { key: 'amount', label: '売上金額', align: 'right' as const, format: (r: DailySeriesPoint) => formatCurrency(r.amount) },
  { key: 'quantity', label: '売上数量', align: 'right' as const, format: (r: DailySeriesPoint) => formatNumber(r.quantity) },
  { key: 'grossProfit', label: '粗利', align: 'right' as const, format: (r: DailySeriesPoint) => formatCurrency(r.grossProfit) },
  { key: 'tempAvg', label: '平均気温(東京)', align: 'right' as const, format: (r: DailySeriesPoint) => `${formatDecimal(r.tempAvg, 1)}℃` },
]
// 新しい日付が上。
const dailyRows = computed(() => [...points.value].reverse())

// ---- 帳票区分の前週比較テーブル（品番3桁×単品4桁・在庫一覧相当） ----
type TransitionTab = 'all' | 'sale-to-info' | 'info-to-sale' | 'dropped'
const transitionTab = ref<TransitionTab>('all')

/** タブ → 該当行の判定。全体＝当週に在るSKU（dropped 以外）。 */
function matchesTab(row: ChohyoTransitionRow, tab: TransitionTab): boolean {
  if (tab === 'all') return row.transition !== 'dropped'
  return row.transition === tab
}

const transitionRows = computed<ChohyoTransitionRow[]>(() =>
  (transition.value?.rows ?? []).filter((r) => matchesTab(r, transitionTab.value)),
)

function tabCount(tab: TransitionTab): number {
  return (transition.value?.rows ?? []).filter((r) => matchesTab(r, tab)).length
}

const TRANSITION_TABS: ReadonlyArray<{ key: TransitionTab; label: string }> = [
  { key: 'all', label: '全体' },
  { key: 'sale-to-info', label: '売→情' },
  { key: 'info-to-sale', label: '情→売' },
  { key: 'dropped', label: '情売→無' },
]

const KUBUN_BADGE: Partial<Record<ChohyoTransitionKind, string>> = {
  'sale-to-info': 'bg-amber-50 text-amber-700',
  'info-to-sale': 'bg-sky-50 text-sky-700',
  dropped: 'bg-rose-50 text-rose-700',
  new: 'bg-emerald-50 text-emerald-700',
}
function kubunDisplay(value: string): string {
  return value === '' ? '—' : value
}

const transitionColumns = [
  { key: 'hinbanCode', label: '品番3桁', frozen: true },
  { key: 'tanpinCode', label: '単品4桁' },
  { key: 'hinmei', label: '品名' },
  { key: 'previousKubun', label: '前週帳票区分', format: (r: ChohyoTransitionRow) => kubunDisplay(r.previousKubun) },
  { key: 'currentKubun', label: '当週帳票区分', format: (r: ChohyoTransitionRow) => kubunDisplay(r.currentKubun) },
  { key: 'quantity', label: '当週売上数量', align: 'right' as const, format: (r: ChohyoTransitionRow) => formatNumber(r.quantity) },
  { key: 'stock', label: '店頭在庫', align: 'right' as const, format: (r: ChohyoTransitionRow) => formatNumber(r.stock) },
]

function transitionRowKey(row: ChohyoTransitionRow): string {
  return `${row.gyotaiCode}|${row.shohinKigou}|${row.hinbanCode}|${row.tanpinCode}`
}

// ---- ロード（連続変更で古い応答が後着しても上書きしないリクエスト世代） ----
let loadSeq = 0
async function load(): Promise<void> {
  const seq = ++loadSeq
  loading.value = true
  errorMessage.value = null
  try {
    await refreshStatus()
    if (seq !== loadSeq) return
    if (!isBuilt.value) {
      summary.value = null
      breakdown.value = null
      daily.value = null
      transition.value = null
      return
    }
    const query = toQuery()
    const [summaryResult, breakdownResult, dailyResult, transitionResult] = await Promise.all([
      get<MartSummaryResponse>('/api/mart/summary', query),
      get<MartBreakdownResponse>('/api/mart/breakdown', { ...query, dimension: 'product', metric: 'amount', limit: 15 }),
      get<DailySeriesResponse>('/api/mart/daily-series', query),
      get<ChohyoTransitionResponse>('/api/mart/chohyo-transition', query),
    ])
    if (seq !== loadSeq) return
    summary.value = summaryResult
    breakdown.value = breakdownResult
    daily.value = dailyResult
    transition.value = transitionResult
  } catch (error) {
    if (seq === loadSeq) {
      errorMessage.value = apiErrorMessage(error)
    }
  } finally {
    if (seq === loadSeq) {
      loading.value = false
    }
  }
}

onMounted(async () => {
  await Promise.all([loadOptions(), loadHinbanOptions()])
  await load()
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">日次分析：特定品番</h1>
      <p class="text-sm text-slate-500">
        品番を絞って日次で深掘りします。SWOT・日次推移（平均気温(東京)）・日次明細に加え、
        帳票区分（売発注/情報発注）の前週比較で品番3桁×単品4桁のSKU遷移を確認します。
      </p>
    </div>

    <FilterBar :scope-key="MART_SCOPE" @apply="load" />

    <!-- 品番3桁フィルタ -->
    <div class="flex flex-wrap items-center gap-2">
      <label class="text-xs font-medium text-slate-400">品番3桁</label>
      <select
        :value="selectedHinban"
        class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm"
        @change="onHinbanChange"
      >
        <option value="">すべて</option>
        <option v-for="h in hinbanOptions" :key="h" :value="h">{{ h }}</option>
      </select>
    </div>

    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="isBuilt && points.length === 0 && (transition?.rows.length ?? 0) === 0"
      empty-message="該当するデータがありません。品番・フィルタを見直してください。"
    >
      <MartNotBuiltNotice v-if="!isBuilt" />
      <div v-else class="space-y-4">
        <!-- KPI -->
        <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
          <KpiCard
            v-for="item in kpiItems"
            :key="item.label"
            :label="item.label"
            :value="item.value"
            :icon="item.icon"
            :accent-class="item.accentClass"
          />
        </div>

        <!-- SWOT（週次推移グラフの上） -->
        <div v-if="swotPanels.length > 0" class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <div v-for="panel in swotPanels" :key="panel.key" class="rounded-xl border p-3" :class="panel.cardClass">
            <div class="mb-1.5 flex items-center gap-1.5">
              <component :is="panel.icon" class="h-4 w-4" :class="panel.iconClass" />
              <h4 class="text-sm font-bold text-slate-800">{{ panel.title }}</h4>
            </div>
            <ul class="space-y-1">
              <li v-for="(item, i) in panel.items" :key="i" class="text-xs text-slate-600">{{ item }}</li>
            </ul>
          </div>
        </div>

        <!-- 日次推移グラフ -->
        <ComboChartCard
          v-if="trendLabels.length > 0"
          title="日次推移グラフ"
          :labels="trendLabels"
          :series="trendSeries"
          :axes="trendAxes"
        />

        <!-- 日次明細（平均気温(東京)つき） -->
        <div v-if="points.length > 0" class="space-y-1">
          <h3 class="text-sm font-semibold text-slate-700">日次明細</h3>
          <p class="text-xs text-slate-400">新しい日付が上。平均気温は東京（実測優先・未カバー日は平年値）。</p>
          <DataTable :columns="dailyColumns" :rows="dailyRows" :row-key="(row: DailySeriesPoint) => row.date" />
        </div>

        <!-- 帳票区分 前週比較（品番3桁×単品4桁） -->
        <div class="space-y-2">
          <div class="flex flex-wrap items-baseline justify-between gap-2">
            <h3 class="text-sm font-semibold text-slate-700">帳票区分の前週比較（品番3桁×単品4桁）</h3>
            <p class="text-xs text-slate-400">
              当週 {{ transition?.currentWeek ?? '—' }} ／ 前週 {{ transition?.previousWeek ?? '—' }}
            </p>
          </div>

          <!-- 帳票区分タグ -->
          <div class="flex flex-wrap gap-1.5">
            <button
              v-for="tab in TRANSITION_TABS"
              :key="tab.key"
              type="button"
              class="inline-flex items-center gap-1.5 rounded-full border px-3 py-1.5 text-xs font-medium transition-colors"
              :class="
                transitionTab === tab.key
                  ? 'border-indigo-500 bg-indigo-50 text-indigo-700'
                  : 'border-slate-200 bg-white text-slate-600 hover:border-indigo-300'
              "
              :aria-pressed="transitionTab === tab.key"
              @click="transitionTab = tab.key"
            >
              {{ tab.label }}
              <span class="rounded-full bg-slate-100 px-1.5 text-[10px] font-bold text-slate-500">
                {{ formatNumber(tabCount(tab.key)) }}
              </span>
            </button>
          </div>

          <p v-if="transition?.truncated" class="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700">
            該当SKUが多いため上限件数で表示を打ち切りました。上の品番3桁フィルタで絞り込んでください。
          </p>

          <DataTable v-if="transitionRows.length > 0" :columns="transitionColumns" :rows="transitionRows" :row-key="transitionRowKey">
            <template #currentKubun="{ row }">
              <span
                v-if="KUBUN_BADGE[(row as ChohyoTransitionRow).transition]"
                class="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium"
                :class="KUBUN_BADGE[(row as ChohyoTransitionRow).transition]"
              >
                {{ kubunDisplay((row as ChohyoTransitionRow).currentKubun) }}
              </span>
              <span v-else>{{ kubunDisplay((row as ChohyoTransitionRow).currentKubun) }}</span>
            </template>
          </DataTable>
          <p v-if="transitionRows.length === 0" class="rounded-xl border border-slate-200 bg-white p-6 text-center text-xs text-slate-400">
            この区分に該当するSKUはありません。
          </p>
        </div>
      </div>
    </StatusBlock>
  </div>
</template>
