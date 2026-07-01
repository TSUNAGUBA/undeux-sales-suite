<script setup lang="ts">
/**
 * ホーム（ダッシュボード + 目的別メニュー）。
 *
 * サプライヤー向けには最上部に「全体モニタリング > 全社サマリー」のカード型サマリー（主要KPI）と、
 * 「本日のインサイト & おすすめ」（今週の急上昇アイテム TOP3・全社売上の予算達成率メーター）を
 * ルールベースの一言コメント付きで表示する（LLM 基盤が無いため、所見は主要指標から機械生成する）。
 * その下に、目的（カテゴリ）を選んでドリルダウンするポータル型メニュー（categoriesForRole）を置く。
 *
 * ダッシュボードは分析 mart 由来（/api/mart/*）で、未構築時・バイヤー時は非表示にしてメニューのみ出す。
 * 予算は予算管理（localStorage）の全社売上予算を参照する。急上昇は当年/前年の商品別実績から算出する。
 */
import {
  Boxes,
  CircleDollarSign,
  Flame,
  Gauge,
  Layers,
  Package,
  Percent,
  ShoppingCart,
  Sparkles,
  Target,
  TrendingUp,
} from 'lucide-vue-next'
import type { KpiCardItem, MartSummaryResponse, ProductPage, ProductRow } from '~/types/api'
import type { RisingItem } from '~/utils/homeInsights'

useHead({ title: 'ホーム | UndeuxSales' })

const { accountType, meta } = useAccountType()
const categories = computed(() => categoriesForRole(accountType.value))
const isSupplier = computed(() => accountType.value === 'supplier')

// ---------------------------------------------------------------
// ダッシュボード（サプライヤーのみ・分析 mart 由来）
// ---------------------------------------------------------------
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()
const { loadOptions, years } = useFilters('mart-filter')
const { load: loadBudget, companyBudget } = useBudget()

const summary = ref<MartSummaryResponse | null>(null)
const risingItems = ref<RisingItem[]>([])
const referenceYear = ref<number | null>(null)
const dashboardLoading = ref(true)
const dashboardError = ref<string | null>(null)

const kpiItems = computed<KpiCardItem[]>(() => {
  const kpi = summary.value?.kpi
  if (!kpi) return []
  return [
    { label: '売上金額', value: formatCurrency(kpi.amount), icon: CircleDollarSign, accentClass: 'bg-indigo-50 text-indigo-600' },
    { label: '売上数量', value: `${formatNumber(kpi.quantity)} 点`, icon: ShoppingCart, accentClass: 'bg-sky-50 text-sky-600' },
    { label: '粗利', value: formatCurrency(kpi.grossProfit), icon: TrendingUp, accentClass: 'bg-emerald-50 text-emerald-600' },
    { label: '粗利率', value: formatRatioAsPercent(kpi.grossProfitRate), icon: Percent, accentClass: 'bg-amber-50 text-amber-600' },
    { label: '在庫数', value: `${formatNumber(kpi.currentStock)} 点`, icon: Boxes, accentClass: 'bg-rose-50 text-rose-600' },
    { label: '消化率', value: formatRatioAsPercent(kpi.sellThroughRate), icon: Gauge, accentClass: 'bg-teal-50 text-teal-600' },
    { label: '商品数', value: `${formatNumber(kpi.productCount)} 品`, icon: Package, accentClass: 'bg-violet-50 text-violet-600' },
    { label: 'SKU数', value: `${formatNumber(kpi.skuCount)} 件`, icon: Layers, accentClass: 'bg-cyan-50 text-cyan-600' },
  ]
})

const budgetAchievement = computed(() => {
  const actual = summary.value?.kpi.amount ?? 0
  const year = referenceYear.value
  const entry = year !== null ? companyBudget(year) : undefined
  return computeBudgetAchievement(actual, entry?.salesBudget ?? null)
})
const budgetGaugeWidth = computed(() => {
  const pct = budgetAchievement.value.percent
  return pct === null ? 0 : Math.max(0, Math.min(100, pct))
})
const budgetCommentText = computed(() => budgetComment(budgetAchievement.value))
const risingCommentText = computed(() => risingComment(risingItems.value))

/** 急上昇アイテムの遷移先（商品マスタ結合済みは商品別分析の詳細へ、無ければ商品別分析一覧へ）。 */
function risingItemHref(item: RisingItem): string {
  return item.masterProductId ? `/mart/products/${item.masterProductId}` : '/mart/products'
}

