<script setup lang="ts">
/**
 * 商品の詳細分析（/mart/products/{productId}）ページ。
 *
 * 商品マスタ詳細（/product-master/{id}）の表現に倣い、画像・基本情報・サマリー・SKU情報・
 * 週次売上推移グラフ（売上数量/売上金額=折れ線、店頭在庫=棒、気温=折れ線）・クロス集計を
 * 1ページで表現する。
 *
 * データフロー:
 * - 画像・基本情報・SKU（マスタ属性）: /api/product-master/{id}（商品マスタが SoT）
 * - サマリー・週次系列・SKU実績・クロス集計: /api/mart/*。商品マスタの自然キー
 *   （業態 businessCategoryCd × 記号 productSign × 品番 productTypeCrd）を
 *   businessTypes / shohinKigos / hinbans フィルタに渡して単一商品へ絞る。
 * - 条件設定の導線は「フィルタ（期間）→ 集計単位（クロス集計の行・列）→ 表示集計値」の順。
 */
import {
  ArrowLeft,
  Boxes,
  CircleDollarSign,
  ExternalLink,
  Gauge,
  Percent,
  ShoppingCart,
  TrendingUp,
} from 'lucide-vue-next'
import type {
  CrosstabDimensionInfo,
  CrosstabDimensionKey,
  CrosstabMatrixResponse,
  CrosstabMetricInfo,
  CrosstabMetricKey,
  KpiCardItem,
  MasterProductDetail,
  ProductPage,
  TemperatureArea,
  WeeklySeriesPoint,
  WeeklySeriesResponse,
} from '~/types/api'
import type { ComboChartAxis, ComboChartSeries } from '~/components/ComboChartCard.vue'

useHead({ title: '商品詳細分析 | UndeuxSales' })

const route = useRoute()
const router = useRouter()
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()
// 年度の選択肢（/api/filters）だけを共有から読む。共有フィルタ state は変更しない。
const { options: sharedOptions, loadOptions, years } = useFilters('mart-filter')

const productId = computed(() => String(route.params.productId ?? ''))

// ---------------------------------------------------------------
// マスタ（画像・基本情報・SKU属性）
// ---------------------------------------------------------------

const detail = ref<MasterProductDetail | null>(null)
const masterLoading = ref(true)
const masterError = ref<string | null>(null)
const notFound = ref(false)

async function loadMaster(): Promise<void> {
  masterLoading.value = true
  masterError.value = null
  notFound.value = false
  try {
    detail.value = await get<MasterProductDetail>(`/api/product-master/${productId.value}`)
  } catch (error) {
    // UNDX-DATA-002 = productId 不正/未登録（商品マスタ詳細ページと同じ判定）。
    if (extractApiError(error)?.errorCode === 'UNDX-DATA-002') {
      notFound.value = true
      detail.value = null
    } else {
      masterError.value = apiErrorMessage(error)
    }
  } finally {
    masterLoading.value = false
  }
}

// 画像ギャラリー（全SKUの画像をフラットに集約。先頭が代表）。
const galleryImages = computed<string[]>(() => {
  const urls: string[] = []
  for (const sku of detail.value?.skus ?? []) {
    for (const image of sku.images) {
      if (!urls.includes(image.imageUrl)) urls.push(image.imageUrl)
    }
  }
  if (urls.length === 0 && detail.value?.summary.primaryImageUrl) {
    urls.push(detail.value.summary.primaryImageUrl)
  }
  return urls
})
const selectedImageIdx = ref(0)
const heroImage = computed<string | null>(
  () => galleryImages.value[selectedImageIdx.value] ?? galleryImages.value[0] ?? null,
)

const priceLabel = computed(() => {
  const summary = detail.value?.summary
  if (!summary) return '—'
  const { minSalesPrice: min, maxSalesPrice: max } = summary
  if (min === null && max === null) return '—'
  if (min !== null && max !== null && min !== max) {
    return `${formatCurrency(min)} 〜 ${formatCurrency(max)}`
  }
  return formatCurrency(max ?? min ?? 0)
})

// ---------------------------------------------------------------
// フィルタ（期間=年度）。mart 集計（サマリー・週次・SKU実績・クロス集計）に共通適用する。
// ---------------------------------------------------------------

const year = ref<number | null>(null)

