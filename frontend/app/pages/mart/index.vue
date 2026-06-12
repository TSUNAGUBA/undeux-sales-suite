<script setup lang="ts">
import {
  CircleDollarSign,
  ShoppingCart,
  TrendingUp,
  Percent,
  Package,
  Layers,
  Boxes,
  Gauge,
  Database,
  RefreshCw,
} from 'lucide-vue-next'
import type {
  KpiCardItem,
  MartSummaryResponse,
  MartBreakdownResponse,
  BreakdownRow,
} from '~/types/api'

useHead({ title: '全社サマリー | UndeuxSales' })

// mart 専用のフィルタスコープ。既存 sales 系（'sales-filter'）とは分離する。
const MART_SCOPE = 'mart-filter'
const { toQuery, loadOptions } = useFilters(MART_SCOPE)
const { get } = useApi()
const { status, isBuilt, rebuilding, rebuildMessage, refreshStatus, rebuild } = useMart()

const summary = ref<MartSummaryResponse | null>(null)
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

function metricValue(row: BreakdownRow): number {
  if (metric.value === 'quantity') return row.quantity
  if (metric.value === 'grossProfit') return row.grossProfit
  return row.amount
}

const kpiItems = computed<KpiCardItem[]>(() => {
  const kpi = summary.value?.kpi
  if (!kpi) {
    return []
  }
  return [
    {
      label: '売上金額',
      value: formatCurrency(kpi.amount),
      icon: CircleDollarSign,
      accentClass: 'bg-indigo-50 text-indigo-600',
    },
    {
      label: '売上数量',
      value: `${formatNumber(kpi.quantity)} 点`,
      icon: ShoppingCart,
      accentClass: 'bg-sky-50 text-sky-600',
    },
    {
      label: '粗利',
      value: formatCurrency(kpi.grossProfit),
      icon: TrendingUp,
      accentClass: 'bg-emerald-50 text-emerald-600',
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

const trendLabels = computed(() =>
  (summary.value?.weeklyTrend ?? []).map((point) => point.date),
)

const trendSeries = computed(() => {
  const trend = summary.value?.weeklyTrend ?? []
  return [
    { label: '売上金額', data: trend.map((p) => p.amount), color: '#4f46e5' },
    { label: '粗利', data: trend.map((p) => p.grossProfit), color: '#059669' },
  ]
})

const breakdownLabels = computed(() => (breakdown.value?.rows ?? []).map((r) => r.label))
const breakdownData = computed(() => (breakdown.value?.rows ?? []).map(metricValue))

const tableColumns = [
  { key: 'label', label: '区分' },
  {
    key: 'quantity',
    label: '数量',
    align: 'right' as const,
    format: (row: BreakdownRow) => formatNumber(row.quantity),
  },
  {
    key: 'amount',
    label: '売上金額',
    align: 'right' as const,
    format: (row: BreakdownRow) => formatCurrency(row.amount),
  },
  {
    key: 'grossProfit',
    label: '粗利',
    align: 'right' as const,
    format: (row: BreakdownRow) => formatCurrency(row.grossProfit),
  },
  {
    key: 'sharePercent',
    label: '構成比',
    align: 'right' as const,
    format: (row: BreakdownRow) => formatPercent(row.sharePercent),
  },
]

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    await refreshStatus()
    if (!isBuilt.value) {
      summary.value = null
      breakdown.value = null
      return
    }
    const query = toQuery()
    const [summaryResult, breakdownResult] = await Promise.all([
      get<MartSummaryResponse>('/api/mart/summary', query),
      get<MartBreakdownResponse>('/api/mart/breakdown', {
        ...query,
        dimension: dimension.value,
        metric: metric.value,
        limit: 15,
      }),
    ])
    summary.value = summaryResult
    breakdown.value = breakdownResult
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
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
      <h1 class="text-xl font-bold text-slate-800">全社サマリー</h1>
      <p class="text-sm text-slate-500">
        分析用ディメンショナルモデル（mart）から集計。既存の売上参照（sales_weekly）とは別系統で、
        他小売・他メーカーにも展開可能な汎用構造。
      </p>
    </div>

    <!-- mart 構築状態と再構築 -->
    <div
      class="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-200 bg-white p-3"
    >
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
    <p class="text-xs text-slate-400">
      ※ 再構築は sales_weekly + 商品マスタから派生データ（次元・ファクト）を作り直します（public → mart のデータ移行。認証ユーザーが実行可）。
    </p>

    <FilterBar :scope-key="MART_SCOPE" @apply="load" />

    <StatusBlock :loading="loading" :error="errorMessage">
      <div
        v-if="!isBuilt"
        class="rounded-xl border border-amber-200 bg-amber-50 p-8 text-center text-sm text-amber-700"
      >
        mart がまだ構築されていません。上の「mart を再構築」を実行すると、スタースキーマ集計が表示されます。
      </div>
      <div v-else class="space-y-4">
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

        <p v-if="summary?.kpi.latestWeek" class="text-xs text-slate-400">
          最新取込週: {{ summary.kpi.latestWeek }}
        </p>

        <LineChartCard
          v-if="trendLabels.length > 0"
          title="週次売上推移グラフ"
          :labels="trendLabels"
          :series="trendSeries"
        />

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
          :title="`${metricLabel}ランキング（上位15）`"
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
    </StatusBlock>
  </div>
</template>
