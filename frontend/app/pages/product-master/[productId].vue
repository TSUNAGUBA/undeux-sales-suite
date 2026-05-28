<script setup lang="ts">
import {
  ArrowLeft,
  ShoppingBag,
  TrendingUp,
  Boxes,
  Percent,
  CalendarDays,
  Activity,
  Clock,
  AlertTriangle,
  AlertOctagon,
  CheckCircle,
  Minus,
  ExternalLink,
} from 'lucide-vue-next'
import type {
  MasterProductDetail,
  MasterProductSku,
  ProductAnalyticsResponse,
  ProductSkuPerformance,
} from '~/types/api'

useHead({ title: '商品詳細 | UndeuxSales' })

const route = useRoute()
const router = useRouter()
const { get } = useApi()
// 商品単位の分析は売上参照スイート内で /product-analytics と同じスコープを共有する。
// 同期間で両画面を行き来できる利点を維持する（CLAUDE.md 原則3 既存パターンの再利用）。
const { filter, loadOptions } = useFilters('product-analytics-filter')

const productId = computed(() => String(route.params.productId ?? ''))

const detail = ref<MasterProductDetail | null>(null)
const analytics = ref<ProductAnalyticsResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)
const analyticsErrorMessage = ref<string | null>(null)
const notFound = ref(false)

// 表示中の SKU（クリックで切り替え）。null のときは商品マスタの代表画像 (Summary.primaryImageUrl)。
const selectedSku = ref<MasterProductSku | null>(null)
const selectedImageIdx = ref(0)

// ============================================================
// 業界経験則に基づく閾値（衣料品向け）。
// マジックナンバーを script setup の const にまとめ、調整時の影響範囲を局所化する。
// ============================================================
const THRESHOLD_STOCK_DAYS_OVERSTOCK = 90 // 90日超で過剰在庫
const THRESHOLD_STOCK_DAYS_CAUTION = 60 // 60日超で注意（滞留疑い）
const THRESHOLD_STOCK_DAYS_HEALTHY = 30 // 30日以下で健全
const THRESHOLD_SELL_THROUGH_HIGH = 0.8 // 消化率 80% 以上で売れ筋
const THRESHOLD_SELL_THROUGH_LOW = 0.3 // 消化率 30% 未満で要警戒
const THRESHOLD_GROSS_PROFIT_RATE_LOW = 0.1 // 粗利率 10% 未満で注意
const THRESHOLD_SHARE_HOT = 15 // 構成比 15% 以上で売れ筋
const THRESHOLD_NEAR_STOCKOUT_RATIO = 0.1 // 在庫が期間内売上数量の 10% 以下で在庫切れ間近
const NEAR_STOCKOUT_MIN_STOCK = 3 // 在庫切れ間近判定の最低数量

// ============================================================
// データ取得
// ============================================================
async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  analyticsErrorMessage.value = null
  notFound.value = false
  try {
    // 2 つの API を並列取得し、片方の失敗は他方の表示を阻害しない（CLAUDE.md 原則4）。
    const [masterResult, analyticsResult] = await Promise.allSettled([
      get<MasterProductDetail>(`/api/product-master/${productId.value}`),
      get<ProductAnalyticsResponse>(
        `/api/product-analytics/${productId.value}`,
        toAnalyticsQuery(),
      ),
    ])

    if (masterResult.status === 'fulfilled') {
      detail.value = masterResult.value
    } else {
      const extracted = extractApiError(masterResult.reason)
      if (extracted?.errorCode === 'UNDX-DATA-002') {
        notFound.value = true
        detail.value = null
        analytics.value = null
        return
      }
      errorMessage.value = apiErrorMessage(masterResult.reason)
      detail.value = null
    }

    if (analyticsResult.status === 'fulfilled') {
      analytics.value = analyticsResult.value
    } else {
      analytics.value = null
      // マスタが取れていれば、売上 API の失敗は警告として表示する（致命的にしない）。
      analyticsErrorMessage.value = apiErrorMessage(analyticsResult.reason)
    }

    // データ再取得後は画像選択を初期化する。
    selectedSku.value = null
    selectedImageIdx.value = 0
  } finally {
    loading.value = false
  }
}

function toAnalyticsQuery(): Record<string, unknown> {
  const year = filter.value.year
  if (year === null) return {}
  return { from: `${year}-01-01`, to: `${year}-12-31` }
}

// ============================================================
// 期間トグル: 全期間 / 前年 / 今年。
// ============================================================
const currentYear = new Date().getFullYear()
const periodOptions = computed(() => [
  { value: null as number | null, label: '全期間' },
  { value: currentYear - 1, label: `${currentYear - 1}年` },
  { value: currentYear, label: `${currentYear}年` },
])
function setPeriod(year: number | null): void {
  filter.value.year = year
  void load()
}

