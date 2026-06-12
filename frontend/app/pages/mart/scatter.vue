<script setup lang="ts">
/**
 * 散布図・回帰分析（/mart/scatter）ページ。
 *
 * データ源は分析 mart（/api/mart/*）。
 * モードA【MD・発注向け】「週平均(最高/最低)気温 × 週売上数量」（点=各週）と
 * モードB【在庫・販促向け】「消化率 × 値引き率」（点=型番、バブル=売上数量）を提供する。
 *
 * データフロー（SoT）: mart は集計素材（週次系列・型番別指標）のみ返し、回帰直線・
 * 象限分類はフロント（utils/regression）で算出する表示射影。気温は mart の気温データ
 * （実測 dim_climate、未カバー週は標準気候へフォールバック）。
 */
import { LineChart, ScatterChart, Thermometer } from 'lucide-vue-next'
import type {
  KpiCardItem,
  MarkdownScatterPoint,
  MarkdownScatterResponse,
  TemperatureArea,
  WeeklySeriesResponse,
} from '~/types/api'
import type { Point } from '~/utils/regression'
import type { ScatterDataset } from '~/components/ScatterChartCard.vue'

useHead({ title: '散布図・回帰分析 | UndeuxSales' })

// mart 専用のフィルタスコープ。既存 sales 系とは分離する。
const MART_SCOPE = 'mart-filter'
const { toQuery, loadOptions } = useFilters(MART_SCOPE)
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()

type Mode = 'temperature' | 'markdown'
const mode = ref<Mode>('temperature')

// 在庫マネジメントの推奨アクション「値下げ候補」から ?mode=markdown で直接
// モードB（消化率×値引き率）を開けるようにする（不正値は既定モードのまま）。
const route = useRoute()
if (route.query.mode === 'markdown') {
  mode.value = 'markdown'
}

// --- モードA: 気温×売上数量 ---
const area = ref<TemperatureArea>('standard')
type TempMeasure = 'avg' | 'max' | 'min'
const tempMeasure = ref<TempMeasure>('avg')
const tempMeasureOptions: { value: TempMeasure; label: string }[] = [
  { value: 'avg', label: '週平均気温' },
  { value: 'max', label: '週最高気温' },
  { value: 'min', label: '週最低気温' },
]
const weekly = ref<WeeklySeriesResponse | null>(null)

// --- モードB: 消化率×値引き率 ---
const markdown = ref<MarkdownScatterResponse | null>(null)

const loading = ref(true)
const errorMessage = ref<string | null>(null)

const areaOptions = TEMPERATURE_AREAS

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    await refreshStatus()
    if (!isBuilt.value) {
      weekly.value = null
      markdown.value = null
      return
    }
    if (mode.value === 'temperature') {
      weekly.value = await get<WeeklySeriesResponse>('/api/mart/weekly-series', {
        ...toQuery(),
        area: area.value,
      })
    } else {
      markdown.value = await get<MarkdownScatterResponse>('/api/mart/markdown', toQuery())
    }
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

// ---------------------------------------------------------------
// モードA: 気温×売上数量 の散布図 + 単回帰
// ---------------------------------------------------------------

function tempOf(p: WeeklySeriesResponse['points'][number]): number {
  return tempMeasure.value === 'max' ? p.tempMax : tempMeasure.value === 'min' ? p.tempMin : p.tempAvg
}

const tempPoints = computed<Point[]>(() =>
  (weekly.value?.points ?? []).map((p) => ({ x: tempOf(p), y: p.quantity })),
)

const tempRegression = computed(() => simpleLinearRegression(tempPoints.value))

const tempMeasureLabel = computed(
  () => tempMeasureOptions.find((m) => m.value === tempMeasure.value)?.label ?? '週平均気温',
)

const tempDatasets = computed<ScatterDataset[]>(() => {
  const datasets: ScatterDataset[] = [
    { label: '各週', color: '#4f46e5', points: tempPoints.value, pointRadius: 4 },
  ]
  // 回帰直線（x 範囲の両端を結ぶ）。
  const reg = tempRegression.value
  const xs = tempPoints.value.map((p) => p.x)
  if (reg && xs.length >= 2) {
    const xMin = Math.min(...xs)
    const xMax = Math.max(...xs)
    datasets.push({
      label: '回帰直線',
      color: '#dc2626',
      points: [
        { x: xMin, y: reg.slope * xMin + reg.intercept },
        { x: xMax, y: reg.slope * xMax + reg.intercept },
      ],
      showLine: true,
    })
  }
  return datasets
})

