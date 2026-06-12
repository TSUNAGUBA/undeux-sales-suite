<script setup lang="ts">
/**
 * 売上分析（/mart/sales）ページ。
 *
 * - 週次売上推移グラフ: 売上数量・売上金額=折れ線、店頭在庫=棒、気温=折れ線 の複合チャート。
 *   気温は週平均/最高/最低の3種とエリア（標準=東京/寒冷=札幌/温暖=那覇）を切り替えられる。
 * - 週次明細テーブル: 週ごとの売上金額・売上数量・気温・店頭在庫・在日・消化率。
 *   品番フィルタ（ドリルダウン）を適用すると品番単位の週次詳細になる。
 * - 集計軸別の売上構成（部門・業態・季節・品番CD（服種）・ブランド）。
 *
 * データ源は /api/mart/*（fact_sales_weekly / fact_inventory_snapshot / 気温 dim_climate）。
 */
import type {
  MartBreakdownResponse,
  BreakdownRow,
  TemperatureArea,
  WeeklySeriesPoint,
  WeeklySeriesResponse,
} from '~/types/api'
import type { ComboChartAxis, ComboChartSeries } from '~/components/ComboChartCard.vue'

useHead({ title: '売上分析 | UndeuxSales' })

const MART_SCOPE = 'mart-filter'
const { toQuery, addToFilter, loadOptions } = useFilters(MART_SCOPE)
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()

const dimension = ref('department')
const metric = ref('amount')

// 気温の定義: エリア（標準/寒冷/温暖）× 種別（平均/最高/最低）。週は月曜〜日曜。
const area = ref<TemperatureArea>('standard')
type TempMeasure = 'avg' | 'max' | 'min'
const tempMeasure = ref<TempMeasure>('avg')
const tempMeasureOptions: { value: TempMeasure; label: string }[] = [
  { value: 'avg', label: '週平均気温' },
  { value: 'max', label: '週最高気温' },
  { value: 'min', label: '週最低気温' },
]
const areaOptions = TEMPERATURE_AREAS

const weekly = ref<WeeklySeriesResponse | null>(null)
const breakdown = ref<MartBreakdownResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

// mart 集計軸（breakdown エンドポイントが対応する軸）。日次・カラー/サイズ別は mart 売上分析では非対応。
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

const tempMeasureLabel = computed(
  () => tempMeasureOptions.find((m) => m.value === tempMeasure.value)?.label ?? '週平均気温',
)

function tempOf(p: WeeklySeriesPoint): number {
  return tempMeasure.value === 'max' ? p.tempMax : tempMeasure.value === 'min' ? p.tempMin : p.tempAvg
}

// ---------------------------------------------------------------
// 週次売上推移グラフ（売上数量/売上金額=折れ線、店頭在庫=棒、気温=折れ線）
// 単位の異なる3軸: y=点（数量・在庫）/ y1=円（金額）/ y2=℃（気温）。
// ---------------------------------------------------------------

const trendLabels = computed(() => (weekly.value?.points ?? []).map((p) => p.week))

const trendSeries = computed<ComboChartSeries[]>(() => {
  const points = weekly.value?.points ?? []
  return [
    { label: '店頭在庫', data: points.map((p) => p.stock), color: '#f59e0b', type: 'bar', yAxisId: 'y' },
    { label: '売上数量', data: points.map((p) => p.quantity), color: '#0ea5e9', type: 'line', yAxisId: 'y' },
    { label: '売上金額', data: points.map((p) => p.amount), color: '#4f46e5', type: 'line', yAxisId: 'y1' },
    {
      label: `${tempMeasureLabel.value}（${weekly.value?.areaCity ?? ''}）`,
      data: points.map(tempOf),
      color: '#dc2626',
      type: 'line',
      yAxisId: 'y2',
    },
  ]
})

const trendAxes: ComboChartAxis[] = [
  { id: 'y', position: 'left', label: '点（数量・在庫）' },
  { id: 'y1', position: 'right', label: '円（売上金額）', gridOff: true },
  { id: 'y2', position: 'right', label: '℃（気温）', beginAtZero: false, gridOff: true },
]

// ---------------------------------------------------------------
// 週次明細テーブル（売上金額・売上数量・気温・店頭在庫・在日・消化率）
// ---------------------------------------------------------------

