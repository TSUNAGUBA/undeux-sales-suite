<script setup lang="ts">
import { ChevronLeft, ChevronRight } from 'lucide-vue-next'
import type { ProductPage, ProductRow } from '~/types/api'

useHead({ title: '商品別分析 | UndeuxSales' })

const { toQuery } = useFilters()
const { get } = useApi()

const sort = ref('salesAmount')
const order = ref<'desc' | 'asc'>('desc')
const page = ref(1)
const pageSize = 20

const productPage = ref<ProductPage | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const sortOptions = [
  { value: 'salesAmount', label: '売上金額' },
  { value: 'salesQuantity', label: '売上数量' },
  { value: 'grossProfit', label: '粗利' },
  { value: 'stock', label: '在庫数' },
  { value: 'sellThroughRate', label: '消化率' },
  { value: 'stockDays', label: '在庫日数' },
]

const totalPages = computed(() => {
  const total = productPage.value?.totalCount ?? 0
  return total === 0 ? 1 : Math.ceil(total / pageSize)
})

const columns = [
  {
    key: 'product',
    label: '品番-単品',
    format: (row: ProductRow) => `${row.hinbanCode}-${row.tanpinCode}`,
  },
  { key: 'hinmei', label: '品名', format: (row: ProductRow) => row.hinmei },
  { key: 'kisetsu', label: '季節', format: (row: ProductRow) => row.kisetsu },
  {
    key: 'salesQuantity',
    label: '売上数量',
    align: 'right' as const,
    format: (row: ProductRow) => formatNumber(row.salesQuantity),
  },
  {
    key: 'salesAmount',
    label: '売上金額',
    align: 'right' as const,
    format: (row: ProductRow) => formatCurrency(row.salesAmount),
  },
  {
    key: 'grossProfit',
    label: '粗利',
    align: 'right' as const,
    format: (row: ProductRow) => formatCurrency(row.grossProfit),
  },
  {
    key: 'stock',
    label: '在庫数',
    align: 'right' as const,
    format: (row: ProductRow) => formatNumber(row.stock),
  },
  {
    key: 'sellThroughRate',
    label: '消化率',
    align: 'right' as const,
    format: (row: ProductRow) => formatRatioAsPercent(row.sellThroughRate),
  },
  {
    key: 'averageStockDays',
    label: '平均在庫日数',
    align: 'right' as const,
    format: (row: ProductRow) => formatDecimal(row.averageStockDays, 1),
  },
]

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    productPage.value = await get<ProductPage>('/api/products', {
      ...toQuery(),
      sort: sort.value,
      order: order.value,
      page: page.value,
      pageSize,
    })
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

onMounted(load)
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">商品別分析</h1>
      <p class="text-sm text-slate-500">品番・単品別の売上と在庫（売れ筋／死に筋）</p>
    </div>

    <FilterBar @apply="reload" />

    <div class="flex flex-wrap items-end gap-3">
      <div>
        <label class="mb-1 block text-xs font-medium text-slate-500">並び替え</label>
        <select
          v-model="sort"
          class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
          @change="reload"
        >
          <option v-for="opt in sortOptions" :key="opt.value" :value="opt.value">
            {{ opt.label }}
          </option>
        </select>
      </div>
      <div>
        <label class="mb-1 block text-xs font-medium text-slate-500">順序</label>
        <select
          v-model="order"
          class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
          @change="reload"
        >
          <option value="desc">多い順（売れ筋）</option>
          <option value="asc">少ない順（死に筋）</option>
        </select>
      </div>
    </div>

    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="(productPage?.items.length ?? 0) === 0"
      empty-message="該当する商品がありません。"
    >
      <div class="space-y-3">
        <DataTable
          :columns="columns"
          :rows="productPage?.items ?? []"
          :row-key="(row: ProductRow) => `${row.hinbanCode}-${row.tanpinCode}`"
        />

        <div class="flex items-center justify-between text-sm text-slate-600">
          <span>全 {{ formatNumber(productPage?.totalCount ?? 0) }} 件</span>
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
            <span>{{ page }} / {{ totalPages }}</span>
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
