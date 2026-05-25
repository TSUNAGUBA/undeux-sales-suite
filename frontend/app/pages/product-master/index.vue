<script setup lang="ts">
import { ChevronLeft, ChevronRight } from 'lucide-vue-next'
import type {
  MasterFilterOptions,
  MasterProductPage,
  ProductMasterFilterState,
} from '~/types/api'

useHead({ title: '商品マスタ | UndeuxSales' })

const { get } = useApi()

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
const pageSize = ref(24)

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
      <h1 class="text-xl font-bold text-slate-800">商品マスタ</h1>
      <p class="text-sm text-slate-500">
        商品マスタ（m_product / m_product_sku）の登録商品をカード表示します。商品をクリックすると商品軸の分析へ遷移します。
      </p>
    </div>

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
      empty-message="該当する商品マスタが見つかりません。フィルター条件を変更するか、運用側でマスタを投入してください。"
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
            :href="`/product-master/${product.productId}`"
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