const tempKpis = computed<KpiCardItem[]>(() => {
  const reg = tempRegression.value
  if (!reg) return []
  const trend = reg.slope >= 0 ? '上昇' : '低下'
  return [
    {
      label: '気温感度（傾き）',
      value: `${formatDecimal(reg.slope, 1)} 点/℃`,
      icon: Thermometer,
      accentClass: 'bg-sky-50 text-sky-600',
    },
    {
      label: '決定係数 R²',
      value: formatDecimal(reg.r2, 2),
      icon: LineChart,
      accentClass: 'bg-indigo-50 text-indigo-600',
    },
    {
      label: '対象週数',
      value: `${formatNumber(reg.n)} 週`,
      icon: ScatterChart,
      accentClass: 'bg-slate-50 text-slate-600',
    },
    {
      label: '気温との関係',
      value: `${trend}傾向`,
      icon: Thermometer,
      accentClass: reg.slope >= 0 ? 'bg-emerald-50 text-emerald-600' : 'bg-rose-50 text-rose-600',
    },
  ]
})

const tempInsight = computed(() => {
  const reg = tempRegression.value
  if (!reg) return null
  const dir = reg.slope >= 0 ? '上がる' : '下がる'
  const fit = reg.r2 >= 0.5 ? '気温で売上の動きをよく説明できます' : '気温以外の要因も大きいです'
  return `${weekly.value?.areaCity ?? ''}の${tempMeasureLabel.value}が 1℃ ${dir}と、`
    + `週売上数量は約 ${formatDecimal(Math.abs(reg.slope), 1)} 点 ${reg.slope >= 0 ? '増える' : '減る'}傾向です`
    + `（R²=${formatDecimal(reg.r2, 2)}：${fit}）。点が斜めに並んだあと急に立ち上がる温度帯が「スイッチ温度（適正展開温度）」です。`
})

// ---------------------------------------------------------------
// モードB: 消化率×値引き率 の4象限
// ---------------------------------------------------------------

/** 売上数量からバブル半径（sqrt スケール、3〜18px）を算出する。 */
function radiusFor(quantity: number, maxQuantity: number): number {
  if (maxQuantity <= 0) return 4
  return 3 + Math.sqrt(quantity / maxQuantity) * 15
}

const SELL_THROUGH_LINE = 50 // 消化率の縦基準線（%）

const markdownMedian = computed(() => {
  const ys = (markdown.value?.points ?? []).map((p) => p.markdownRate).sort((a, b) => a - b)
  if (ys.length === 0) return 0
  const mid = Math.floor(ys.length / 2)
  return ys.length % 2 === 0 ? (ys[mid - 1]! + ys[mid]!) / 2 : ys[mid]!
})

const markdownDatasets = computed<ScatterDataset[]>(() => {
  const points = markdown.value?.points ?? []
  if (points.length === 0) return []
  const maxQty = Math.max(...points.map((p) => p.quantity), 1)
  const datasets: ScatterDataset[] = [
    {
      label: '型番（バブル=売上数量）',
      color: '#7c3aed',
      points: points.map((p) => ({ x: p.sellThroughRate, y: p.markdownRate })),
      pointRadius: points.map((p) => radiusFor(p.quantity, maxQty)),
    },
    // 基準線（縦: 消化率50% / 横: 値引き率の中央値）。
    {
      label: `消化率 ${SELL_THROUGH_LINE}%`,
      color: '#94a3b8',
      points: [
        { x: SELL_THROUGH_LINE, y: 0 },
        { x: SELL_THROUGH_LINE, y: 100 },
      ],
      showLine: true,
      dashed: true,
    },
    {
      label: `値引き率 中央値 ${formatDecimal(markdownMedian.value, 0)}%`,
      color: '#cbd5e1',
      points: [
        { x: 0, y: markdownMedian.value },
        { x: 100, y: markdownMedian.value },
      ],
      showLine: true,
      dashed: true,
    },
  ]
  return datasets
})

const quadrantGuide = [
  { label: '右下：お宝', desc: '値下げ少なく消化率高 → 即追加生産／値下げ禁止', color: 'text-emerald-600' },
  { label: '左下：危険', desc: '値下げ少なく消化率低 → 早期に小幅値下げで損切り', color: 'text-amber-600' },
  { label: '左上：大爆死', desc: '大幅値下げでも消化率低 → EC限定／来期アウトレット', color: 'text-rose-600' },
  { label: '右上：好調値引き', desc: '値下げ進行かつ消化率高 → 計画的な売り切り', color: 'text-sky-600' },
]

// 型番別テーブル（散布図の点の明細）。倉庫在庫はデータソース（売上参照DB）に
// 存在しないため対象外（店頭在庫のみ）。
const markdownColumns = [
  { key: 'label', label: '品番CD（服種）' },
  { key: 'businessType', label: '業態' },
  { key: 'kisetsu', label: '季節', format: (row: MarkdownScatterPoint) => row.season ?? '-' },
  {
    key: 'quantity',
    label: '売上数量',
    align: 'right' as const,
    format: (row: MarkdownScatterPoint) => formatNumber(row.quantity),
  },
  {
    key: 'sellThroughRate',
    label: '消化率',
    align: 'right' as const,
    format: (row: MarkdownScatterPoint) => formatPercent(row.sellThroughRate),
  },
  {
    key: 'markdownRate',
    label: '値引き率',
    align: 'right' as const,
    format: (row: MarkdownScatterPoint) => formatPercent(row.markdownRate),
  },
  {
    key: 'stockDays',
    label: '平均在庫日数',
    align: 'right' as const,
    format: (row: MarkdownScatterPoint) => formatDecimal(row.stockDays, 1),
  },
  {
    key: 'stock',
    label: '店頭在庫数',
    align: 'right' as const,
    format: (row: MarkdownScatterPoint) => formatNumber(row.stock),
  },
]