const weeklyColumns = computed(() => [
  { key: 'week', label: '週（月曜）' },
  {
    key: 'quantity',
    label: '売上数量',
    align: 'right' as const,
    format: (row: WeeklySeriesPoint) => formatNumber(row.quantity),
  },
  {
    key: 'amount',
    label: '売上金額',
    align: 'right' as const,
    format: (row: WeeklySeriesPoint) => formatCurrency(row.amount),
  },
  {
    key: 'temp',
    label: `気温（${tempMeasureLabel.value}）`,
    align: 'right' as const,
    format: (row: WeeklySeriesPoint) => `${formatDecimal(tempOf(row), 1)}℃`,
  },
  {
    key: 'stock',
    label: '店頭在庫',
    align: 'right' as const,
    format: (row: WeeklySeriesPoint) => formatNumber(row.stock),
  },
  {
    key: 'stockDays',
    label: '在日（平均）',
    align: 'right' as const,
    format: (row: WeeklySeriesPoint) => formatDecimal(row.stockDays, 1),
  },
  {
    key: 'sellThroughRate',
    label: '消化率',
    align: 'right' as const,
    format: (row: WeeklySeriesPoint) => formatRatioAsPercent(row.sellThroughRate),
  },
])

// ---------------------------------------------------------------
// 集計軸別の売上構成（既存どおり）
// ---------------------------------------------------------------

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
      weekly.value = null
      breakdown.value = null
      return
    }
    const query = toQuery()
    const [weeklyResult, breakdownResult] = await Promise.all([
      get<WeeklySeriesResponse>('/api/mart/weekly-series', { ...query, area: area.value }),
      get<MartBreakdownResponse>('/api/mart/breakdown', {
        ...query,
        dimension: dimension.value,
        metric: metric.value,
        limit: 15,
      }),
    ])
    weekly.value = weeklyResult
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
    // mart breakdown の product は品番CD（product_code）が key。
    addToFilter('hinbans', row.key)
    targetRow = 'category:hinban'
  }

  navigateTo({
    path: '/mart/crosstab',
    query: { rowDimension: targetRow, columnDimension: 'time:year' },
  })
}

// エリア変更はバックエンドの気温が変わるため再取得する。
// 気温種別はフロント射影（同じ週次データから avg/max/min を選ぶだけ）なので再取得しない。
const initialized = ref(false)
watch(area, () => {
  if (initialized.value) void load()
})

onMounted(async () => {
  await loadOptions()
  await load()
  initialized.value = true
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">売上分析</h1>
      <p class="text-sm text-slate-500">
        分析 mart（fact_sales_weekly / fact_inventory_snapshot）の週次推移と集計軸別の売上構成（日次は mart 非対応）。
        気温は mart の気温データ（実測 dim_climate、未カバー週は標準気候へフォールバック）。
      </p>
    </div>

    <FilterBar :scope-key="MART_SCOPE" @apply="load" />

    <div class="flex flex-wrap items-center gap-2">
      <select
        v-model="area"
        class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm"
        aria-label="気温エリア"
      >
        <option v-for="a in areaOptions" :key="a.value" :value="a.value">{{ a.label }}</option>
      </select>
      <select
        v-model="tempMeasure"
        class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm"
        aria-label="気温の種別"
      >
        <option v-for="m in tempMeasureOptions" :key="m.value" :value="m.value">{{ m.label }}</option>
      </select>
    </div>

    <StatusBlock :loading="loading" :error="errorMessage">
      <MartNotBuiltNotice v-if="!isBuilt" />
      <div v-else class="space-y-4">
        <ComboChartCard
          v-if="trendLabels.length > 0"
          title="週次売上推移グラフ"
          :labels="trendLabels"
          :series="trendSeries"
          :axes="trendAxes"
        />

        <!-- 週次明細（品番フィルタ適用時は品番の週次詳細になる） -->
        <div v-if="(weekly?.points.length ?? 0) > 0" class="space-y-1">
          <h3 class="text-sm font-semibold text-slate-700">週次明細</h3>
          <p class="text-xs text-slate-400">
            週ごとの売上・気温・在庫。品番ドリルダウン（フィルタ）を適用すると品番詳細になります。
          </p>
          <DataTable
            :columns="weeklyColumns"
            :rows="weekly?.points ?? []"
            :row-key="(row: WeeklySeriesPoint) => row.week"
          />
        </div>

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
