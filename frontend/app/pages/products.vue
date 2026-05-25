<script setup lang="ts">
import { ChevronLeft, ChevronRight } from 'lucide-vue-next'
import type { ProductPage, ProductRow } from '~/types/api'

useHead({ title: '商品別分析 | UndeuxSales' })

const { toQuery, addToFilter, loadOptions } = useFilters()
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
  // 画像セル（slot で描画）。マスタ未登録時はプレースホルダーを表示。
  { key: 'thumbnail', label: '画像', format: (_row: ProductRow) => '' },
  {
    key: 'product',
    label: '品番-単品',
    format: (row: ProductRow) => `${row.hinbanCode}-${row.tanpinCode}`,
  },
  // 商品名はマスタ優先、無ければ既存 hinmei を fallback（ブランドは slot で副表示）。
  {
    key: 'productName',
    label: '商品名',
    format: (row: ProductRow) => row.productName ?? row.hinmei,
  },
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

function handleProductDrill(row: ProductRow): void {
  // 商品マスタが解決できている場合は商品マスタ詳細（SKU 一覧）へ遷移。
  // マスタ未解決時は従来通りクロス集計の単品ドリルへフォールバックする。
  if (row.masterProductId) {
    void navigateTo(`/product-master/${row.masterProductId}`)
    return
  }
  addToFilter('hinbans', row.hinbanCode)
  void navigateTo({ path: '/crosstab', query: { dimension: 'product' } })
}

onMounted(async () => {
  await loadOptions()
  await load()
})

// 在庫日数は「数値が大きい=売り切るまで長い=死に筋」「数値が小さい=すぐ売り切れる=売れ筋」と
// 他の指標と意味が逆転するため、順序ドロップダウンのラベル（売れ筋/死に筋の補足）を動的に切替える。
const orderLabels = computed(() => {
  const invertedKeys = new Set(['stockDays'])
  const inverted = invertedKeys.has(sort.value)
  return {
    desc: inverted ? '多い順（死に筋）' : '多い順（売れ筋）',
    asc: inverted ? '少ない順（売れ筋）' : '少ない順（死に筋）',
  }
})
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
          <option value="desc">{{ orderLabels.desc }}</option>
          <option value="asc">{{ orderLabels.asc }}</option>
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
          :row-key="(row: ProductRow) => `${row.gyotaiCode}|${row.shohinKigou}|${row.hinbanCode}|${row.tanpinCode}`"
          clickable
          @row-click="handleProductDrill"
        >
          <template #thumbnail="{ row }">
            <div class="h-10 w-10 overflow-hidden rounded">
              <ProductImage
                :src="(row as ProductRow).primaryImageUrl"
                :alt="(row as ProductRow).productName ?? (row as ProductRow).hinmei"
                icon-class="h-4 w-4"
                :show-label="false"
              />
            </div>
          </template>
          <template #productName="{ row }">
            <div class="flex min-w-0 flex-col">
              <span class="truncate text-slate-800">
                {{ (row as ProductRow).productName ?? (row as ProductRow).hinmei }}
              </span>
              <span
                v-if="(row as ProductRow).brand"
                class="truncate text-xs text-slate-400"
              >{{ (row as ProductRow).brand }}</span>
            </div>
          </template>
        </DataTable>

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