/** 商品の自然キー + 期間を mart API のクエリへ変換する。 */
function martQuery(): Record<string, unknown> {
  const summary = detail.value?.summary
  const query: Record<string, unknown> = {}
  if (summary) {
    query.businessTypes = [summary.businessCategoryCd]
    query.shohinKigos = [summary.productSign]
    query.hinbans = [summary.productTypeCrd]
  }
  if (year.value !== null) {
    query.from = `${year.value}-01-01`
    query.to = `${year.value}-12-31`
  }
  return query
}

// ---------------------------------------------------------------
// mart 集計（サマリーKPI・週次系列・SKU実績）
// ---------------------------------------------------------------

const area = ref<TemperatureArea>('standard')
type TempMeasure = 'avg' | 'max' | 'min'
const tempMeasure = ref<TempMeasure>('avg')
const tempMeasureOptions: { value: TempMeasure; label: string }[] = [
  { value: 'avg', label: '週平均気温' },
  { value: 'max', label: '週最高気温' },
  { value: 'min', label: '週最低気温' },
]
const areaOptions = TEMPERATURE_AREAS

const martSummary = ref<{ kpi: import('~/types/api').MartKpi } | null>(null)
const weekly = ref<WeeklySeriesResponse | null>(null)
const skuPerformance = ref<ProductPage | null>(null)
const analyticsLoading = ref(true)
const analyticsError = ref<string | null>(null)

async function loadAnalytics(): Promise<void> {
  if (!detail.value) return
  analyticsLoading.value = true
  analyticsError.value = null
  try {
    await refreshStatus()
    if (!isBuilt.value) {
      martSummary.value = null
      weekly.value = null
      skuPerformance.value = null
      return
    }
    const query = martQuery()
    const [summaryResult, weeklyResult, skuResult] = await Promise.all([
      get<import('~/types/api').MartSummaryResponse>('/api/mart/summary', query),
      get<WeeklySeriesResponse>('/api/mart/weekly-series', { ...query, area: area.value }),
      get<ProductPage>('/api/mart/products', {
        ...query,
        sort: 'salesAmount',
        order: 'desc',
        page: 1,
        pageSize: 200,
      }),
    ])
    martSummary.value = summaryResult
    weekly.value = weeklyResult
    skuPerformance.value = skuResult
  } catch (error) {
    analyticsError.value = apiErrorMessage(error)
  } finally {
    analyticsLoading.value = false
  }
}

// ---------------------------------------------------------------
// サマリー（KPIカード）
// ---------------------------------------------------------------