// ============================================================
// SKU 結合: マスタ（unitCd）と売上（unitCd）を結合し、SKU マトリクスの行を作る。
// マスタにあって売上にない SKU は数量・金額・在庫ゼロのプレースホルダ行として出す。
// ============================================================
interface SkuMatrixRow extends ProductSkuPerformance {
  master: MasterProductSku | null
  /** クライアント側で導出した近似在庫日数（null なら算出不可）。 */
  estimatedStockDays: number | null
}

const periodDays = computed<number>(() => {
  const weeks = analytics.value?.weeklyTrend.length ?? 0
  return weeks > 0 ? weeks * 7 : 0
})

function deriveStockDays(quantity: number, stock: number): number | null {
  if (periodDays.value === 0 || quantity <= 0) return null
  const dailyVelocity = quantity / periodDays.value
  if (dailyVelocity <= 0) return null
  return stock / dailyVelocity
}

const skuMatrixRows = computed<SkuMatrixRow[]>(() => {
  const perf = analytics.value?.bySku ?? []
  const masters = detail.value?.skus ?? []
  const masterByUnit = new Map(masters.map((m) => [m.unitCd, m]))
  const perfByUnit = new Map(perf.map((p) => [p.unitCd, p]))

  const rows: SkuMatrixRow[] = perf.map((p) => ({
    ...p,
    master: masterByUnit.get(p.unitCd) ?? null,
    estimatedStockDays: deriveStockDays(p.quantity, p.stock),
  }))

  // マスタにあるが売上に存在しない SKU は 0 埋めで追加。
  for (const m of masters) {
    if (perfByUnit.has(m.unitCd)) continue
    rows.push({
      unitCd: m.unitCd,
      colorName: m.colorName,
      sizeName: m.sizeName,
      salesPrice: m.salesPrice,
      primaryImageUrl: m.images[0]?.imageUrl ?? null,
      quantity: 0,
      amount: 0,
      grossProfit: 0,
      stock: 0,
      sharePercent: 0,
      master: m,
      estimatedStockDays: null,
    })
  }

  // 売上金額降順、同額ならカラー → サイズ → 単品コード昇順で安定化。
  rows.sort((a, b) => {
    if (b.amount !== a.amount) return b.amount - a.amount
    const byColor = (a.colorName ?? '').localeCompare(b.colorName ?? '', 'ja')
    if (byColor !== 0) return byColor
    const bySize = compareSize(a.sizeName, b.sizeName)
    if (bySize !== 0) return bySize
    return a.unitCd.localeCompare(b.unitCd)
  })
  return rows
})

// ============================================================
// 状態バッジ判定（優先度順に1つだけ返す）。
// ============================================================
type SkuBadgeKind =
  | 'hot' // 売れ筋
  | 'near-stockout' // 在庫切れ間近
  | 'stagnant' // 滞留（在庫あり・売上ゼロ）
  | 'sold-out' // 完売
  | 'aging' // 滞留疑い（在庫日数高め）
  | 'master-only' // マスタのみ（売上・在庫なし）
  | 'normal' // 通常

interface SkuBadge {
  kind: SkuBadgeKind
  label: string
  icon: typeof TrendingUp
  className: string
}

function badgeFor(row: SkuMatrixRow): SkuBadge {
  const { quantity, stock, sharePercent, estimatedStockDays } = row

  if (quantity > 0 && sharePercent >= THRESHOLD_SHARE_HOT) {
    return {
      kind: 'hot',
      label: '売れ筋',
      icon: TrendingUp,
      className: 'bg-emerald-100 text-emerald-700',
    }
  }
  if (
    quantity > 0
    && stock > 0
    && stock <= Math.max(quantity * THRESHOLD_NEAR_STOCKOUT_RATIO, NEAR_STOCKOUT_MIN_STOCK)
  ) {
    return {
      kind: 'near-stockout',
      label: '在庫切れ間近',
      icon: AlertTriangle,
      className: 'bg-amber-100 text-amber-700',
    }
  }
  if (stock > 0 && quantity === 0) {
    return {
      kind: 'stagnant',
      label: '要警戒（滞留）',
      icon: AlertOctagon,
      className: 'bg-rose-100 text-rose-700',
    }
  }
  if (stock === 0 && quantity > 0) {
    return {
      kind: 'sold-out',
      label: '完売',
      icon: CheckCircle,
      className: 'bg-slate-100 text-slate-700',
    }
  }
  if (estimatedStockDays !== null && estimatedStockDays >= THRESHOLD_STOCK_DAYS_CAUTION) {
    return {
      kind: 'aging',
      label: '滞留疑い',
      icon: Clock,
      className: 'bg-amber-50 text-amber-700',
    }
  }
  if (stock === 0 && quantity === 0) {
    return {
      kind: 'master-only',
      label: 'マスタのみ',
      icon: Minus,
      className: 'bg-slate-50 text-slate-400',
    }
  }
  return {
    kind: 'normal',
    label: '通常',
    icon: Activity,
    className: 'bg-slate-50 text-slate-500',
  }
}

// ============================================================
// アラート（KPI 全体 + SKU 単位の集計）。
// ============================================================
interface AlertItem {
  tone: 'warning' | 'danger' | 'positive'
  icon: typeof AlertTriangle
  message: string
}

