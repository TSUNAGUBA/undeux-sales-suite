<script setup lang="ts">
import type { MartSummaryResponse, MartBreakdownResponse, BreakdownRow } from '~/types/api'

useHead({ title: '売上分析（スタースキーマ） | UndeuxSales' })

const MART_SCOPE = 'mart-filter'
const { toQuery, addToFilter, loadOptions } = useFilters(MART_SCOPE)
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()

const dimension = ref('department')
const metric = ref('amount')

const summary = ref<MartSummaryResponse | null>(null)
const breakdown = ref<MartBreakdownResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

// mart 集計軸（breakdown エンドポイントが対応する軸）。日次・カラー/サイズ別は mart 売上分析では非対応。
const dimensionOptions = [
  { value: 'department', label: '部門別' },
  { value: 'businessType', label: '業態別' },
  { value: 'season', label: '季節別' },
  { value: 'product', label: '品番別' },
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

const trendLabels = computed(() => (summary.value?.weeklyTrend ?? []).map((point) => point.date))
const trendSeries = computed(() => {
  const points = summary.value?.weeklyTrend ?? []
  return [
    { label: '売上金額', data: points.map((p) => p.amount), color: '#4f46e5' },
    { label: '粗利', data: points.map((p) => p.grossProfit), color: '#059669' },
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

function handleBreakdownDrill(row: BreakdownRow): void {
  const currentDim = dimension.value
  let targetRow = 'category:hinban'

  if (currentDim === 'department') {
    addToFilter('departments', row.key)
  } else if (currentDim === 'businessType') {
    addToFilter('businessTypes', row.key)
  } else if (currentDim === 'season') {
    addToFilter('seasons', row.key)
  } else if (currentDim === 'product') {
    // mart breakdown の product は品番（product_code）が key。
    addToFilter('hinbans', row.key)
    targetRow = 'category:hinban'
  }

  navigateTo({
    path: '/mart/crosstab',
    query: { rowDimension: targetRow, columnDimension: 'time:year' },
  })
}

onMounted(async () => {
  await loadOptions()
  await load()
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">売上分析（スタースキーマ）</h1>
      <p class="text-sm text-slate-500">
        分析 mart（fact_sales_weekly）の週次トレンドと集計軸別の売上構成（日次は mart 非対応）。
      </p>
    </div>

    <FilterBar :scope-key="MART_SCOPE" @apply="load" />

    <StatusBlock :loading="loading" :error="errorMessage">
      <MartNotBuiltNotice v-if="!isBuilt" />
      <div v-else class="space-y-4">
        <LineChartCard
          v-if="trendLabels.length > 0"
          title="週次売上推移（mart）"
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
          clickable
          @row-click="handleBreakdownDrill"
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
