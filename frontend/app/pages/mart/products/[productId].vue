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
  ItemDetailResponse,
  KpiCardItem,
  MasterProductDetail,
  ProductPage,
  TemperatureArea,
  WeeklySeriesPoint,
  WeeklySeriesResponse,
} from '~/types/api'
import type { ComboChartAxis, ComboChartSeries } from '~/components/ComboChartCard.vue'
import type { ItemDetailRowCategory } from '~/utils/itemDetail'

useHead({ title: '商品詳細分析 | UndeuxSales' })

const route = useRoute()
const router = useRouter()
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()
// 年度の選択肢（/api/filters）を共有から読む。加えて「クロス集計」ページへドリルダウンする際は
// 共有フィルタ（mart-filter）をリセットし、この商品のスコープ（業態・品番・年度）だけを引き継ぐ（openCrosstab）。
const { filter, options: sharedOptions, optionsError, loadOptions, years, reset: resetSharedFilter } = useFilters('mart-filter')

const productId = computed(() => String(route.params.productId ?? ''))

// ---------------------------------------------------------------
// マスタ（画像・基本情報・SKU属性）
// ---------------------------------------------------------------

const detail = ref<MasterProductDetail | null>(null)
const masterLoading = ref(true)
const masterError = ref<string | null>(null)
const notFound = ref(false)

// productId の高速往復（back/forward）で旧商品の応答が後着しても表示を上書きしないための世代。
let masterRequestSeq = 0