const alerts = computed<AlertItem[]>(() => {
  if (!analytics.value) return []
  const list: AlertItem[] = []
  const kpi = analytics.value.kpi

  if (kpi.amount === 0 && kpi.currentStock > 0) {
    list.push({
      tone: 'warning',
      icon: AlertTriangle,
      message: '選択期間内に売上がありません（在庫あり）。期間を見直すか、取込状況をご確認ください。',
    })
  }
  if (kpi.averageStockDays >= THRESHOLD_STOCK_DAYS_OVERSTOCK) {
    list.push({
      tone: 'warning',
      icon: AlertTriangle,
      message: `過剰在庫の傾向: 平均在庫日数 ${formatDecimal(kpi.averageStockDays, 0)} 日`,
    })
  }

  const rows = skuMatrixRows.value
  const stagnantCount = rows.filter((r) => r.stock > 0 && r.quantity === 0).length
  const nearStockoutCount = rows.filter((r) => badgeFor(r).kind === 'near-stockout').length
  if (stagnantCount > 0) {
    list.push({
      tone: 'warning',
      icon: AlertOctagon,
      message: `滞留 SKU が ${stagnantCount} 件（在庫ありで期間内売上ゼロ）`,
    })
  }
  if (nearStockoutCount > 0) {
    list.push({
      tone: 'danger',
      icon: AlertTriangle,
      message: `在庫切れ間近 SKU が ${nearStockoutCount} 件`,
    })
  }
  if (kpi.sellThroughRate >= THRESHOLD_SELL_THROUGH_HIGH) {
    list.push({
      tone: 'positive',
      icon: TrendingUp,
      message: `高消化率: ${formatRatioAsPercent(kpi.sellThroughRate)}（売れ筋確認）`,
    })
  }
  return list
})

function alertContainerClass(tone: AlertItem['tone']): string {
  switch (tone) {
    case 'danger':
      return 'border-rose-200 bg-rose-50 text-rose-700'
    case 'positive':
      return 'border-emerald-200 bg-emerald-50 text-emerald-700'
    default:
      return 'border-amber-200 bg-amber-50 text-amber-700'
  }
}

// ============================================================
// KPI 色（動的アクセント）。
// ============================================================
function sellThroughAccent(rate: number): string {
  if (rate >= 0.7) return 'bg-emerald-50 text-emerald-600'
  if (rate < THRESHOLD_SELL_THROUGH_LOW) return 'bg-rose-50 text-rose-600'
  return 'bg-slate-100 text-slate-600'
}
function stockDaysAccent(days: number): string {
  if (days >= THRESHOLD_STOCK_DAYS_OVERSTOCK) return 'bg-rose-50 text-rose-600'
  if (days >= THRESHOLD_STOCK_DAYS_CAUTION) return 'bg-amber-50 text-amber-600'
  if (days <= THRESHOLD_STOCK_DAYS_HEALTHY) return 'bg-emerald-50 text-emerald-600'
  return 'bg-slate-100 text-slate-600'
}
function grossProfitAccent(rate: number): string {
  if (rate < THRESHOLD_GROSS_PROFIT_RATE_LOW) {
    return 'bg-amber-50 text-amber-600'
  }
  return 'bg-emerald-50 text-emerald-600'
}

// ============================================================
// 派生値（ヒーロー・価格・KPI 補助）。
// ============================================================
const heroImageUrl = computed<string | null>(() => {
  if (selectedSku.value) {
    const images = selectedSku.value.images
    if (images.length === 0) return null
    const idx = Math.min(selectedImageIdx.value, images.length - 1)
    return images[idx]?.imageUrl ?? null
  }
  return detail.value?.summary.primaryImageUrl ?? null
})

const priceLabel = computed(() => {
  const s = detail.value?.summary
  if (!s) return '—'
  const min = s.minSalesPrice
  const max = s.maxSalesPrice
  if (min === null && max === null) return '—'
  if (min !== null && max !== null && min !== max) {
    return `${formatCurrency(min)} 〜 ${formatCurrency(max)}`
  }
  return formatCurrency(max ?? min ?? 0)
})

// 想定在庫金額（bySku の stock × salesPrice 合計）。
const estimatedStockValue = computed<number>(() => {
  const rows = analytics.value?.bySku ?? []
  return rows.reduce((sum, r) => sum + r.stock * r.salesPrice, 0)
})

// 構成比バーの色（売れ筋は emerald、それ以外は indigo）。
function shareBarClass(sharePercent: number): string {
  return sharePercent >= THRESHOLD_SHARE_HOT ? 'bg-emerald-500' : 'bg-indigo-500'
}

