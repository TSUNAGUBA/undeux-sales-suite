<script setup lang="ts">
import type { BreakdownResponse, BreakdownRow, TrendResponse } from '~/types/api'

useHead({ title: '売上分析 | UndeuxSales' })

const { toQuery, addToFilter, loadOptions } = useFilters()
const { get } = useApi()

const granularity = ref<'weekly' | 'daily'>('weekly')
const dimension = ref('department')
const metric = ref('amount')

const trend = ref<TrendResponse | null>(null)
const breakdown = ref<BreakdownResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const dimensionOptions = [
  { value: 'department', label: '部門別' },
  { value: 'businessType', label: '業態別' },
  { value: 'season', label: '季節別' },
  { value: 'product', label: '商品別' },
  { value: 'color', label: 'カラー別' },
  { value: 'size', label: 'サイズ別' },
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

const trendLabels = computed(() => (trend.value?.points ?? []).map((point) => point.date))
const trendSeries = computed(() => {
  const points = trend.value?.points ?? []
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
    const query = toQuery()
    const [trendResult, breakdownResult] = await Promise.all([
      get<TrendResponse>('/api/sales/trend', { ...query, granularity: granularity.value }),
      get<BreakdownResponse>('/api/sales/breakdown', {
        ...query,
        dimension: dimension.value,
        metric: metric.value,
        limit: 15,
      }),
    ])
    trend.value = trendResult
    breakdown.value = breakdownResult
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

function handleBreakdownDrill(row: BreakdownRow): void {
  const currentDim = dimension.value
  // 新クロス集計仕様: 行=対応するカテゴリ軸、列=年（時間軸との組合せでトレンドを併覧）。
  let targetRow = 'category:hinban'

  if (currentDim === 'department') {
    addToFilter('departments', row.key)
  } else if (currentDim === 'businessType') {
    addToFilter('businessTypes', row.key)
  } else if (currentDim === 'season') {
    addToFilter('seasons', row.key)
  } else if (currentDim === 'product') {
    // BreakdownDimension.Product の表示ラベルは `hinban_code-tanpin_code` 形式
    // （内部キー key は (gyotai|shohin_kigou|hinban|tanpin) のユニーク識別子なので、
    //   品番抽出には表示用の label を使う）。
    const hinban = row.label.split('-')[0]
    if (hinban) {
      addToFilter('hinbans', hinban)
    }
    targetRow = 'category:product'
  }
  // color / size はフィルター追加対象がないため、対応する集計単位のまま遷移
  if (currentDim === 'color') {
    targetRow = 'category:color'
  } else if (currentDim === 'size') {
    targetRow = 'category:size'
  }

  navigateTo({
    path: '/crosstab',
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
      <h1 class="text-xl font-bold text-slate-800">売上分析</h1>
      <p class="text-sm text-slate-500">トレンドと集計軸別の売上構成</p>
    </div>

    <FilterBar @apply="load" />

    <StatusBlock :loading="loading" :error="errorMessage">
      <div class="space-y-4">
        <div class="flex flex-wrap items-center gap-2">
          <span class="text-sm font-medium text-slate-600">トレンド粒度:</span>
          <div class="inline-flex overflow-hidden rounded-lg border border-slate-300">
            <button
              type="button"
              class="px-3 py-1.5 text-sm"
              :class="
                granularity === 'weekly'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-slate-600'
              "
              @click="granularity = 'weekly'; load()"
            >
              週次
            </button>
            <button
              type="button"
              class="px-3 py-1.5 text-sm"
              :class="
                granularity === 'daily'
                  ? 'bg-indigo-600 text-white'
                  : 'bg-white text-slate-600'
              "
              @click="granularity = 'daily'; load()"
            >
              日次
            </button>
          </div>
        </div>

        <LineChartCard
          v-if="trendLabels.length > 0"
          :title="granularity === 'weekly' ? '週次売上推移' : '日次売上推移'"
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