async function loadMaster(): Promise<void> {
  const seq = ++masterRequestSeq
  masterLoading.value = true
  masterError.value = null
  notFound.value = false
  try {
    const result = await get<MasterProductDetail>(`/api/product-master/${productId.value}`)
    if (seq !== masterRequestSeq) return
    detail.value = result
  } catch (error) {
    if (seq !== masterRequestSeq) return
    // UNDX-DATA-002 = productId 不正/未登録（商品マスタ詳細ページと同じ判定）。
    if (extractApiError(error)?.errorCode === 'UNDX-DATA-002') {
      notFound.value = true
      detail.value = null
    } else {
      masterError.value = apiErrorMessage(error)
    }
  } finally {
    if (seq === masterRequestSeq) {
      masterLoading.value = false
    }
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

/**
 * 商品の自然キー + 期間を mart API のクエリへ変換する。
 * 呼び出し側は detail 取得済みであること（マスタ未取得のまま商品スコープなしの
 * 全社集計へ静かに縮退しないよう、summary を必須引数で受ける）。
 */
function martQueryOf(summary: import('~/types/api').MasterProductSummary): Record<string, unknown> {
  const query: Record<string, unknown> = {
    businessTypes: [summary.businessCategoryCd],
    shohinKigos: [summary.productSign],
    hinbans: [summary.productTypeCrd],
  }
  if (year.value !== null) {
    query.from = `${year.value}-01-01`
    query.to = `${year.value}-12-31`
  }
  return query
}

/**
 * 商品詳細分析（/api/mart/item-detail）用のクエリ。item-detail は業態×商品記号×品番で絞る
 * （martQueryOf の shohinKigos/hinbans キーとは別名のため専用に組む）。表示側で配下SKUに再絞り込みする。
 */
function itemDetailQueryOf(summary: import('~/types/api').MasterProductSummary): Record<string, unknown> {
  const query: Record<string, unknown> = {
    businessTypes: [summary.businessCategoryCd],
    productSign: summary.productSign,
    productCode: summary.productTypeCrd,
    limit: 300,
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
const itemDetail = ref<ItemDetailResponse | null>(null)
const analyticsLoading = ref(true)
const analyticsError = ref<string | null>(null)

// 配下SKU週次明細（商品詳細分析の表）で表示する行区分（この商品スコープでは気温は共通のため省く）。
const PRODUCT_DETAIL_CATEGORIES: ItemDetailRowCategory[] = ['quantity', 'stock', 'stockDays', 'salePrice']

// 期間・エリア切替の連打で古い応答が後着しても表示を上書きしないためのリクエスト世代。
// analyticsRequestSeq は全体ロード、weeklyRequestSeq は週次のみ取得（loadWeeklyOnly）用。
// 分離する理由は loadWeeklyOnly の doc コメントを参照。
let analyticsRequestSeq = 0
let weeklyRequestSeq = 0

async function loadAnalytics(): Promise<void> {
  // await を跨いでも商品スコープが変わらないよう、呼出時点の summary を捕捉する。
  const summary = detail.value?.summary
  if (!summary) return
  const seq = ++analyticsRequestSeq
  // 全体ロードは週次系列も取得するため、in-flight の週次のみ取得（loadWeeklyOnly）を無効化する。
  ++weeklyRequestSeq
  analyticsLoading.value = true
  analyticsError.value = null
  try {
    await refreshStatus()
    if (seq !== analyticsRequestSeq) return
    if (!isBuilt.value) {
      martSummary.value = null
      weekly.value = null
      skuPerformance.value = null
      itemDetail.value = null
      return
    }
    const query = martQueryOf(summary)
    const [summaryResult, weeklyResult, skuResult, itemDetailResult] = await Promise.all([
      get<import('~/types/api').MartSummaryResponse>('/api/mart/summary', query),
      get<WeeklySeriesResponse>('/api/mart/weekly-series', { ...query, area: area.value }),
      get<ProductPage>('/api/mart/products', {
        ...query,
        sort: 'salesAmount',
        order: 'desc',
        page: 1,
        pageSize: 200,
      }),
      get<ItemDetailResponse>('/api/mart/item-detail', itemDetailQueryOf(summary)),
    ])
    if (seq !== analyticsRequestSeq) return
    martSummary.value = summaryResult
    weekly.value = weeklyResult
    skuPerformance.value = skuResult
    itemDetail.value = itemDetailResult
  } catch (error) {
    if (seq === analyticsRequestSeq) {
      analyticsError.value = apiErrorMessage(error)
    }
  } finally {
    if (seq === analyticsRequestSeq) {
      analyticsLoading.value = false
    }
  }
}

/**
 * 週次系列のみ再取得する（エリア変更用。サマリー・SKU実績の再取得とローディング表示を避ける）。
 *
 * 世代は analyticsRequestSeq と「分離」する: 共有すると、全体ロードの実行中に本関数が
 * 世代を進めた場合、全体ロード側の finally が自分の世代でなくなり analyticsLoading が
 * 解除されない（スピナー固着）。現状の UI ではエリア選択は analytics ロード中は非描画で
 * 到達しないが、セレクト配置の変更で顕在化し得るため構造的に防ぐ。
 * 逆方向（本関数の実行中に全体ロードが開始）は、全体ロード開始時に weeklyRequestSeq も
 * 進めて本関数の古い応答を破棄し、全体ロード側の週次結果（最新条件）を正とする。
 */
async function loadWeeklyOnly(): Promise<void> {
  const summary = detail.value?.summary
  if (!summary || !isBuilt.value) return
  const seq = ++weeklyRequestSeq
  try {
    const result = await get<WeeklySeriesResponse>('/api/mart/weekly-series', {
      ...martQueryOf(summary),
      area: area.value,
    })
    if (seq !== weeklyRequestSeq) return
    weekly.value = result
  } catch (error) {
    if (seq === weeklyRequestSeq) {
      analyticsError.value = apiErrorMessage(error)
    }
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
  { key: 'thumbnail', label: '画像', format: (_row: SkuInfoRow) => '', frozen: true, width: 56 },
  { key: 'unitCd', label: '単品CD', frozen: true },
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
// 商品詳細分析（配下SKU週次明細）— {id} 配下のSKU（同一 業態×記号×品番）のみに絞る。
// ---------------------------------------------------------------
const itemDetailView = computed(() => {
  if (!itemDetail.value) return null
  const v = buildItemDetailView(itemDetail.value)
  const s = detail.value?.summary
  if (!s) return v
  const rows = v.rows.filter(
    (r) =>
      r.gyotaiCode === s.businessCategoryCd
      && r.shohinKigou === s.productSign
      && r.hinbanCode === s.productTypeCrd,
  )
  return { weeks: v.weeks, rows, latestWeek: v.latestWeek, truncated: v.truncated }
})

// ---------------------------------------------------------------
// クロス集計（専用ページ /mart/crosstab へ集約）
//
// 詳細なクロス集計（カラー×サイズ等）は各ページに埋め込まず「クロス集計」メニューへ一本化した。
// ここではこの商品のスコープ（業態・品番・年度）を共有フィルタ（mart-filter）へ引き継いでから
// 専用ページへ遷移するドリルダウンだけを提供する（在庫マネジメント等の既存ドリルダウンと同パターン）。
// ---------------------------------------------------------------

function openCrosstab(): void {
  const summary = detail.value?.summary
  if (!summary) return
  // 共有フィルタ（mart-filter）を一旦リセットし、この商品の業態×品番(3桁)＋表示中の年度だけを
  // 引き継いでクロス集計ページへ遷移する（複数商品ドリルダウンでの絞り込み蓄積・他ページの残存
  // フィルタ混入を防ぐ。set で置換するため addToFilter の累積は使わない）。
  // 共有フィルタに 商品記号 の軸が無いため、スコープは品番3桁単位（同一品番の兄弟商品を含む）。
  resetSharedFilter()
  filter.value.year = year.value
  filter.value.businessTypes = [summary.businessCategoryCd]
  filter.value.hinbans = [summary.productTypeCrd]
  void navigateTo({
    path: '/mart/crosstab',
    query: { rowDimension: 'category:color', columnDimension: 'category:size' },
  })
}

// ---------------------------------------------------------------
// 再取得トリガ
// ---------------------------------------------------------------

const initialized = ref(false)

// 期間（年度）は全 mart 集計（サマリー・週次・SKU実績）に効く。
watch(year, () => {
  if (!initialized.value) return
  void loadAnalytics()
})

// エリアは週次系列の気温にのみ効くため、週次系列だけを再取得する。
watch(area, () => {
  if (!initialized.value) return
  void loadWeeklyOnly()
})

function goBack(): void {
  if (window.history.length > 1) {
    router.back()
  } else {
    void router.push('/mart/products')
  }
}

/** マスタ取得から mart 集計まで全体を読み直す（初期表示・productId 変更時）。 */
async function reloadAll(): Promise<void> {
  await loadMaster()
  if (detail.value) {
    // 直接アクセス（mart ページを経由しない流入）でも集計が無言で空にならないよう、
    // 構築状態を先に取得してから集計をロードする
    // （refreshStatus は冪等。失敗時は loadAnalytics 側の catch でエラー表示される）。
    await refreshStatus().catch(() => undefined)
    await loadAnalytics()
  }
}

// 同一ルートの再利用（別商品の詳細への遷移）でも表示を読み直す（商品マスタ詳細と同パターン）。
watch(productId, () => {
  void reloadAll()
})

onMounted(async () => {
  await loadOptions()
  await reloadAll()
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
        <!-- フィルタ（期間）。画像・基本情報の上に置き、以降の mart 集計（サマリー・SKU実績・週次・クロス集計）に共通適用する -->
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
          <p v-if="optionsError" class="text-xs text-amber-600">
            期間の選択肢の取得に失敗しました（全期間で表示しています）: {{ optionsError }}
          </p>
          <p v-else-if="!sharedOptions" class="text-xs text-slate-400">期間の選択肢を読み込み中...</p>
        </div>

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

        <!-- StatusBlock を外側にし、エラーが「未構築」表示にマスクされないようにする
             （mart 各ページと同じ入れ子。loading 中も未構築通知のフラッシュを防ぐ）。 -->
        <StatusBlock :loading="analyticsLoading" :error="analyticsError">
          <MartNotBuiltNotice v-if="!isBuilt" />
          <div v-else class="space-y-4">
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
              <p class="mb-2 text-xs text-slate-400">
                行は商品マスタ登録のSKU。マスタ未登録SKUの実績はサマリー・週次グラフには含まれますが本表には現れません。
              </p>
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

            <!-- 商品詳細分析（配下SKU週次明細）。商品詳細分析ページと同じ週別マトリクス表現。 -->
            <section class="space-y-2">
              <h2 class="text-sm font-semibold text-slate-700">商品詳細分析（配下SKU）</h2>
              <p class="text-xs text-slate-400">
                この商品の配下SKUについて、週ごとの「売数／在庫数／在日／販売価格」を表示します
                （在日の色分け・値下げ週の強調あり）。
              </p>
              <ItemDetailWeeklyTable
                v-if="itemDetailView && itemDetailView.rows.length > 0"
                :weeks="itemDetailView.weeks"
                :rows="itemDetailView.rows"
                :visible-categories="PRODUCT_DETAIL_CATEGORIES"
              />
              <p
                v-else
                class="rounded-xl border border-slate-200 bg-white p-6 text-center text-sm text-slate-400"
              >
                配下SKUの週次明細がありません。
              </p>
            </section>

            <!-- クロス集計（専用メニューへ集約） -->
            <section class="space-y-2">
              <h2 class="text-sm font-semibold text-slate-700">クロス集計</h2>
              <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                <p class="text-sm text-slate-600">
                  カラー×サイズや年・四半期などの詳細なクロス集計は、専用の「クロス集計」ページに集約しました。
                  この商品の業態・品番（品番3桁：{{ detail?.summary.productTypeCrd ?? '—' }}）を条件に引き継いで開きます。
                </p>
                <button
                  type="button"
                  class="mt-3 inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-2 text-sm font-medium text-white transition-colors hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-400"
                  @click="openCrosstab"
                >
                  <ExternalLink class="h-4 w-4" />
                  クロス集計で分析する
                </button>
              </div>
            </section>
          </div>
        </StatusBlock>
      </div>
    </StatusBlock>
  </div>
</template>
