<script setup lang="ts">
/**
 * 商品別分析（/mart/products）の一覧ページ。
 *
 * 商品マスタ一覧（/product-master）と同じ画像カード表現（ProductMasterCard /
 * ProductMasterFilters を再利用）で商品を一覧し、カード押下で商品の詳細分析
 * （/mart/products/{productId}）へ遷移する。
 *
 * 一覧の対象は商品マスタ登録商品（カードの実績値は売上参照データ由来の集計）。
 * マスタ未登録の商品は本一覧には現れない（詳細分析はマスタの自然キーで mart を参照するため）。
 */
import { ChevronLeft, ChevronRight } from 'lucide-vue-next'
import type {
  MasterFilterOptions,
  MasterProductPage,
  ProductMasterFilterState,
} from '~/types/api'

useHead({ title: '商品別分析 | UndeuxSales' })

const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()

function emptyFilter(): ProductMasterFilterState {
  return {
    search: '',
    businessCategoryCds: [],
    divisionCds: [],
    brands: [],
    managers: [],
  }
}

const filter = ref<ProductMasterFilterState>(emptyFilter())
const options = ref<MasterFilterOptions | null>(null)
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

function toQuery(): Record<string, unknown> {
  const query: Record<string, unknown> = {
    page: page.value,
    pageSize: pageSize.value,
  }
  if (filter.value.search) query.search = filter.value.search
  if (filter.value.businessCategoryCds.length > 0) {
    query.businessCategoryCds = filter.value.businessCategoryCds
  }
  if (filter.value.divisionCds.length > 0) {
    query.divisionCds = filter.value.divisionCds
  }
  if (filter.value.brands.length > 0) {
    query.brands = filter.value.brands
  }
  if (filter.value.managers.length > 0) {
    query.managers = filter.value.managers
  }
  return query
}

async function loadOptions(): Promise<void> {
  try {
    options.value = await get<MasterFilterOptions>('/api/product-master/options')
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
    pageData.value = await get<MasterProductPage>('/api/product-master', toQuery())
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
  await loadOptions()
  await load()
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">商品別分析</h1>
      <p class="text-sm text-slate-500">
        商品をカードで一覧します。カードを押すと商品の詳細分析（基本情報・サマリー・SKU情報・週次売上推移・クロス集計）へ遷移します。
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

    <ProductMasterFilters
      v-model="filter"
      :options="options"
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