async function loadDashboard(): Promise<void> {
  dashboardLoading.value = true
  dashboardError.value = null
  try {
    await refreshStatus()
    if (!isBuilt.value) {
      summary.value = null
      risingItems.value = []
      return
    }
    const year = referenceYear.value
    const range = year !== null ? { from: `${year}-01-01`, to: `${year}-12-31` } : {}
    const [summaryRes, curProducts] = await Promise.all([
      get<MartSummaryResponse>('/api/mart/summary', range),
      get<ProductPage>('/api/mart/products', {
        ...range,
        sort: 'salesQuantity',
        order: 'desc',
        page: 1,
        pageSize: 100,
      }),
    ])
    summary.value = summaryRes
    // 前年同期の商品別実績（伸び率算出用）。補助情報のため取得失敗は無視して売れ筋 fallback にする。
    let prevProducts: ProductRow[] | null = null
    if (year !== null) {
      try {
        const prev = await get<ProductPage>('/api/mart/products', {
          from: `${year - 1}-01-01`,
          to: `${year - 1}-12-31`,
          sort: 'salesQuantity',
          order: 'desc',
          page: 1,
          pageSize: 100,
        })
        prevProducts = prev.items
      } catch {
        prevProducts = null
      }
    }
    risingItems.value = computeRisingItems(curProducts.items, prevProducts, 3)
  } catch (error) {
    dashboardError.value = apiErrorMessage(error)
  } finally {
    dashboardLoading.value = false
  }
}

onMounted(async () => {
  if (!isSupplier.value) return
  loadBudget()
  await loadOptions()
  referenceYear.value = years.value.length > 0
    ? years.value[years.value.length - 1]!
    : new Date().getFullYear()
  await loadDashboard()
})
</script>

