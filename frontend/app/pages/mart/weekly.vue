<script setup lang="ts">
/**
 * 週間モニタリング（/mart/weekly）ページ。
 *
 * 直近取込週の実績と前週比、週次推移を1ページで把握するダッシュボード。
 * データ源は /api/mart/weekly-series（WeeklySeriesResponse）。売上分析（/mart/sales）と
 * 同じ週次系列を用い、本ページは「最新週スナップショット＋前週比（WoW）」に焦点を当てる。
 *
 * - フィルタスコープは 'mart-filter'（他 mart ページと共有）。
 * - 前週比は1つ前の取込週との相対差をフロント側で算出する表示射影（mart は週次素材のみ返す）。
 */
import {
  Boxes,
  CalendarRange,
  CircleDollarSign,
  Gauge,
  ShoppingCart,
  TrendingUp,
} from 'lucide-vue-next'
import type { KpiCardItem, TemperatureArea, WeeklySeriesPoint, WeeklySeriesResponse } from '~/types/api'
import type { ComboChartAxis, ComboChartSeries } from '~/components/ComboChartCard.vue'

useHead({ title: '週間モニタリング | UndeuxSales' })

const MART_SCOPE = 'mart-filter'
const { toQuery, loadOptions } = useFilters(MART_SCOPE)
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()

// 気温の定義: エリア（標準/寒冷/温暖）× 種別（平均/最高/最低）。売上分析と同じ複合チャート。
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
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const points = computed<WeeklySeriesPoint[]>(() => weekly.value?.points ?? [])
const latest = computed<WeeklySeriesPoint | null>(() => points.value[points.value.length - 1] ?? null)
const previous = computed<WeeklySeriesPoint | null>(() => points.value[points.value.length - 2] ?? null)

const DELTA_DISPLAY_EPSILON = 0.05

/** 前週比（相対%）。前週が無い／0 の場合は表示しない。 */
function pctText(cur: number, prev: number | undefined | null): string | undefined {
  if (prev === undefined || prev === null || prev === 0) return undefined
  const pct = ((cur - prev) / Math.abs(prev)) * 100
  if (Math.abs(pct) < DELTA_DISPLAY_EPSILON) return '前週比 ±0.0%'
  return `前週比 ${pct > 0 ? '+' : '−'}${Math.abs(pct).toFixed(1)}%`
}

/** 比率系（消化率）の前週差（pt）。 */
function ptText(cur: number, prev: number | undefined | null): string | undefined {
  if (prev === undefined || prev === null) return undefined
  const delta = (cur - prev) * 100
  if (Math.abs(delta) < DELTA_DISPLAY_EPSILON) return '前週 ±0.0pt'
  return `前週 ${delta > 0 ? '+' : '−'}${Math.abs(delta).toFixed(1)}pt`
}

/** 日数系（平均在日）の前週差（日）。 */
function dayText(cur: number, prev: number | undefined | null): string | undefined {
  if (prev === undefined || prev === null) return undefined
  const delta = cur - prev
  if (Math.abs(delta) < DELTA_DISPLAY_EPSILON) return '前週 ±0.0日'
  return `前週 ${delta > 0 ? '+' : '−'}${Math.abs(delta).toFixed(1)}日`
}

const kpiItems = computed<KpiCardItem[]>(() => {
  const cur = latest.value
  if (!cur) return []
  const prev = previous.value
  return [
    {
      label: '売上金額',
      value: formatCurrency(cur.amount),
      icon: CircleDollarSign,
      accentClass: 'bg-indigo-50 text-indigo-600',
      sub: pctText(cur.amount, prev?.amount),
    },
    {
      label: '売上数量',
      value: `${formatNumber(cur.quantity)} 点`,
      icon: ShoppingCart,
      accentClass: 'bg-sky-50 text-sky-600',
      sub: pctText(cur.quantity, prev?.quantity),
    },
    {
      label: '粗利',
      value: formatCurrency(cur.grossProfit),
      icon: TrendingUp,
      accentClass: 'bg-emerald-50 text-emerald-600',
      sub: pctText(cur.grossProfit, prev?.grossProfit),
    },
    {
      label: '店頭在庫',
      value: `${formatNumber(cur.stock)} 点`,
      icon: Boxes,
      accentClass: 'bg-rose-50 text-rose-600',
      sub: pctText(cur.stock, prev?.stock),
    },
    {
      label: '消化率',
      value: formatRatioAsPercent(cur.sellThroughRate),
      icon: Gauge,
      accentClass: 'bg-teal-50 text-teal-600',
      sub: ptText(cur.sellThroughRate, prev?.sellThroughRate),
    },
    {
      label: '平均在日',
      value: `${formatDecimal(cur.stockDays, 1)} 日`,
      icon: CalendarRange,
      accentClass: 'bg-violet-50 text-violet-600',
      sub: dayText(cur.stockDays, prev?.stockDays),
    },
  ]
})

const tempMeasureLabel = computed(
  () => tempMeasureOptions.find((m) => m.value === tempMeasure.value)?.label ?? '週平均気温',
)
function tempOf(p: WeeklySeriesPoint): number {
  return tempMeasure.value === 'max' ? p.tempMax : tempMeasure.value === 'min' ? p.tempMin : p.tempAvg
}

