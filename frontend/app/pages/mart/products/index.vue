<script setup lang="ts">
/**
 * 商品別分析（/mart/products）の一覧ページ。
 *
 * フィルターは「全社サマリー」を踏襲（業態・部門・年度・季節・棚割1・平均在庫日数）し、末尾に
 * ブランド・担当者・キーワードを加える（ProductAnalysisFilters）。全社サマリー踏襲分は専用スコープ
 * 'product-analysis-filter' の useFilters（SalesFilterState）へ、ブランド・担当者・キーワードは
 * ローカル ref（ProductExtraFilterState）へ保持し、両者をマージして /api/product-master へ渡す。
 *
 * 一覧の対象は商品マスタ登録商品（カードの実績値は売上参照データ由来の集計）。期間・部門・季節・
 * 棚割1・在日のいずれかを指定すると、その条件で売上のある商品に絞り込まれる（バックエンドの
 * sales_weekly EXISTS）。カード押下で商品の詳細分析（/mart/products/{productId}）へ遷移する。
 */
import { ChevronLeft, ChevronRight } from 'lucide-vue-next'
import type { MasterFilterOptions, MasterProductPage } from '~/types/api'
import type { ProductExtraFilterState } from '~/components/ProductAnalysisFilters.vue'

useHead({ title: '商品別分析 | UndeuxSales' })

// 全社サマリー踏襲フィルターは専用スコープで保持（他 mart ページの 'mart-filter' とは分離する）。
const FILTER_SCOPE = 'product-analysis-filter'
const { toQuery, loadOptions } = useFilters(FILTER_SCOPE)
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()

// ブランド・担当者・キーワード（全社サマリー踏襲フィルターの末尾に付与）。
const extraFilter = ref<ProductExtraFilterState>({ brands: [], managers: [], search: '' })
const masterOptions = ref<MasterFilterOptions | null>(null)
const optionsError = ref<string | null>(null)

const pageData = ref<MasterProductPage | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const page = ref(1)
const pageSize = ref(12)

const totalPages = computed(() => {
  const total = pageData.value?.totalCount ?? 0
  return total === 0 ? 1 : Math.ceil(total / pageSize.value)
})

/** 全社サマリー踏襲フィルター（toQuery）＋ ブランド・担当者・キーワード ＋ ページング。 */
function buildQuery(): Record<string, unknown> {
  const query: Record<string, unknown> = {
    ...toQuery(),
    page: page.value,
    pageSize: pageSize.value,
  }
  if (extraFilter.value.brands.length > 0) query.brands = extraFilter.value.brands
  if (extraFilter.value.managers.length > 0) query.managers = extraFilter.value.managers
  if (extraFilter.value.search) query.search = extraFilter.value.search
  return query
}

async function loadMasterOptions(): Promise<void> {
  try {
    masterOptions.value = await get<MasterFilterOptions>('/api/product-master/options')
    optionsError.value = null
  } catch (error) {
    optionsError.value = apiErrorMessage(error)
  }
}

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    // 詳細分析は mart を参照するため、一覧段階で構築状態を共有 state に反映しておく
    // （未構築でも一覧自体は表示できる。詳細側でガードが出る）。
    await refreshStatus().catch(() => undefined)
    pageData.value = await get<MasterProductPage>('/api/product-master', buildQuery())
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

function reload(): void {
  page.value = 1
  load()
}

function changePage(delta: number): void {
  const next = page.value + delta
  if (next >= 1 && next <= totalPages.value) {
    page.value = next
    load()
  }
}

onMounted(async () => {
  await Promise.all([loadOptions(), loadMasterOptions()])
  await load()
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">商品別分析</h1>
      <p class="text-sm text-slate-500">
        商品をカードで一覧します。フィルターは全社サマリーと同じ条件（業態・部門・年度・季節・棚割1・平均在庫日数）に
        ブランド・担当者・キーワードを加えたものです。カードを押すと商品の詳細分析（基本情報・サマリー・SKU情報・週次売上推移・クロス集計）へ遷移します。
      </p>
    </div>

    <p
      v-if="!isBuilt"
      class="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700"
    >
      分析 mart が未構築のため、詳細分析の集計は表示できません。
      <NuxtLink to="/mart" class="font-medium underline">全社サマリー</NuxtLink>
      で「mart を再構築」を実行してください。
    </p>

    <ProductAnalysisFilters
      v-model="extraFilter"
      :scope-key="FILTER_SCOPE"
      :options="masterOptions"
      :options-error="optionsError"
      @apply="reload"
      @reset="reload"
    />

    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="(pageData?.items.length ?? 0) === 0"
      empty-message="該当する商品が見つかりません。フィルター条件を変更するか、商品マスタを投入してください。"
    >
      <div class="space-y-3">
        <div class="flex items-center justify-between text-sm text-slate-600">
          <span>全 {{ formatNumber(pageData?.totalCount ?? 0) }} 件</span>
        </div>

        <div class="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
          <ProductMasterCard
            v-for="product in pageData?.items ?? []"
            :key="product.productId"
            :product="product"
            :href="`/mart/products/${product.productId}`"
          />
        </div>

        <div class="flex items-center justify-between text-sm text-slate-600">
          <span>{{ page }} / {{ totalPages }} ページ</span>
          <div class="flex items-center gap-2">
            <button
              type="button"
              class="flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 disabled:opacity-40"
              :disabled="page <= 1"
              @click="changePage(-1)"
            >
              <ChevronLeft class="h-4 w-4" />
              前へ
            </button>
            <button
              type="button"
              class="flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 disabled:opacity-40"
              :disabled="page >= totalPages"
              @click="changePage(1)"
            >
              次へ
              <ChevronRight class="h-4 w-4" />
            </button>
          </div>
        </div>
      </div>
    </StatusBlock>
  </div>
</template>