<template>
  <div class="mx-auto w-full max-w-6xl space-y-8">
    <!-- ダッシュボード（サプライヤーのみ・mart 構築時） -->
    <section v-if="isSupplier">
      <StatusBlock :loading="dashboardLoading" :error="dashboardError">
        <div v-if="isBuilt && summary" class="space-y-5">
          <!-- カード型サマリー（全社サマリー由来） -->
          <div>
            <div class="mb-2 flex items-baseline justify-between gap-2">
              <h2 class="text-lg font-bold text-slate-800">
                全社サマリー<span
                  v-if="referenceYear !== null"
                  class="ml-1 text-sm font-normal text-slate-400"
                >（{{ referenceYear }}年）</span>
              </h2>
              <NuxtLink to="/mart" class="shrink-0 text-xs font-medium text-indigo-600 hover:text-indigo-800">
                全体モニタリングで詳しく見る →
              </NuxtLink>
            </div>
            <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
              <KpiCard
                v-for="item in kpiItems"
                :key="item.label"
                :label="item.label"
                :value="item.value"
                :icon="item.icon"
                :accent-class="item.accentClass"
              />
            </div>
          </div>

          <!-- 本日のインサイト & おすすめ -->
          <div>
            <div class="mb-2 flex items-center gap-1.5">
              <Sparkles class="h-5 w-5 text-indigo-500" />
              <h2 class="text-lg font-bold text-slate-800">本日のインサイト & おすすめ</h2>
            </div>
            <div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
              <!-- 今週の急上昇 TOP3 -->
              <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                <div class="mb-3 flex items-center gap-1.5">
                  <Flame class="h-4 w-4 text-rose-500" />
                  <h3 class="text-sm font-bold text-slate-800">今週の急上昇！アイテム TOP3</h3>
                </div>
                <div v-if="risingItems.length > 0" class="grid grid-cols-3 gap-2">
                  <NuxtLink
                    v-for="(item, idx) in risingItems"
                    :key="item.key"
                    :to="risingItemHref(item)"
                    class="group flex flex-col gap-1.5 rounded-lg border border-slate-100 p-2 transition-colors hover:border-indigo-300 hover:bg-indigo-50/40"
                    :title="item.name"
                  >
                    <div class="relative aspect-square w-full overflow-hidden rounded-md bg-slate-100">
                      <ProductImage :src="item.imageUrl" :alt="item.name" icon-class="h-8 w-8" :show-label="false" />
                      <span
                        class="absolute left-1 top-1 flex h-5 w-5 items-center justify-center rounded-full bg-white/90 text-xs font-bold text-slate-600 shadow-sm"
                      >{{ idx + 1 }}</span>
                      <span
                        v-if="item.growthPercent !== null && item.growthPercent > 0"
                        class="absolute bottom-1 right-1 inline-flex items-center gap-0.5 rounded-full bg-emerald-500/90 px-1.5 py-0.5 text-[10px] font-bold text-white shadow-sm"
                      >
                        <TrendingUp class="h-2.5 w-2.5" />{{ signedPercentText(item.growthPercent) }}
                      </span>
                    </div>
                    <p class="line-clamp-1 text-xs font-medium text-slate-700 group-hover:text-indigo-700" :title="item.name">
                      {{ item.name }}
                    </p>
                  </NuxtLink>
                </div>
                <p v-else class="py-6 text-center text-sm text-slate-400">
                  対象期間に売上実績のあるアイテムがありません。
                </p>
                <div class="mt-3 flex items-start gap-1.5 rounded-lg bg-indigo-50/60 px-3 py-2">
                  <Sparkles class="mt-0.5 h-3.5 w-3.5 shrink-0 text-indigo-400" />
                  <p class="text-xs leading-relaxed text-slate-600">{{ risingCommentText }}</p>
                </div>
              </div>

              <!-- 予算達成率メーター（全社売上） -->
              <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
                <div class="mb-3 flex items-center gap-1.5">
                  <Target class="h-4 w-4 text-indigo-500" />
                  <h3 class="text-sm font-bold text-slate-800">目標達成状況（全社売上）</h3>
                </div>

                <template v-if="budgetAchievement.percent !== null">
                  <div class="mb-1 flex items-baseline justify-between">
                    <span class="text-xs text-slate-500">
                      {{ referenceYear }}年 実績 {{ formatCurrency(budgetAchievement.actual) }}
                      <span class="text-slate-400">/ 予算 {{ formatCurrency(budgetAchievement.budget) }}</span>
                    </span>
                    <span
                      class="text-2xl font-bold tabular-nums"
                      :class="budgetAchievement.reached ? 'text-emerald-600' : 'text-indigo-600'"
                    >{{ formatPercent(budgetAchievement.percent) }}</span>
                  </div>
                  <div
                    class="h-3.5 w-full overflow-hidden rounded-full bg-slate-100"
                    role="progressbar"
                    :aria-valuenow="Math.round(budgetAchievement.percent)"
                    aria-valuemin="0"
                    aria-valuemax="100"
                    aria-label="全社売上の予算達成率"
                  >
                    <div
                      class="h-full rounded-full bg-gradient-to-r from-indigo-400 via-indigo-500 to-emerald-400 transition-all"
                      :style="{ width: budgetGaugeWidth + '%' }"
                    />
                  </div>
                  <p v-if="!budgetAchievement.reached" class="mt-1 text-right text-xs text-slate-400">
                    達成まであと {{ formatPercent(budgetAchievement.remainingPercent ?? 0) }}
                  </p>
                </template>
                <div v-else class="rounded-lg border border-dashed border-slate-200 px-3 py-4 text-center">
                  <p class="text-sm text-slate-500">全社売上予算が未登録です。</p>
                  <NuxtLink to="/mart/budget" class="mt-1 inline-block text-xs font-medium text-indigo-600 hover:text-indigo-800">
                    予算管理で登録する →
                  </NuxtLink>
                </div>

                <div class="mt-3 flex items-start gap-1.5 rounded-lg bg-indigo-50/60 px-3 py-2">
                  <Sparkles class="mt-0.5 h-3.5 w-3.5 shrink-0 text-indigo-400" />
                  <p class="text-xs leading-relaxed text-slate-600">{{ budgetCommentText }}</p>
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- mart 未構築時は控えめな案内（メニューは下に出す） -->
        <div
          v-else-if="!isBuilt"
          class="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700"
        >
          ダッシュボードを表示するには分析 mart の構築が必要です。
          <NuxtLink to="/mart" class="font-medium underline">全社サマリー</NuxtLink>
          で「mart を再構築」を実行してください。
        </div>
      </StatusBlock>
    </section>

    <!-- 目的別メニュー -->
    <section>
      <div class="mb-4">
        <h1 class="text-xl font-bold text-slate-800">目的を選ぶ</h1>
        <p class="mt-1 text-sm text-slate-500">
          {{ meta.label }}としてログイン中です。目的を選んでください。選んだ先では、関連するページだけがタブで並びます。
        </p>
      </div>

      <div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <HomeCategoryCard
          v-for="category in categories"
          :key="category.id"
          :category="category"
        />
      </div>
    </section>
  </div>
</template>