const kpiItems = computed<KpiCardItem[]>(() => {
  const kpi = martSummary.value?.kpi
  if (!kpi) return []
  return [
    {
      label: '売上数量',
      value: `${formatNumber(kpi.quantity)} 点`,
      icon: ShoppingCart,
      accentClass: 'bg-sky-50 text-sky-600',
    },
    {
      label: '売上金額',
      value: formatCurrency(kpi.amount),
      icon: CircleDollarSign,
      accentClass: 'bg-indigo-50 text-indigo-600',
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
      label: '店頭在庫',
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
  ]
})

// ---------------------------------------------------------------
// SKU情報（マスタ属性 × mart 実績の突合）
// ---------------------------------------------------------------

interface SkuInfoRow {
  unitCd: string
  colorName: string
  sizeName: string
  salesPrice: number
  imageUrl: string | null
  quantity: number
  amount: number
  grossProfit: number
  stock: number
  sellThroughRate: number
  stockDays: number
}

const skuRows = computed<SkuInfoRow[]>(() => {
  const perfByUnit = new Map(
    (skuPerformance.value?.items ?? []).map((row) => [row.tanpinCode, row]),
  )
  return (detail.value?.skus ?? []).map((sku) => {
    const perf = perfByUnit.get(sku.unitCd)
    return {
      unitCd: sku.unitCd,
      colorName: sku.colorName,
      sizeName: sku.sizeName,
      salesPrice: sku.salesPrice,
      imageUrl: sku.images[0]?.imageUrl ?? null,
      quantity: perf?.salesQuantity ?? 0,
      amount: perf?.salesAmount ?? 0,
      grossProfit: perf?.grossProfit ?? 0,
      stock: perf?.stock ?? 0,
      sellThroughRate: perf?.sellThroughRate ?? 0,
      stockDays: perf?.averageStockDays ?? 0,
    }
  })
})

const skuColumns = [
  { key: 'thumbnail', label: '画像', format: (_row: SkuInfoRow) => '' },
  { key: 'unitCd', label: '単品CD' },
  { key: 'colorName', label: 'カラー' },
  { key: 'sizeName', label: 'サイズ' },
  {
    key: 'salesPrice',
    label: '定価',
    align: 'right' as const,
    format: (row: SkuInfoRow) => formatCurrency(row.salesPrice),
  },
  {
    key: 'quantity',
    label: '売上数量',
    align: 'right' as const,
    format: (row: SkuInfoRow) => formatNumber(row.quantity),
  },
  {
    key: 'amount',
    label: '売上金額',
    align: 'right' as const,
    format: (row: SkuInfoRow) => formatCurrency(row.amount),
  },
  {
    key: 'stock',
    label: '店頭在庫',
    align: 'right' as const,
    format: (row: SkuInfoRow) => formatNumber(row.stock),
  },
  {
    key: 'sellThroughRate',
    label: '消化率',
    align: 'right' as const,
    format: (row: SkuInfoRow) => formatRatioAsPercent(row.sellThroughRate),
  },
  {
    key: 'stockDays',
    label: '在日（平均）',
    align: 'right' as const,
    format: (row: SkuInfoRow) => formatDecimal(row.stockDays, 1),
  },
]

// ---------------------------------------------------------------
// 週次売上推移グラフ（売上数量/売上金額=折れ線、店頭在庫=棒、気温=折れ線）
// ---------------------------------------------------------------

const tempMeasureLabel = computed(
  () => tempMeasureOptions.find((m) => m.value === tempMeasure.value)?.label ?? '週平均気温',
)

function tempOf(p: WeeklySeriesPoint): number {
  return tempMeasure.value === 'max' ? p.tempMax : tempMeasure.value === 'min' ? p.tempMin : p.tempAvg
}

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
// クロス集計（商品スコープ固定。行・列とメトリクスのみ選択）
// ---------------------------------------------------------------

const DETAIL_DIMENSIONS: CrosstabDimensionInfo[] = [
  { key: 'category:color', category: 'category', label: 'カラー', isTimeAxis: false },
  { key: 'category:size', category: 'category', label: 'サイズ', isTimeAxis: false },
  { key: 'category:product', category: 'category', label: '単品（品番-単品）', isTimeAxis: false },
  { key: 'time:year', category: 'time', label: '年', isTimeAxis: true },
  { key: 'time:quarter', category: 'time', label: '四半期', isTimeAxis: true },
  { key: 'time:month', category: 'time', label: '月', isTimeAxis: true },
]

// 気温系は商品スコープのクロス集計では提供しない（週次売上推移グラフ側で表現する）。
const DETAIL_METRICS: CrosstabMetricInfo[] = [
  { key: 'quantity', label: '売上数量', format: 'number' },
  { key: 'amount', label: '売上金額', format: 'currency' },
  { key: 'grossProfit', label: '粗利', format: 'currency' },
  { key: 'sharePercent', label: '構成比率', format: 'percent' },
  { key: 'stock', label: '店頭在庫', format: 'number' },
  { key: 'sellThroughRate', label: '消化率', format: 'percent' },
  { key: 'stockDays', label: '在日（平均）', format: 'decimal' },
]
const STOCK_METRICS: CrosstabMetricKey[] = ['stock', 'sellThroughRate', 'stockDays']

const rowDimensionKey = ref<CrosstabDimensionKey>('category:color')
const columnDimensionKey = ref<CrosstabDimensionKey>('category:size')
const selectedMetrics = ref<CrosstabMetricKey[]>(['quantity', 'stock'])

const rowChoices = computed(() =>
  DETAIL_DIMENSIONS.filter((d) => d.key !== columnDimensionKey.value),
)
const colChoices = computed(() =>
  DETAIL_DIMENSIONS.filter((d) => d.key !== rowDimensionKey.value),
)

const hasTimeAxis = computed(() => {
  const row = DETAIL_DIMENSIONS.find((d) => d.key === rowDimensionKey.value)
  const col = DETAIL_DIMENSIONS.find((d) => d.key === columnDimensionKey.value)
  return Boolean(row?.isTimeAxis || col?.isTimeAxis)
})

const availableMetrics = computed<CrosstabMetricKey[]>(() =>
  hasTimeAxis.value
    ? DETAIL_METRICS.filter((m) => !STOCK_METRICS.includes(m.key)).map((m) => m.key)
    : DETAIL_METRICS.map((m) => m.key),
)

// 時間軸に切り替えたら在庫系メトリクスを自動解除する（クロス集計ページと同じ健全化）。
watch(availableMetrics, (avail) => {
  const after = selectedMetrics.value.filter((m) => avail.includes(m))
  if (after.length !== selectedMetrics.value.length) {
    selectedMetrics.value = after.length > 0 ? after : ['quantity']
  }
})

function toggleMetric(key: CrosstabMetricKey): void {
  const set = new Set(selectedMetrics.value)
  if (set.has(key)) {
    if (set.size === 1) return // 最低1つは残す
    set.delete(key)
  } else {
    set.add(key)
  }
  selectedMetrics.value = [...set]
}

const crosstab = ref<CrosstabMatrixResponse | null>(null)
const crosstabLoading = ref(false)
const crosstabError = ref<string | null>(null)

async function loadCrosstab(): Promise<void> {
  if (!detail.value || !isBuilt.value) {
    crosstab.value = null
    return
  }
  crosstabLoading.value = true
  crosstabError.value = null
  try {
    crosstab.value = await get<CrosstabMatrixResponse>('/api/mart/crosstab', {
      ...martQuery(),
      rowDimension: rowDimensionKey.value,
      columnDimension: columnDimensionKey.value,
    })
  } catch (error) {
    crosstabError.value = apiErrorMessage(error)
  } finally {
    crosstabLoading.value = false
  }
}

// ---------------------------------------------------------------
// 再取得トリガ
// ---------------------------------------------------------------

const initialized = ref(false)

// 期間（年度）は全 mart 集計に効く。エリアは週次系列のみだが纏めて再取得する（リクエスト数は同じ）。
watch([year, area], () => {
  if (!initialized.value) return
  void loadAnalytics()
  void loadCrosstab()
})

// 行・列の変更はクロス集計のみ再取得。同一になった場合は colChoices/rowChoices で防いでいる。
watch([rowDimensionKey, columnDimensionKey], () => {
  if (!initialized.value) return
  void loadCrosstab()
})

function goBack(): void {
  if (window.history.length > 1) {
    router.back()
  } else {
    void router.push('/mart/products')
  }
}

onMounted(async () => {
  await loadOptions()
  await loadMaster()
  if (detail.value) {
    await Promise.all([loadAnalytics(), loadCrosstab()])
  }
  initialized.value = true
})
</script>

<template>
  <div class="space-y-4">
    <!-- 戻る・関連リンク -->
    <div class="flex flex-wrap items-center gap-2">
      <button
        type="button"
        class="inline-flex min-h-[36px] items-center gap-1 rounded-lg border border-slate-300 px-3 py-2 text-xs text-slate-600 hover:bg-slate-50"
        @click="goBack"
      >
        <ArrowLeft class="h-3.5 w-3.5" />
        戻る
      </button>
      <NuxtLink
        to="/mart/products"
        class="inline-flex min-h-[36px] items-center gap-1 rounded-lg border border-slate-300 px-3 py-2 text-xs text-slate-600 hover:bg-slate-50"
      >
        商品別分析一覧へ
      </NuxtLink>
      <NuxtLink
        v-if="detail"
        :to="`/product-master/${productId}`"
        class="ml-auto inline-flex min-h-[36px] items-center gap-1 rounded-lg border border-slate-300 px-3 py-2 text-xs text-slate-600 hover:bg-slate-50"
      >
        商品マスタ詳細を見る
        <ExternalLink class="h-3.5 w-3.5" />
      </NuxtLink>
    </div>

    <div
      v-if="notFound"
      class="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-700"
    >
      指定された商品が見つかりません。URL の productId を確認してください。
    </div>

    <StatusBlock v-else :loading="masterLoading" :error="masterError">
      <div v-if="detail" class="space-y-4">
        <!-- 画像 + 基本情報 -->
        <div class="grid grid-cols-1 gap-4 rounded-xl border border-slate-200 bg-white p-4 shadow-sm md:grid-cols-[260px_1fr]">
          <div class="space-y-2">
            <div class="aspect-square w-full overflow-hidden rounded-lg bg-slate-100">
              <ProductImage
                :src="heroImage"
                :alt="detail.summary.productName"
                icon-class="h-12 w-12"
              />
            </div>
            <div v-if="galleryImages.length > 1" class="flex flex-wrap gap-1.5">
              <button
                v-for="(url, idx) in galleryImages.slice(0, 12)"
                :key="url"
                type="button"
                class="h-12 w-12 overflow-hidden rounded border"
                :class="idx === selectedImageIdx ? 'border-indigo-500 ring-1 ring-indigo-300' : 'border-slate-200'"
                :aria-label="`画像 ${idx + 1} を表示`"
                @click="selectedImageIdx = idx"
              >
                <ProductImage :src="url" :alt="detail.summary.productName" icon-class="h-4 w-4" :show-label="false" />
              </button>
            </div>
          </div>

          <div class="min-w-0 space-y-3">
            <div>
              <p class="text-xs text-slate-500">
                <code class="font-mono">{{ detail.summary.productSign }}</code>
                <span class="mx-1 text-slate-300">/</span>
                <code class="font-mono">{{ detail.summary.productTypeCrd }}</code>
              </p>
              <h1 class="text-xl font-bold text-slate-800">{{ detail.summary.productName || '—' }}</h1>
              <p v-if="detail.summary.brand" class="text-sm text-slate-500">{{ detail.summary.brand }}</p>
            </div>

            <dl class="grid grid-cols-2 gap-x-4 gap-y-2 border-t border-slate-100 pt-3 text-sm sm:grid-cols-3">
              <div>
                <dt class="text-xs text-slate-400">業態</dt>
                <dd class="font-medium text-slate-800">{{ detail.summary.businessCategorySign || detail.summary.businessCategoryCd }}</dd>
              </div>
              <div>
                <dt class="text-xs text-slate-400">部門</dt>
                <dd class="font-medium text-slate-800">{{ detail.summary.divisionName || detail.summary.divisionCd }}</dd>
              </div>
              <div>
                <dt class="text-xs text-slate-400">品番CD（服種）</dt>
                <dd class="font-medium text-slate-800">{{ detail.summary.productTypeCrd }}</dd>
              </div>
              <div>
                <dt class="text-xs text-slate-400">担当者</dt>
                <dd class="font-medium text-slate-800">{{ detail.summary.manager || '—' }}</dd>
              </div>
              <div>
                <dt class="text-xs text-slate-400">定価</dt>
                <dd class="font-medium text-slate-800">{{ priceLabel }}</dd>
              </div>
              <div>
                <dt class="text-xs text-slate-400">季節（季節コード）</dt>
                <dd class="font-medium text-slate-800">{{ detail.summary.kisetsu || '—' }}</dd>
              </div>
              <div>
                <dt class="text-xs text-slate-400">SKU数</dt>
                <dd class="font-medium text-slate-800">{{ formatNumber(detail.summary.skuCount) }}</dd>
              </div>
              <div>
                <dt class="text-xs text-slate-400">色数</dt>
                <dd class="font-medium text-slate-800">{{ formatNumber(detail.summary.colorCount) }}</dd>
              </div>
              <div>
                <dt class="text-xs text-slate-400">サイズ数</dt>
                <dd class="font-medium text-slate-800">{{ formatNumber(detail.summary.sizeCount) }}</dd>
              </div>
            </dl>
          </div>
        </div>

        <!-- フィルタ（期間）。以降の mart 集計（サマリー・SKU実績・週次・クロス集計）に共通適用 -->
        <div class="flex flex-wrap items-end gap-3 rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
          <div>
            <label class="mb-1 block text-xs font-medium text-slate-500">期間（年度）</label>
            <select
              v-model="year"
              class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
            >
              <option :value="null">全期間</option>
              <option v-for="y in years" :key="y" :value="y">{{ y }}年</option>
            </select>
          </div>
          <p v-if="!sharedOptions" class="text-xs text-slate-400">期間の選択肢を読み込み中...</p>
        </div>

        <MartNotBuiltNotice v-if="!isBuilt" />

        <StatusBlock v-else :loading="analyticsLoading" :error="analyticsError">
          <div class="space-y-4">
            <!-- サマリー -->
            <section>
              <h2 class="mb-2 text-sm font-semibold text-slate-700">サマリー</h2>
              <div class="grid grid-cols-2 gap-3 md:grid-cols-3 lg:grid-cols-6">
                <KpiCard
                  v-for="item in kpiItems"
                  :key="item.label"
                  :label="item.label"
                  :value="item.value"
                  :icon="item.icon"
                  :accent-class="item.accentClass"
                />
              </div>
              <p v-if="martSummary?.kpi.latestWeek" class="mt-1 text-xs text-slate-400">
                在庫・消化率は最新取込週（{{ martSummary.kpi.latestWeek }}）スナップショット基準。
              </p>
            </section>

            <!-- SKU情報 -->
            <section>
              <h2 class="mb-2 text-sm font-semibold text-slate-700">SKU情報</h2>
              <DataTable
                :columns="skuColumns"
                :rows="skuRows"
                :row-key="(row: SkuInfoRow) => row.unitCd"
              >
                <template #thumbnail="{ row }">
                  <div class="h-10 w-10 overflow-hidden rounded">
                    <ProductImage
                      :src="(row as SkuInfoRow).imageUrl"
                      :alt="`${(row as SkuInfoRow).colorName} ${(row as SkuInfoRow).sizeName}`"
                      icon-class="h-4 w-4"
                      :show-label="false"
                    />
                  </div>
                </template>
              </DataTable>
            </section>

            <!-- 週次売上推移グラフ -->
            <section class="space-y-2">
              <h2 class="text-sm font-semibold text-slate-700">週次売上推移グラフ</h2>
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
              <ComboChartCard
                v-if="trendLabels.length > 0"
                title="週次売上推移グラフ"
                :labels="trendLabels"
                :series="trendSeries"
                :axes="trendAxes"
              />
              <p
                v-else
                class="rounded-xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-400"
              >
                選択した期間に売上データがありません。
              </p>
            </section>

            <!-- クロス集計 -->
            <section class="space-y-2">
              <h2 class="text-sm font-semibold text-slate-700">クロス集計</h2>
              <div class="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
                <div class="flex flex-wrap items-end gap-3">
                  <div>
                    <label class="mb-1 block text-xs font-medium text-slate-500">行（縦軸）</label>
                    <select
                      v-model="rowDimensionKey"
                      class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                      :disabled="crosstabLoading"
                    >
                      <option v-for="d in rowChoices" :key="d.key" :value="d.key">{{ d.label }}</option>
                    </select>
                  </div>
                  <div>
                    <label class="mb-1 block text-xs font-medium text-slate-500">列（横軸）</label>
                    <select
                      v-model="columnDimensionKey"
                      class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                      :disabled="crosstabLoading"
                    >
                      <option v-for="d in colChoices" :key="d.key" :value="d.key">{{ d.label }}</option>
                    </select>
                  </div>
                  <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
                    <label
                      v-for="m in DETAIL_METRICS"
                      :key="m.key"
                      class="flex items-center gap-1.5 text-sm"
                      :class="
                        availableMetrics.includes(m.key)
                          ? 'cursor-pointer text-slate-700'
                          : 'cursor-not-allowed text-slate-300'
                      "
                    >
                      <input
                        type="checkbox"
                        :checked="selectedMetrics.includes(m.key)"
                        :disabled="!availableMetrics.includes(m.key)"
                        class="accent-indigo-600 disabled:opacity-40"
                        @change="toggleMetric(m.key)"
                      >
                      {{ m.label }}
                    </label>
                  </div>
                </div>
                <p v-if="hasTimeAxis" class="mt-1 text-xs text-slate-400">
                  在日・消化率・店頭在庫は時間軸（年・四半期・月）との組合せでは表示されません。
                </p>
              </div>

              <StatusBlock
                :loading="crosstabLoading"
                :error="crosstabError"
                :empty="crosstab !== null && crosstab.rowLabels.length === 0"
                empty-message="該当するデータがありません。期間を見直してください。"
              >
                <CrossTabTable
                  v-if="crosstab"
                  :data="crosstab"
                  :selected-metrics="selectedMetrics"
                  :metrics="DETAIL_METRICS"
                  display-mode="stacked"
                />
              </StatusBlock>
            </section>
          </div>
        </StatusBlock>
      </div>
    </StatusBlock>
  </div>
</template>
