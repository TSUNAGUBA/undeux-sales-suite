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
  RankingDimensionKey,
  RankingMetricKey,
  RankingResponse,
  TemperatureArea,
  WeeklySeriesPoint,
  WeeklySeriesResponse,
} from '~/types/api'
import type { ComboChartAxis, ComboChartSeries } from '~/components/ComboChartCard.vue'
import type { MoverItem } from '~/utils/ranking'

useHead({ title: '売上分析 | UndeuxSales' })

const MART_SCOPE = 'mart-filter'
const { toQuery, addToFilter, loadOptions, options } = useFilters(MART_SCOPE)
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

// ---------------------------------------------------------------
// 順位変動（別ページ＝ランキング分析の RankingMoversChart を本ページにも表示）。
// 現在の集計軸 × 前年同期比較で算出する（ブランド軸はランキング API 非対応のため除外）。
// ---------------------------------------------------------------
const MOVERS_DIM_MAP: Partial<Record<string, RankingDimensionKey>> = {
  department: 'department',
  businessType: 'businessType',
  season: 'season',
  product: 'product',
}
const moversDim = computed<RankingDimensionKey | undefined>(() => MOVERS_DIM_MAP[dimension.value])

/** 前年同期の比較範囲（現在の from-to を1年戻す）。両端が確定している場合のみ。 */
function previousYearRange(): { compareFrom: string; compareTo: string } | null {
  const range = dateRange()
  if (!range.from || !range.to) return null
  const shiftYear = (d: string): string => {
    const [y, m, day] = d.split('-')
    return `${Number.parseInt(y!, 10) - 1}-${m}-${day}`
  }
  return { compareFrom: shiftYear(range.from), compareTo: shiftYear(range.to) }
}
const comparisonAvailable = computed(() => previousYearRange() !== null)

const moversData = ref<RankingResponse | null>(null)
// 連打・期間変更で古い応答が後着しても上書きしないリクエスト世代。
let moversSeq = 0

async function loadMovers(baseQuery: Record<string, unknown>): Promise<void> {
  const seq = ++moversSeq
  moversData.value = null
  const dim = moversDim.value
  const compare = previousYearRange()
  if (!dim || !compare || periodInvalid.value) return
  try {
    const res = await get<RankingResponse>('/api/mart/ranking', {
      ...baseQuery,
      dimension: dim,
      compareFrom: compare.compareFrom,
      compareTo: compare.compareTo,
    })
    if (seq !== moversSeq) return
    moversData.value = res
  } catch (error) {
    // 順位変動は補助表示。取得失敗で売上分析本体は止めない（原則4）。
    if (seq === moversSeq) {
      console.error('[sales] 順位変動の取得に失敗しました:', error)
      moversData.value = null
    }
  }
}

const moverItems = computed<MoverItem[]>(() => {
  const data = moversData.value
  if (!data) return []
  const metricKey = metric.value as RankingMetricKey
  const curRank = assignRanks(
    data.rows
      .filter((r) => r.current)
      .map((r) => ({ key: r.key, value: metricRawValue(r.current, metricKey), tieBreak: r.current!.amount })),
    'higher',
  )
  const prevRank = assignRanks(
    data.rows
      .filter((r) => r.comparison)
      .map((r) => ({ key: r.key, value: metricRawValue(r.comparison, metricKey), tieBreak: r.comparison!.amount })),
    'higher',
  )
  const items: MoverItem[] = []
  for (const r of data.rows) {
    if (!r.current) continue
    const rank = curRank.get(r.key)
    if (rank === undefined) continue
    const pr = prevRank.get(r.key) ?? null
    items.push({
      key: r.key,
      label: r.label,
      rank,
      prevRank: pr,
      isNew: r.comparison === null,
      delta: pr !== null ? pr - rank : null,
    })
  }
  return items
})

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
      moversData.value = null
      return
    }
    const query = periodQuery()
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
    // 順位変動は補助表示のため非ブロッキングで取得（本体の表示を待たせない）。
    void loadMovers(query)
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
        分析 mart（fact_sales_weekly / fact_inventory_snapshot）の週次推移と集計軸別の売上構成（日次は mart 非対応）。
        気温は mart の気温データ（実測 dim_climate、未カバー週は標準気候へフォールバック）。
      </p>
    </div>

    <FilterBar :scope-key="MART_SCOPE" hide-year @apply="load" />

    <!-- 期間（年月 from-to）。年度（単一選択）に代わる期間指定。 -->
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
        年月の from-to で期間を指定します（未指定の端は開放）。順位変動には開始・終了の両方が必要です。
      </p>
    </div>

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

        <!-- 順位変動（前年同期比）。ランキング分析と同じ RankingMoversChart を再利用。 -->
        <section class="space-y-1">
          <h3 class="text-sm font-semibold text-slate-700">順位変動（前年同期比）</h3>
          <RankingMoversChart v-if="moverItems.length > 0" :items="moverItems" />
          <p
            v-else-if="!moversDim"
            class="rounded-xl border border-slate-200 bg-white p-4 text-xs text-slate-400"
          >
            順位変動は集計軸が「部門・業態・季節・品番CD（服種）」のときに表示されます（ブランド軸は非対応）。
          </p>
          <p
            v-else-if="!comparisonAvailable"
            class="rounded-xl border border-slate-200 bg-white p-4 text-xs text-slate-400"
          >
            上の「期間（年月）」で開始・終了の両方を指定すると、前年同期比の順位変動を表示します。
          </p>
          <p
            v-else
            class="rounded-xl border border-slate-200 bg-white p-4 text-xs text-slate-400"
          >
            順位変動を表示できるデータがありません（比較期間にデータが無い可能性があります）。
          </p>
        </section>
      </div>
    </StatusBlock>
  </div>
</template>