// ---------------------------------------------------------------
// 再取得トリガ（モード・エリア変更で即再取得。フィルタは「適用」ボタン）。
// ---------------------------------------------------------------
const initialized = ref(false)
watch([mode, area], () => {
  if (initialized.value) void load()
})

const isEmpty = computed(() =>
  mode.value === 'temperature'
    ? (weekly.value?.points.length ?? 0) === 0
    : (markdown.value?.points.length ?? 0) === 0,
)

onMounted(async () => {
  await loadOptions()
  await load()
  initialized.value = true
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">散布図・回帰分析</h1>
      <p class="text-sm text-slate-500">
        分析 mart の集計素材で、気温×売上で「スイッチ温度」を、消化率×値引き率で値下げ判断（4象限）を可視化します。
        気温は mart の気温データ（実測 dim_climate、未カバー週は標準気候へフォールバック）。
      </p>
    </div>

    <FilterBar :scope-key="MART_SCOPE" @apply="load" />

    <!-- モード切替 -->
    <div class="flex flex-wrap items-center gap-2">
      <div class="inline-flex overflow-hidden rounded-lg border border-slate-300">
        <button
          type="button"
          class="px-3 py-1.5 text-sm"
          :class="mode === 'temperature' ? 'bg-indigo-600 text-white' : 'bg-white text-slate-600'"
          @click="mode = 'temperature'"
        >
          気温 × 売上数量
        </button>
        <button
          type="button"
          class="px-3 py-1.5 text-sm"
          :class="mode === 'markdown' ? 'bg-indigo-600 text-white' : 'bg-white text-slate-600'"
          @click="mode = 'markdown'"
        >
          消化率 × 値引き率
        </button>
      </div>

      <template v-if="mode === 'temperature'">
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
      </template>
    </div>

    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="isBuilt && isEmpty"
      empty-message="該当するデータがありません。フィルタや期間を見直してください。"
    >
      <MartNotBuiltNotice v-if="!isBuilt" />

      <!-- モードA -->
      <div v-else-if="mode === 'temperature'" class="space-y-4">
        <div class="grid grid-cols-2 gap-3 lg:grid-cols-4">
          <KpiCard
            v-for="item in tempKpis"
            :key="item.label"
            :label="item.label"
            :value="item.value"
            :icon="item.icon"
            :accent-class="item.accentClass"
          />
        </div>
        <p v-if="tempInsight" class="rounded-lg bg-sky-50 px-3 py-2 text-sm text-sky-800">
          {{ tempInsight }}
        </p>
        <ScatterChartCard
          :title="`${tempMeasureLabel}（${weekly?.areaCity ?? ''}） × 週売上数量`"
          :x-label="`${tempMeasureLabel}（℃）`"
          y-label="週売上数量（点）"
          :datasets="tempDatasets"
          x-suffix="℃"
          y-suffix="点"
        />
      </div>

      <!-- モードB -->
      <div v-else class="space-y-4">
        <ScatterChartCard
          title="プロパー消化率 × 値引き率（型番）"
          x-label="消化率（%）"
          y-label="値引き率（%）"
          :datasets="markdownDatasets"
          x-suffix="%"
          y-suffix="%"
          begin-at-zero
        />
        <div class="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-4">
          <div
            v-for="q in quadrantGuide"
            :key="q.label"
            class="rounded-lg border border-slate-200 bg-white p-3 text-xs"
          >
            <p class="font-semibold" :class="q.color">{{ q.label }}</p>
            <p class="mt-1 text-slate-500">{{ q.desc }}</p>
          </div>
        </div>
        <!-- 型番別の明細テーブル（売上数量・平均在庫日数・季節・店頭在庫数を含む） -->
        <div class="space-y-1">
          <h3 class="text-sm font-semibold text-slate-700">型番別明細</h3>
          <DataTable
            :columns="markdownColumns"
            :rows="markdown?.points ?? []"
            :row-key="(row: MarkdownScatterPoint) => row.key"
          />
        </div>

        <p v-if="markdown?.latestWeek" class="text-xs text-slate-400">
          消化率の基準週: {{ markdown.latestWeek }} ／ 値引き率は商品マスタの定価を基準に算出（マスタ未登録の型番は対象外）
          ／ 在庫はいずれも店頭在庫（倉庫在庫はデータソースに含まれないため対象外）。
        </p>
      </div>
    </StatusBlock>
  </div>
</template>
