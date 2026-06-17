<script setup lang="ts">
/**
 * ブランド/シリーズ分析（/mart/brand）ページ。
 *
 * ブランド軸・シリーズ軸（＝商品記号 product_sign。データに「シリーズ」概念が無いため
 * 商品記号で代替）で売れ行きを比較する集計ページ。
 *
 * データ源（軸により別エンドポイント。共通の AxisRow に正規化する）:
 * - ブランド: /api/mart/breakdown?dimension=brand（breakdown はブランド軸に対応）
 * - シリーズ（商品記号）: /api/mart/ranking?dimension=shohinKigo（商品記号は複合キーのため
 *   breakdown 非対応。ranking の主期間集計を流用する）
 *
 * 構成比は返却行（上位N件）を母集団としてフロント側で算出する表示射影
 * （ランキング分析ページの ABC/構成比と同じ「返却行を母集団とする」方針）。
 */
import { ListOrdered, Tag, TrendingUp, Trophy } from 'lucide-vue-next'
import type {
  KpiCardItem,
  MartBreakdownResponse,
  RankingResponse,
} from '~/types/api'

useHead({ title: 'ブランド/シリーズ分析 | UndeuxSales' })

const MART_SCOPE = 'mart-filter'
const { toQuery, loadOptions } = useFilters(MART_SCOPE)
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()

/** 取得件数の上限（チャート・表ともこの範囲で構成比を算出する）。 */
const FETCH_LIMIT = 30
/** 棒グラフに出す最大件数。 */
const CHART_MAX = 20

type Axis = 'brand' | 'series'
const axis = ref<Axis>('brand')
const axisOptions: { value: Axis; label: string }[] = [
  { value: 'brand', label: 'ブランド' },
  { value: 'series', label: 'シリーズ（商品記号）' },
]

type Metric = 'amount' | 'quantity' | 'grossProfit'
const metric = ref<Metric>('amount')
const metricOptions: { value: Metric; label: string }[] = [
  { value: 'amount', label: '売上金額' },
  { value: 'quantity', label: '売上数量' },
  { value: 'grossProfit', label: '粗利' },
]

/** ブランド/シリーズ共通の行（軸別エンドポイントの結果を正規化）。 */
interface AxisRow {
  key: string
  label: string
  quantity: number
  amount: number
  grossProfit: number
}

const rows = ref<AxisRow[]>([])
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const axisLabel = computed(() => axisOptions.find((a) => a.value === axis.value)?.label ?? 'ブランド')
const metricLabel = computed(() => metricOptions.find((m) => m.value === metric.value)?.label ?? '売上金額')

function metricValue(row: AxisRow): number {
  return metric.value === 'quantity' ? row.quantity : metric.value === 'grossProfit' ? row.grossProfit : row.amount
}

/** 選択指標の降順に整列した行。 */
const sortedRows = computed<AxisRow[]>(() =>
  rows.value.slice().sort((a, b) => metricValue(b) - metricValue(a)),
)
const totalMetric = computed(() => sortedRows.value.reduce((sum, r) => sum + metricValue(r), 0))
function shareOf(row: AxisRow): number {
  return totalMetric.value > 0 ? (metricValue(row) / totalMetric.value) * 100 : 0
}

const kpiItems = computed<KpiCardItem[]>(() => {
  const list = sortedRows.value
  if (list.length === 0) return []
  const top = list[0]!
  const top5Share = list.slice(0, 5).reduce((sum, r) => sum + shareOf(r), 0)
  return [
    {
      label: '対象件数',
      value: `${formatNumber(list.length)} 件`,
      icon: ListOrdered,
      accentClass: 'bg-slate-50 text-slate-600',
    },
    {
      label: '首位',
      value: top.label || '—',
      icon: Trophy,
      accentClass: 'bg-amber-50 text-amber-600',
      sub: `構成比 ${formatPercent(shareOf(top))}`,
    },
    {
      label: '上位5構成比',
      value: formatPercent(top5Share),
      icon: TrendingUp,
      accentClass: 'bg-indigo-50 text-indigo-600',
    },
  ]
})