// ============================================================
// チャート用データ。
// ============================================================
const trendLabels = computed(() =>
  (analytics.value?.weeklyTrend ?? []).map((p) => p.date.slice(5)),
)
const trendSeries = computed(() => [
  {
    label: '売上金額',
    data: (analytics.value?.weeklyTrend ?? []).map((p) => p.amount),
    color: '#4f46e5',
  },
  {
    label: '粗利',
    data: (analytics.value?.weeklyTrend ?? []).map((p) => p.grossProfit),
    color: '#10b981',
  },
])

const businessTypeLabels = computed(() =>
  (analytics.value?.byBusinessType ?? []).map((b) =>
    b.displayName
      ? `${b.businessCategoryCd} ${b.displayName}${b.shortName ? ` (${b.shortName})` : ''}`
      : b.businessCategoryCd,
  ),
)
const businessTypeData = computed(() =>
  (analytics.value?.byBusinessType ?? []).map((b) => b.amount),
)
const showBusinessTypeChart = computed(() => businessTypeLabels.value.length >= 2)

// ============================================================
// 操作・ライフサイクル。
// ============================================================
function selectSku(row: SkuMatrixRow): void {
  if (row.master) {
    selectedSku.value = row.master
    selectedImageIdx.value = 0
    if (typeof window !== 'undefined') {
      window.scrollTo({ top: 0, behavior: 'smooth' })
    }
  }
}

function selectMasterSku(sku: MasterProductSku): void {
  selectedSku.value = sku
  selectedImageIdx.value = 0
}

function clearSelection(): void {
  selectedSku.value = null
  selectedImageIdx.value = 0
}

function goBack(): void {
  router.back()
}

watch(productId, () => {
  void load()
})

onMounted(async () => {
  await loadOptions()
  await load()
})

// ============================================================
// 表示制御（売上データの有無判定）。
// ============================================================
const hasAnalytics = computed(() => analytics.value !== null)
const hasSalesData = computed(() => {
  const a = analytics.value
  return !!a && (a.kpi.amount > 0 || a.weeklyTrend.length > 0 || a.bySku.length > 0)
})
</script>

