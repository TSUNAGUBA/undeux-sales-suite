<script setup lang="ts">
/**
 * 売上分析（/mart/sales）ページ。
 *
 * - 週次売上推移グラフ: 売上数量・売上金額=折れ線、店頭在庫=棒、気温=折れ線 の複合チャート。
 *   気温は週平均/最高/最低の3種とエリア（標準=東京/寒冷=札幌/温暖=那覇）を切り替えられる。
 * - 週次明細テーブル: 週ごとの売上金額・売上数量・気温・店頭在庫・在日・消化率。
 *   品番フィルタ（ドリルダウン）を適用すると品番単位の週次詳細になる。
 *
 * 期間指定（年月 from-to）はフィルターの最上部に置き、業態・部門フィルタ（FilterBar）と一体で扱う。
 * 売上金額ランキング・順位変動・部門別売上ランキングは目的が薄いため本ページからは除外し、
 * ランキング系の分析は「探索・予測分析 > ランキング分析」に集約する。
 *
 * データ源は /api/mart/*（fact_sales_weekly / fact_inventory_snapshot / 気温 dim_climate）。
 */
import type {
  TemperatureArea,
  WeeklySeriesPoint,
  WeeklySeriesResponse,
} from '~/types/api'
import type { ComboChartAxis, ComboChartSeries } from '~/components/ComboChartCard.vue'

useHead({ title: '売上分析 | UndeuxSales' })

const MART_SCOPE = 'mart-filter'
const { toQuery, loadOptions, options } = useFilters(MART_SCOPE)
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()

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
const loading = ref(true)
const errorMessage = ref<string | null>(null)

// ---------------------------------------------------------------
// 期間（年月 from-to）。年度（単一選択）に代わる期間指定。
// 共有フィルタの year は使わず（FilterBar は hide-year）、本ページにローカルに保持する。
// ---------------------------------------------------------------
const fromMonth = ref<string | null>(null)
const toMonth = ref<string | null>(null)

/** 取込週（月曜）から "YYYY-MM" の昇順ユニークリストを導出する。 */
const months = computed<string[]>(() => {
  const set = new Set<string>()
  for (const week of options.value?.weeks ?? []) {
    set.add(week.slice(0, 7))
  }
  return [...set].sort()
})

/** 年月の終端を月末日へ（import_date <= to に月内の全週を含めるため）。 */
function monthEnd(ym: string): string {
  const [y, m] = ym.split('-').map((s) => Number.parseInt(s, 10))
  const lastDay = new Date(y!, m!, 0).getDate()
  return `${ym}-${String(lastDay).padStart(2, '0')}`
}

/** 選択中の年月レンジを API の from/to（日付）へ。未指定の端は開放。 */
function dateRange(): { from?: string; to?: string } {
  const range: { from?: string; to?: string } = {}
  if (fromMonth.value) range.from = `${fromMonth.value}-01`
  if (toMonth.value) range.to = monthEnd(toMonth.value)
  return range
}

/** 開始が終了より後の不正レンジ。 */
const periodInvalid = computed(
  () => fromMonth.value !== null && toMonth.value !== null && fromMonth.value > toMonth.value,
)

/** 共有フィルタの軸はそのまま使い、期間だけ年月レンジで上書きしたクエリ。 */
function periodQuery(): Record<string, unknown> {
  const query = toQuery()
  delete query.from
  delete query.to
  const range = dateRange()
  if (range.from) query.from = range.from
  if (range.to) query.to = range.to
  return query
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
  { key: 'week', label: '週（月曜）', frozen: true },
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

// 期間・エリアの連続変更で古い応答が後着しても表示を上書きしないためのリクエスト世代。
let salesLoadSeq = 0

async function load(): Promise<void> {
  const seq = ++salesLoadSeq
  loading.value = true
  errorMessage.value = null
  try {
    await refreshStatus()
    if (seq !== salesLoadSeq) return
    if (!isBuilt.value) {
      weekly.value = null
      return
    }
    const query = periodQuery()
    const weeklyResult = await get<WeeklySeriesResponse>('/api/mart/weekly-series', { ...query, area: area.value })
    if (seq !== salesLoadSeq) return
    weekly.value = weeklyResult
  } catch (error) {
    if (seq === salesLoadSeq) {
      errorMessage.value = apiErrorMessage(error)
    }
  } finally {
    if (seq === salesLoadSeq) {
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
  // 既定の期間: 最新年の先頭月〜最新月（年度→年月化に伴う初期表示を従来の単年に近づける）。
  if (months.value.length > 0) {
    const latest = months.value[months.value.length - 1]!
    const latestYear = latest.slice(0, 4)
    toMonth.value = latest
    fromMonth.value = months.value.find((m) => m.startsWith(latestYear)) ?? latest
  }
  await load()
  initialized.value = true
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">売上分析</h1>
      <p class="text-sm text-slate-500">
        分析 mart（fact_sales_weekly / fact_inventory_snapshot）の週次売上推移と週次明細（日次は mart 非対応）。
        気温は mart の気温データ（実測 dim_climate、未カバー週は標準気候へフォールバック）。
      </p>
    </div>

    <!-- 期間（年月 from-to）。フィルターの最上部に置く（業態・部門フィルタと一体で扱う）。 -->
    <div class="flex flex-wrap items-end gap-3 rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
      <div>
        <label class="mb-1 block text-xs font-medium text-slate-500">期間（年月）開始</label>
        <select
          v-model="fromMonth"
          class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
          @change="load"
        >
          <option :value="null">最初から</option>
          <option v-for="m in months" :key="m" :value="m">{{ m }}</option>
        </select>
      </div>
      <div>
        <label class="mb-1 block text-xs font-medium text-slate-500">期間（年月）終了</label>
        <select
          v-model="toMonth"
          class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
          @change="load"
        >
          <option :value="null">最後まで</option>
          <option v-for="m in months" :key="m" :value="m">{{ m }}</option>
        </select>
      </div>
      <p v-if="periodInvalid" class="text-xs text-amber-600">
        開始が終了より後です。期間を見直してください。
      </p>
      <p v-else class="text-xs text-slate-400">
        年月の from-to で期間を指定します（未指定の端は開放）。
      </p>
    </div>

    <FilterBar :scope-key="MART_SCOPE" hide-year @apply="load" />

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

        <div
          v-else
          class="rounded-xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-400"
        >
          選択した期間に売上データがありません。
        </div>
      </div>
    </StatusBlock>
  </div>
</template>