const trendLabels = computed(() => points.value.map((p) => p.week))
const trendSeries = computed<ComboChartSeries[]>(() => {
  const ps = points.value
  return [
    { label: '店頭在庫', data: ps.map((p) => p.stock), color: '#f59e0b', type: 'bar', yAxisId: 'y' },
    { label: '売上数量', data: ps.map((p) => p.quantity), color: '#0ea5e9', type: 'line', yAxisId: 'y' },
    { label: '売上金額', data: ps.map((p) => p.amount), color: '#4f46e5', type: 'line', yAxisId: 'y1' },
    {
      label: `${tempMeasureLabel.value}（${weekly.value?.areaCity ?? ''}）`,
      data: ps.map(tempOf),
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

// 前週比つき週次明細（新しい週が上）。WoW は1つ前の取込週との相対差。
interface WeeklyRow extends WeeklySeriesPoint {
  amountWow: number | null
  quantityWow: number | null
}

function relativeChange(cur: number, prev: number | undefined): number | null {
  return prev !== undefined && prev !== 0 ? ((cur - prev) / Math.abs(prev)) * 100 : null
}

const weeklyRows = computed<WeeklyRow[]>(() =>
  points.value
    .map((p, i) => {
      const prev = i > 0 ? points.value[i - 1] : undefined
      return {
        ...p,
        amountWow: relativeChange(p.amount, prev?.amount),
        quantityWow: relativeChange(p.quantity, prev?.quantity),
      }
    })
    .reverse(),
)

function wowCell(v: number | null): string {
  if (v === null) return '—'
  if (Math.abs(v) < DELTA_DISPLAY_EPSILON) return '±0.0%'
  return `${v > 0 ? '+' : '−'}${Math.abs(v).toFixed(1)}%`
}

const weeklyColumns = [
  { key: 'week', label: '週（月曜）', frozen: true },
  { key: 'amount', label: '売上金額', align: 'right' as const, format: (r: WeeklyRow) => formatCurrency(r.amount) },
  { key: 'amountWow', label: '売上 前週比', align: 'right' as const, format: (r: WeeklyRow) => wowCell(r.amountWow) },
  { key: 'quantity', label: '売上数量', align: 'right' as const, format: (r: WeeklyRow) => formatNumber(r.quantity) },
  { key: 'quantityWow', label: '数量 前週比', align: 'right' as const, format: (r: WeeklyRow) => wowCell(r.quantityWow) },
  { key: 'grossProfit', label: '粗利', align: 'right' as const, format: (r: WeeklyRow) => formatCurrency(r.grossProfit) },
  { key: 'stock', label: '店頭在庫', align: 'right' as const, format: (r: WeeklyRow) => formatNumber(r.stock) },
  { key: 'sellThroughRate', label: '消化率', align: 'right' as const, format: (r: WeeklyRow) => formatRatioAsPercent(r.sellThroughRate) },
  { key: 'stockDays', label: '平均在日', align: 'right' as const, format: (r: WeeklyRow) => `${formatDecimal(r.stockDays, 1)} 日` },
]

// エリア・フィルタの連続変更で古い応答が後着しても上書きしないリクエスト世代。
let loadSeq = 0

async function load(): Promise<void> {
  const seq = ++loadSeq
  loading.value = true
  errorMessage.value = null
  try {
    await refreshStatus()
    if (seq !== loadSeq) return
    if (!isBuilt.value) {
      weekly.value = null
      return
    }
    const result = await get<WeeklySeriesResponse>('/api/mart/weekly-series', {
      ...toQuery(),
      area: area.value,
    })
    if (seq !== loadSeq) return
    weekly.value = result
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
      <h1 class="text-xl font-bold text-slate-800">週間モニタリング</h1>
      <p class="text-sm text-slate-500">
        直近取込週の実績と前週比、週次推移をまとめて確認します（分析 mart の週次系列基準<template
          v-if="latest"
        > ／ 最新取込週 {{ latest.week }}</template>）。
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

    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="isBuilt && points.length === 0"
      empty-message="該当する週次データがありません。フィルタを見直してください。"
    >
      <MartNotBuiltNotice v-if="!isBuilt" />
      <div v-else class="space-y-4">
        <!-- 最新週の実績 + 前週比 -->
        <div class="grid grid-cols-2 gap-3 md:grid-cols-3 lg:grid-cols-6">
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

        <ComboChartCard
          v-if="trendLabels.length > 0"
          title="週次推移グラフ"
          :labels="trendLabels"
          :series="trendSeries"
          :axes="trendAxes"
        />

        <div class="space-y-1">
          <h3 class="text-sm font-semibold text-slate-700">週次明細（前週比つき）</h3>
          <p class="text-xs text-slate-400">
            新しい取込週が上。前週比は1つ前の取込週との相対差です。
          </p>
          <DataTable :columns="weeklyColumns" :rows="weeklyRows" :row-key="(row: WeeklyRow) => row.week" />
        </div>
      </div>
    </StatusBlock>
  </div>
</template>