const chartRows = computed<AxisRow[]>(() => sortedRows.value.slice(0, CHART_MAX))
const barLabels = computed(() => chartRows.value.map((r) => r.label || '(未設定)'))
const barData = computed(() => chartRows.value.map(metricValue))

const tableColumns = [
  { key: 'label', label: axisLabel.value },
  { key: 'quantity', label: '売上数量', align: 'right' as const, format: (r: AxisRow) => formatNumber(r.quantity) },
  { key: 'amount', label: '売上金額', align: 'right' as const, format: (r: AxisRow) => formatCurrency(r.amount) },
  { key: 'grossProfit', label: '粗利', align: 'right' as const, format: (r: AxisRow) => formatCurrency(r.grossProfit) },
  { key: 'share', label: '構成比', align: 'right' as const, format: (r: AxisRow) => formatPercent(shareOf(r)) },
]
// 軸切替で先頭列の見出し（ブランド/シリーズ）を更新する。
const dynamicColumns = computed(() =>
  tableColumns.map((col) => (col.key === 'label' ? { ...col, label: axisLabel.value } : col)),
)

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    await refreshStatus()
    if (!isBuilt.value) {
      rows.value = []
      return
    }
    const query = toQuery()
    if (axis.value === 'brand') {
      const res = await get<MartBreakdownResponse>('/api/mart/breakdown', {
        ...query,
        dimension: 'brand',
        metric: metric.value,
        limit: FETCH_LIMIT,
      })
      rows.value = res.rows.map((r) => ({
        key: r.key,
        label: r.label,
        quantity: r.quantity,
        amount: r.amount,
        grossProfit: r.grossProfit,
      }))
    } else {
      const res = await get<RankingResponse>('/api/mart/ranking', {
        ...query,
        dimension: 'shohinKigo',
        limit: FETCH_LIMIT,
      })
      rows.value = res.rows.map((r) => ({
        key: r.key,
        label: r.label,
        quantity: r.current?.quantity ?? 0,
        amount: r.current?.amount ?? 0,
        grossProfit: r.current?.grossProfit ?? 0,
      }))
    }
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

// 軸・指標の変更はサーバ側の集計対象（上位N件）が変わるため再取得する。
const initialized = ref(false)
watch([axis, metric], () => {
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
      <h1 class="text-xl font-bold text-slate-800">ブランド/シリーズ分析</h1>
      <p class="text-sm text-slate-500">
        ブランド・シリーズ（商品記号）軸で売れ行きを比較します。構成比は表示中の上位
        {{ FETCH_LIMIT }} 件を母集団として算出します（データに「シリーズ」が無いため商品記号で代替）。
      </p>
    </div>

    <FilterBar :scope-key="MART_SCOPE" @apply="load" />

    <!-- 集計軸・指標（フィルタ → 集計単位 → 表示集計値 の導線に合わせ FilterBar の後段に置く） -->
    <div class="flex flex-wrap items-end gap-3 rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
      <div>
        <label class="mb-1 block text-xs font-medium text-slate-500">集計軸</label>
        <select
          v-model="axis"
          class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
          :disabled="loading"
        >
          <option v-for="opt in axisOptions" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
        </select>
      </div>
      <div>
        <label class="mb-1 block text-xs font-medium text-slate-500">指標</label>
        <select
          v-model="metric"
          class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
          :disabled="loading"
        >
          <option v-for="opt in metricOptions" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
        </select>
      </div>
    </div>

    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="isBuilt && sortedRows.length === 0"
      empty-message="該当するデータがありません。フィルタや集計軸を見直してください。"
    >
      <MartNotBuiltNotice v-if="!isBuilt" />
      <div v-else class="space-y-4">
        <div class="grid grid-cols-1 gap-3 sm:grid-cols-3">
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

        <BarChartCard
          v-if="barLabels.length > 0"
          :title="`${axisLabel}別 ${metricLabel}（上位${CHART_MAX}）`"
          :labels="barLabels"
          :data="barData"
          :series-label="metricLabel"
          color="#4f46e5"
          horizontal
        />

        <DataTable :columns="dynamicColumns" :rows="sortedRows" :row-key="(row: AxisRow) => row.key" />
      </div>
    </StatusBlock>
  </div>
</template>