<template>
  <div class="space-y-4">
    <!-- 戻る・関連リンク -->
    <div class="flex flex-wrap items-center gap-2">
      <button
        type="button"
        class="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 text-xs text-slate-600 hover:bg-slate-50"
        @click="goBack"
      >
        <ArrowLeft class="h-3.5 w-3.5" />
        戻る
      </button>
      <NuxtLink
        to="/product-master"
        class="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 text-xs text-slate-600 hover:bg-slate-50"
      >
        商品マスタ一覧へ
      </NuxtLink>
      <NuxtLink
        v-if="detail"
        :to="`/product-analytics/${productId}`"
        class="ml-auto inline-flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 text-xs text-slate-600 hover:bg-slate-50"
      >
        商品分析の詳細を見る
        <ExternalLink class="h-3.5 w-3.5" />
      </NuxtLink>
    </div>

    <div
      v-if="notFound"
      class="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-700"
    >
      指定された商品マスタが見つかりません。URL の productId を確認してください。
    </div>

    <StatusBlock
      v-else
      :loading="loading"
      :error="errorMessage"
      :empty="!detail"
      empty-message="表示する商品データがありません。"
    >
      <div v-if="detail" class="space-y-4">
        <!-- Header Hero: 画像 + 基本情報 + 期間トグル -->
        <div class="grid grid-cols-1 gap-4 md:grid-cols-[280px_minmax(0,1fr)]">
          <!-- 画像カード -->
          <div class="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
            <div class="relative aspect-square w-full overflow-hidden rounded-lg bg-slate-100">
              <ProductImage
                :src="heroImageUrl"
                :alt="detail.summary.productName"
                icon-class="h-12 w-12"
                label-class="text-xs"
              />
              <span
                v-if="selectedSku"
                class="absolute left-2 top-2 rounded-full bg-indigo-600 px-2 py-0.5 text-xs font-medium text-white shadow-sm"
              >
                {{ selectedSku.colorName }} / {{ selectedSku.sizeName }}
              </span>
            </div>

            <div
              v-if="selectedSku && selectedSku.images.length > 1"
              class="mt-2 flex flex-wrap gap-1"
            >
              <button
                v-for="(img, i) in selectedSku.images"
                :key="img.imageId"
                type="button"
                class="h-10 w-10 overflow-hidden rounded ring-2 transition-opacity"
                :class="
                  selectedImageIdx === i
                    ? 'ring-indigo-500 opacity-100'
                    : 'ring-transparent opacity-60 hover:opacity-100'
                "
                :title="`画像 ${i + 1}`"
                @click="selectedImageIdx = i"
              >
                <ProductImage
                  :src="img.imageUrl"
                  alt=""
                  icon-class="h-4 w-4"
                  :show-label="false"
                />
              </button>
            </div>

            <button
              v-if="selectedSku"
              type="button"
              class="mt-2 w-full rounded-lg border border-slate-300 px-2 py-1 text-xs text-slate-600 hover:bg-slate-50"
              @click="clearSelection"
            >
              代表画像に戻す
            </button>
          </div>

          <!-- 商品情報カード -->
          <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
            <div class="flex flex-wrap items-start justify-between gap-3">
              <div class="flex min-w-0 flex-col gap-2">
                <span
                  v-if="detail.summary.brand"
                  class="self-start rounded-full bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-700"
                >
                  {{ detail.summary.brand }}
                </span>
                <h1 class="text-lg font-bold leading-snug text-slate-800">
                  {{ detail.summary.productName }}
                </h1>
                <p class="text-xs text-slate-500">
                  {{ detail.summary.divisionName }} ・ {{ detail.summary.businessCategorySign }}
                  <span v-if="detail.summary.manager"> ・ 担当: {{ detail.summary.manager }}</span>
                </p>
              </div>

              <!-- 期間セグメントトグル（PC は右上、モバイルは下に折り返し） -->
              <div
                class="inline-flex shrink-0 rounded-lg border border-slate-300 text-xs"
                role="tablist"
                aria-label="集計期間"
              >
                <button
                  v-for="opt in periodOptions"
                  :key="opt.label"
                  type="button"
                  role="tab"
                  :aria-selected="filter.year === opt.value"
                  class="px-3 py-2 transition-colors first:rounded-l-lg last:rounded-r-lg"
                  :class="
                    filter.year === opt.value
                      ? 'bg-indigo-600 font-semibold text-white'
                      : 'text-slate-600 hover:bg-slate-50'
                  "
                  @click="setPeriod(opt.value)"
                >
                  {{ opt.label }}
                </button>
              </div>
            </div>

            <dl class="mt-3 grid grid-cols-2 gap-x-3 gap-y-2 border-t border-slate-100 pt-3 text-xs sm:grid-cols-4">
              <div>
                <dt class="text-slate-400">価格</dt>
                <dd class="font-semibold text-slate-700">{{ priceLabel }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">SKU 数</dt>
                <dd class="font-semibold text-slate-700">
                  {{ formatNumber(detail.summary.skuCount) }}
                </dd>
              </div>
              <div>
                <dt class="text-slate-400">カラー</dt>
                <dd class="font-semibold text-slate-700">{{ formatNumber(detail.summary.colorCount) }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">サイズ</dt>
                <dd class="font-semibold text-slate-700">{{ formatNumber(detail.summary.sizeCount) }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">業態</dt>
                <dd class="font-semibold text-slate-700">{{ detail.summary.businessCategoryCd }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">商品記号</dt>
                <dd class="font-mono font-semibold text-slate-700">{{ detail.summary.productSign }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">品番</dt>
                <dd class="font-mono font-semibold text-slate-700">{{ detail.summary.productTypeCrd }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">最新週</dt>
                <dd class="font-semibold text-slate-700">
                  {{ analytics?.kpi.latestWeek ?? '—' }}
                </dd>
              </div>
            </dl>
          </div>
        </div>

        <!-- Alert Strip -->
        <div v-if="alerts.length > 0" class="flex flex-col gap-2">
          <div
            v-for="(alert, i) in alerts"
            :key="i"
            class="flex items-start gap-2 rounded-xl border px-3 py-2 text-sm"
            :class="alertContainerClass(alert.tone)"
          >
            <component :is="alert.icon" class="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
            <span>{{ alert.message }}</span>
          </div>
        </div>

        <!-- 売上 API 失敗時の通知（非ブロッキング） -->
        <div
          v-if="analyticsErrorMessage"
          class="rounded-xl border border-amber-200 bg-amber-50 p-3 text-sm text-amber-700"
        >
          売上分析データの取得に失敗しました: {{ analyticsErrorMessage }}
        </div>

        <!-- 売上データなし時のヒント -->
        <div
          v-else-if="hasAnalytics && !hasSalesData"
          class="rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm text-slate-600"
        >
          この商品は選択期間内に売上データが取り込まれていません。期間を「全期間」に切り替えるか、商品マスタ詳細をご確認ください。
        </div>

        <!-- KPI Strip（6 枚） -->
        <div
          v-if="analytics"
          class="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6"
          aria-label="商品 KPI"
        >
          <KpiCard
            label="売上金額"
            :value="formatCurrency(analytics.kpi.amount)"
            :sub="`数量 ${formatNumber(analytics.kpi.quantity)}`"
            :icon="ShoppingBag"
            accent-class="bg-indigo-50 text-indigo-600"
          />
          <KpiCard
            label="粗利"
            :value="formatCurrency(analytics.kpi.grossProfit)"
            :sub="`粗利率 ${formatRatioAsPercent(analytics.kpi.grossProfitRate)}`"
            :icon="Percent"
            :accent-class="grossProfitAccent(analytics.kpi.grossProfitRate)"
          />
          <KpiCard
            label="現在在庫"
            :value="formatNumber(analytics.kpi.currentStock)"
            :sub="`想定在庫額 ${formatCurrency(estimatedStockValue)}`"
            :icon="Boxes"
            accent-class="bg-amber-50 text-amber-600"
          />
          <KpiCard
            label="消化率"
            :value="formatRatioAsPercent(analytics.kpi.sellThroughRate)"
            :sub="
              analytics.kpi.sellThroughRate >= THRESHOLD_SELL_THROUGH_HIGH
                ? '売れ筋'
                : analytics.kpi.sellThroughRate < THRESHOLD_SELL_THROUGH_LOW
                  ? '要警戒'
                  : '標準'
            "
            :icon="Activity"
            :accent-class="sellThroughAccent(analytics.kpi.sellThroughRate)"
          />
          <KpiCard
            label="平均在庫日数"
            :value="`${formatDecimal(analytics.kpi.averageStockDays, 1)} 日`"
            :sub="`目安 ${THRESHOLD_STOCK_DAYS_CAUTION} 日以内`"
            :icon="CalendarDays"
            :accent-class="stockDaysAccent(analytics.kpi.averageStockDays)"
          />
          <KpiCard
            label="最新週"
            :value="analytics.kpi.latestWeek ?? '—'"
            sub="データ更新日"
            :icon="Clock"
            accent-class="bg-slate-100 text-slate-600"
          />
        </div>

        <!-- 週次トレンド + 業態別売上 -->
        <div v-if="analytics && trendLabels.length > 0" class="grid grid-cols-1 gap-3 lg:grid-cols-12">
          <div :class="showBusinessTypeChart ? 'lg:col-span-8' : 'lg:col-span-12'">
            <LineChartCard
              title="週次トレンド（売上金額・粗利）"
              :labels="trendLabels"
              :series="trendSeries"
            />
          </div>
          <div v-if="showBusinessTypeChart" class="lg:col-span-4">
            <BarChartCard
              title="業態別売上"
              :labels="businessTypeLabels"
              :data="businessTypeData"
              color="#10b981"
              series-label="売上金額"
              horizontal
            />
          </div>
        </div>

        <!-- SKU マトリクス -->
        <div
          v-if="skuMatrixRows.length > 0"
          id="sku-matrix"
          class="rounded-xl border border-slate-200 bg-white shadow-sm"
        >
          <div class="border-b border-slate-100 px-4 py-3">
            <h2 class="text-sm font-semibold text-slate-700">
              SKU 別実績（{{ formatNumber(skuMatrixRows.length) }} 件）
            </h2>
            <p class="mt-0.5 text-xs text-slate-400">
              売上金額の降順。行クリックで上部画像が切り替わります。在庫日数は期間内平均からの単純推計です。
            </p>
          </div>

          <!-- PC: テーブル -->
          <div class="hidden overflow-x-auto sm:block">
            <table class="w-full text-sm">
              <thead class="bg-slate-50 text-xs text-slate-500">
                <tr>
                  <th class="px-3 py-2 text-left">画像</th>
                  <th class="px-3 py-2 text-left">単品コード</th>
                  <th class="px-3 py-2 text-left">カラー</th>
                  <th class="px-3 py-2 text-left">サイズ</th>
                  <th class="px-3 py-2 text-right">売価</th>
                  <th class="px-3 py-2 text-right">数量</th>
                  <th class="px-3 py-2 text-right">売上金額</th>
                  <th class="px-3 py-2 text-left">構成比</th>
                  <th class="px-3 py-2 text-right">在庫</th>
                  <th class="px-3 py-2 text-right">在庫日数</th>
                  <th class="px-3 py-2 text-left">状態</th>
                </tr>
              </thead>
              <tbody>
                <tr
                  v-for="row in skuMatrixRows"
                  :key="row.unitCd"
                  tabindex="0"
                  role="button"
                  :aria-label="`SKU ${row.unitCd} を選択`"
                  class="cursor-pointer border-b border-slate-100 transition-colors hover:bg-slate-50 focus:bg-slate-100 focus:outline-none last:border-0"
                  :class="[
                    selectedSku?.unitCd === row.unitCd ? 'bg-indigo-50' : '',
                    badgeFor(row).kind === 'stagnant' ? 'bg-rose-50/40' : '',
                    badgeFor(row).kind === 'master-only' ? 'opacity-60' : '',
                  ]"
                  @click="selectSku(row)"
                  @keydown.enter.prevent="selectSku(row)"
                  @keydown.space.prevent="selectSku(row)"
                >
                  <td class="px-3 py-2">
                    <div class="h-10 w-10 overflow-hidden rounded">
                      <ProductImage
                        :src="row.primaryImageUrl ?? row.master?.images[0]?.imageUrl ?? null"
                        :alt="`${row.colorName} / ${row.sizeName}`"
                        icon-class="h-4 w-4"
                        :show-label="false"
                      />
                    </div>
                  </td>
                  <td class="px-3 py-2 font-mono text-xs text-slate-500">{{ row.unitCd }}</td>
                  <td class="px-3 py-2 text-slate-700">{{ row.colorName || '—' }}</td>
                  <td class="px-3 py-2 text-slate-700">{{ row.sizeName || '—' }}</td>
                  <td class="px-3 py-2 text-right tabular-nums text-slate-700">
                    {{ row.salesPrice > 0 ? formatCurrency(row.salesPrice) : '—' }}
                  </td>
                  <td class="px-3 py-2 text-right tabular-nums text-slate-700">
                    {{ formatNumber(row.quantity) }}
                  </td>
                  <td class="px-3 py-2 text-right tabular-nums font-semibold text-slate-800">
                    {{ formatCurrency(row.amount) }}
                  </td>
                  <td class="px-3 py-2">
                    <div class="flex min-w-[6rem] items-center gap-2">
                      <span class="tabular-nums text-xs text-slate-600">
                        {{ formatPercent(row.sharePercent) }}
                      </span>
                      <div
                        class="relative h-1.5 flex-1 overflow-hidden rounded-full bg-slate-100"
                        role="progressbar"
                        :aria-valuenow="row.sharePercent"
                        aria-valuemin="0"
                        aria-valuemax="100"
                      >
                        <div
                          class="absolute inset-y-0 left-0 rounded-full"
                          :class="shareBarClass(row.sharePercent)"
                          :style="{ width: `${Math.min(100, row.sharePercent)}%` }"
                        />
                      </div>
                    </div>
                  </td>
                  <td class="px-3 py-2 text-right tabular-nums text-slate-700">
                    {{ formatNumber(row.stock) }}
                  </td>
                  <td class="px-3 py-2 text-right tabular-nums text-slate-700">
                    <span v-if="row.estimatedStockDays !== null" :title="'期間内平均からの推計値'">
                      {{ formatDecimal(row.estimatedStockDays, 0) }} 日
                    </span>
                    <span v-else class="text-slate-300">—</span>
                  </td>
                  <td class="px-3 py-2">
                    <span
                      class="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium"
                      :class="badgeFor(row).className"
                    >
                      <component :is="badgeFor(row).icon" class="h-3 w-3" aria-hidden="true" />
                      {{ badgeFor(row).label }}
                    </span>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>

          <!-- モバイル: カード -->
          <div class="flex flex-col gap-2 p-3 sm:hidden">
            <button
              v-for="row in skuMatrixRows"
              :key="row.unitCd"
              type="button"
              class="flex flex-col gap-2 rounded-lg border p-3 text-left transition-colors"
              :class="[
                selectedSku?.unitCd === row.unitCd
                  ? 'border-indigo-400 bg-indigo-50'
                  : 'border-slate-200',
                badgeFor(row).kind === 'stagnant' ? 'bg-rose-50/40' : '',
                badgeFor(row).kind === 'master-only' ? 'opacity-60' : '',
              ]"
              @click="selectSku(row)"
            >
              <div class="flex items-start gap-3">
                <div class="h-14 w-14 shrink-0 overflow-hidden rounded">
                  <ProductImage
                    :src="row.primaryImageUrl ?? row.master?.images[0]?.imageUrl ?? null"
                    :alt="`${row.colorName} / ${row.sizeName}`"
                    icon-class="h-5 w-5"
                    :show-label="false"
                  />
                </div>
                <div class="min-w-0 flex-1">
                  <p class="font-mono text-xs text-slate-500">{{ row.unitCd }}</p>
                  <p class="text-sm font-semibold text-slate-800">
                    {{ row.colorName || '—' }} / {{ row.sizeName || '—' }}
                  </p>
                </div>
                <span
                  class="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-medium"
                  :class="badgeFor(row).className"
                >
                  <component :is="badgeFor(row).icon" class="h-3 w-3" aria-hidden="true" />
                  {{ badgeFor(row).label }}
                </span>
              </div>
              <dl class="grid grid-cols-2 gap-x-3 gap-y-1.5">
                <div>
                  <dt class="text-[11px] text-slate-400">売上金額</dt>
                  <dd class="text-sm font-semibold text-slate-900">{{ formatCurrency(row.amount) }}</dd>
                </div>
                <div>
                  <dt class="text-[11px] text-slate-400">構成比</dt>
                  <dd>
                    <div class="flex items-center gap-2">
                      <span class="text-sm text-slate-700">{{ formatPercent(row.sharePercent) }}</span>
                      <div
                        class="relative h-1.5 flex-1 overflow-hidden rounded-full bg-slate-100"
                        role="progressbar"
                        :aria-valuenow="row.sharePercent"
                        aria-valuemin="0"
                        aria-valuemax="100"
                      >
                        <div
                          class="absolute inset-y-0 left-0 rounded-full"
                          :class="shareBarClass(row.sharePercent)"
                          :style="{ width: `${Math.min(100, row.sharePercent)}%` }"
                        />
                      </div>
                    </div>
                  </dd>
                </div>
                <div>
                  <dt class="text-[11px] text-slate-400">数量</dt>
                  <dd class="text-sm text-slate-700">{{ formatNumber(row.quantity) }}</dd>
                </div>
                <div>
                  <dt class="text-[11px] text-slate-400">在庫</dt>
                  <dd class="text-sm text-slate-700">{{ formatNumber(row.stock) }}</dd>
                </div>
                <div>
                  <dt class="text-[11px] text-slate-400">売価</dt>
                  <dd class="text-sm text-slate-700">
                    {{ row.salesPrice > 0 ? formatCurrency(row.salesPrice) : '—' }}
                  </dd>
                </div>
                <div>
                  <dt class="text-[11px] text-slate-400">在庫日数</dt>
                  <dd class="text-sm text-slate-700">
                    <span v-if="row.estimatedStockDays !== null">
                      {{ formatDecimal(row.estimatedStockDays, 0) }} 日
                    </span>
                    <span v-else class="text-slate-300">—</span>
                  </dd>
                </div>
              </dl>
            </button>
          </div>
        </div>

        <!-- 商品マスタ詳細（折りたたみ、default closed） -->
        <CollapsiblePanel
          v-if="detail.skus.length > 0"
          title="商品マスタ詳細（売価・原価・画像）"
          :default-open="false"
        >
          <div class="hidden overflow-x-auto sm:block">
            <table class="w-full text-sm">
              <thead class="bg-slate-50 text-xs text-slate-500">
                <tr>
                  <th class="px-3 py-2 text-left">画像</th>
                  <th class="px-3 py-2 text-left">単品コード</th>
                  <th class="px-3 py-2 text-left">カラー</th>
                  <th class="px-3 py-2 text-left">サイズ</th>
                  <th class="px-3 py-2 text-right">売価</th>
                  <th class="px-3 py-2 text-right">原価</th>
                  <th class="px-3 py-2 text-right">画像数</th>
                </tr>
              </thead>
              <tbody>
                <tr
                  v-for="sku in detail.skus"
                  :key="sku.skuItemId"
                  class="cursor-pointer border-b border-slate-100 hover:bg-slate-50 last:border-0"
                  :class="selectedSku?.skuItemId === sku.skuItemId ? 'bg-indigo-50' : ''"
                  @click="selectMasterSku(sku)"
                >
                  <td class="px-3 py-2">
                    <div class="h-10 w-10 overflow-hidden rounded">
                      <ProductImage
                        :src="sku.images[0]?.imageUrl ?? null"
                        :alt="`${sku.colorName} / ${sku.sizeName}`"
                        icon-class="h-4 w-4"
                        :show-label="false"
                      />
                    </div>
                  </td>
                  <td class="px-3 py-2 font-mono text-xs text-slate-500">{{ sku.unitCd }}</td>
                  <td class="px-3 py-2 text-slate-700">{{ sku.colorName || '—' }}</td>
                  <td class="px-3 py-2 text-slate-700">{{ sku.sizeName || '—' }}</td>
                  <td class="px-3 py-2 text-right tabular-nums text-slate-700">
                    {{ sku.salesPrice > 0 ? formatCurrency(sku.salesPrice) : '—' }}
                  </td>
                  <td class="px-3 py-2 text-right tabular-nums text-slate-700">
                    {{ sku.costPrice > 0 ? formatCurrency(sku.costPrice) : '—' }}
                  </td>
                  <td class="px-3 py-2 text-right tabular-nums text-slate-700">
                    {{ formatNumber(sku.images.length) }}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>

          <div class="flex flex-col gap-2 sm:hidden">
            <button
              v-for="sku in detail.skus"
              :key="sku.skuItemId"
              type="button"
              class="flex items-start gap-3 rounded-lg border border-slate-200 p-3 text-left"
              :class="selectedSku?.skuItemId === sku.skuItemId ? 'border-indigo-400 bg-indigo-50' : ''"
              @click="selectMasterSku(sku)"
            >
              <div class="h-14 w-14 shrink-0 overflow-hidden rounded">
                <ProductImage
                  :src="sku.images[0]?.imageUrl ?? null"
                  :alt="`${sku.colorName} / ${sku.sizeName}`"
                  icon-class="h-5 w-5"
                  :show-label="false"
                />
              </div>
              <div class="min-w-0 flex-1">
                <p class="font-mono text-xs text-slate-500">{{ sku.unitCd }}</p>
                <p class="text-sm font-semibold text-slate-800">
                  {{ sku.colorName || '—' }} / {{ sku.sizeName || '—' }}
                </p>
                <dl class="mt-1 grid grid-cols-2 gap-x-3 gap-y-1">
                  <div>
                    <dt class="text-[11px] text-slate-400">売価</dt>
                    <dd class="text-sm text-slate-700">
                      {{ sku.salesPrice > 0 ? formatCurrency(sku.salesPrice) : '—' }}
                    </dd>
                  </div>
                  <div>
                    <dt class="text-[11px] text-slate-400">原価</dt>
                    <dd class="text-sm text-slate-700">
                      {{ sku.costPrice > 0 ? formatCurrency(sku.costPrice) : '—' }}
                    </dd>
                  </div>
                </dl>
              </div>
            </button>
          </div>
        </CollapsiblePanel>
      </div>
    </StatusBlock>
  </div>
</template>
