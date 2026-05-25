<script setup lang="ts">
import { ChevronLeft, ChevronRight, Search, RotateCcw, Package } from 'lucide-vue-next'
import type { CrosstabResponse, CrosstabRow } from '~/types/api'

useHead({ title: 'クロス集計 | UndeuxSales' })

const route = useRoute()
const router = useRouter()
const { filter, toQuery, reset, addToFilter, loadOptions } = useFilters()
const { get } = useApi()

const dimension = ref<string>(
  typeof route.query.dimension === 'string' ? route.query.dimension : 'hinban',
)
const selectedMetrics = ref<string[]>([
  'amount',
  'quantity',
  'grossProfit',
  'sharePercent',
  'stockDays',
])

const result = ref<CrosstabResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const dimensionOptions = [
  { value: 'hinban', label: '品番3桁' },
  { value: 'product', label: '単品（品番-単品）' },
  { value: 'department', label: '部門' },
  { value: 'customer', label: '取引先' },
  { value: 'businessType', label: '業態' },
  { value: 'season', label: '季節区分' },
  { value: 'color', label: 'カラー' },
  { value: 'size', label: 'サイズ' },
  { value: 'chohyoKubun', label: '帳票区分' },
  { value: 'tanawari1', label: '棚割1' },
  { value: 'tanawari2', label: '棚割2' },
  { value: 'shohinKigo', label: '商品記号' },
]

const allMetricOptions = [
  { value: 'amount', label: '売上金額' },
  { value: 'quantity', label: '売上数量' },
  { value: 'grossProfit', label: '粗利' },
  { value: 'sharePercent', label: '構成比率' },
  { value: 'stockDays', label: '在日（平均）' },
  { value: 'sellThroughRate', label: '消化率' },
  { value: 'stock', label: '在庫数' },
]

const metricSelectOptions = computed(() =>
  allMetricOptions.map((o) => ({ value: o.value, text: o.label })),
)

const isProductDimension = computed(() => dimension.value === 'product')

const keyLabel = computed(
  () =>
    dimensionOptions.find((o) => o.value === dimension.value)?.label ?? '区分',
)

const drillableDimensions = new Set([
  'department',
  'customer',
  'businessType',
  'season',
  'hinban',
  // 'product' は行クリックで一律ドリルできないため除外。代わりに productName 列のセル内
  // リンク（マスタ解決時のみ表示）から商品軸分析へ遷移する。
])

const canDrill = computed(() => drillableDimensions.has(dimension.value))

const nextDimLabel = computed(() => {
  if (dimension.value === 'hinban') return '単品'
  return '品番3桁'
})

interface CrosstabColumn {
  key: string
  label: string
  align?: 'left' | 'right'
  format?: (row: CrosstabRow) => string
}

function buildMetricColumn(metricValue: string, label: string): CrosstabColumn {
  switch (metricValue) {
    case 'amount':
      return {
        key: 'amount',
        label,
        align: 'right',
        format: (row: CrosstabRow) => formatCurrency(row.amount),
      }
    case 'quantity':
      return {
        key: 'quantity',
        label,
        align: 'right',
        format: (row: CrosstabRow) => formatNumber(row.quantity),
      }
    case 'grossProfit':
      return {
        key: 'grossProfit',
        label,
        align: 'right',
        format: (row: CrosstabRow) => formatCurrency(row.grossProfit),
      }
    case 'sharePercent':
      return {
        key: 'sharePercent',
        label,
        align: 'right',
        format: (row: CrosstabRow) => formatPercent(row.sharePercent),
      }
    case 'stockDays':
      return {
        key: 'stockDays',
        label,
        align: 'right',
        format: (row: CrosstabRow) => formatDecimal(row.stockDays, 1),
      }
    case 'sellThroughRate':
      return {
        key: 'sellThroughRate',
        label,
        align: 'right',
        format: (row: CrosstabRow) => formatPercent(row.sellThroughRate),
      }
    case 'stock':
      return {
        key: 'stock',
        label,
        align: 'right',
        format: (row: CrosstabRow) => formatNumber(row.stock),
      }
    default:
      return { key: metricValue, label }
  }
}

const tableColumns = computed<CrosstabColumn[]>(() => {
  const columns: CrosstabColumn[] = [
    {
      key: 'label',
      label: keyLabel.value,
      format: (row: CrosstabRow) => (row.label === '' ? '(未設定)' : row.label),
    },
  ]

  if (isProductDimension.value) {
    columns.push(
      {
        key: 'thumbnail',
        label: '画像',
        format: () => '',
      },
      {
        key: 'productName',
        label: '商品名',
        format: (row: CrosstabRow) =>
          row.basicItems?.productName ?? row.basicItems?.hinmei ?? '-',
      },
      {
        key: 'shohinKigo',
        label: '商品記号',
        format: (row: CrosstabRow) => row.basicItems?.shohinKigo ?? '-',
      },
      {
        key: 'color',
        label: 'カラー',
        format: (row: CrosstabRow) => row.basicItems?.color ?? '-',
      },
      {
        key: 'size',
        label: 'サイズ',
        format: (row: CrosstabRow) => row.basicItems?.size ?? '-',
      },
      {
        key: 'kisetsu',
        label: '季節',
        format: (row: CrosstabRow) => row.basicItems?.kisetsu ?? '-',
      },
    )
  }

  for (const metricValue of selectedMetrics.value) {
    const opt = allMetricOptions.find((o) => o.value === metricValue)
    if (!opt) continue
    columns.push(buildMetricColumn(metricValue, opt.label))
  }
  return columns
})

// テーブル表示領域の高さに合わせてページサイズを動的算出する。
// 41px ≈ DataTable の 1 行高（py-2.5 + text-sm + border）。
const ROW_HEIGHT_PX = 41
const HEADER_HEIGHT_PX = 41
const PAGE_SIZE_FALLBACK = 25
const MIN_PAGE_SIZE = 10

const tableContainer = ref<HTMLElement | null>(null)
const containerHeight = ref(0)
let resizeObserver: ResizeObserver | null = null

onMounted(() => {
  if (tableContainer.value && typeof ResizeObserver !== 'undefined') {
    resizeObserver = new ResizeObserver((entries) => {
      const entry = entries[0]
      if (entry) {
        containerHeight.value = entry.contentRect.height
      }
    })
    resizeObserver.observe(tableContainer.value)
  }
})

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  resizeObserver = null
})

const pageSize = computed(() => {
  if (containerHeight.value <= 0) return PAGE_SIZE_FALLBACK
  const usable = Math.max(0, containerHeight.value - HEADER_HEIGHT_PX)
  return Math.max(MIN_PAGE_SIZE, Math.floor(usable / ROW_HEIGHT_PX))
})

const currentPage = ref(1)

const totalRows = computed(() => result.value?.rows.length ?? 0)

const totalPages = computed(() => {
  if (totalRows.value === 0) return 1
  return Math.ceil(totalRows.value / pageSize.value)
})

watch(result, () => {
  currentPage.value = 1
})

watch(totalPages, (newTotal) => {
  if (currentPage.value > newTotal) {
    currentPage.value = Math.max(1, newTotal)
  }
})

const visibleRows = computed<CrosstabRow[]>(() => {
  const rows = result.value?.rows ?? []
  const start = (currentPage.value - 1) * pageSize.value
  return rows.slice(start, start + pageSize.value)
})

const rangeStart = computed(() =>
  totalRows.value === 0 ? 0 : (currentPage.value - 1) * pageSize.value + 1,
)
const rangeEnd = computed(() =>
  Math.min(totalRows.value, currentPage.value * pageSize.value),
)

function changePage(delta: number): void {
  const next = currentPage.value + delta
  if (next >= 1 && next <= totalPages.value) {
    currentPage.value = next
  }
}

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    result.value = await get<CrosstabResponse>('/api/crosstab', {
      ...toQuery(),
      dimension: dimension.value,
    })
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

async function applyAndLoad(): Promise<void> {
  await router.replace({ query: { ...route.query, dimension: dimension.value } })
  await load()
}

function resetAndLoad(): void {
  reset()
  void applyAndLoad()
}

function handleRowDrill(row: CrosstabRow): void {
  const dim = dimension.value

  let nextDim: string | null = null
  if (dim === 'department') {
    addToFilter('departments', row.key)
    nextDim = 'hinban'
  } else if (dim === 'customer') {
    addToFilter('customers', row.key)
    nextDim = 'hinban'
  } else if (dim === 'businessType') {
    addToFilter('businessTypes', row.key)
    nextDim = 'hinban'
  } else if (dim === 'season') {
    addToFilter('seasons', row.key)
    nextDim = 'hinban'
  } else if (dim === 'hinban') {
    addToFilter('hinbans', row.key)
    nextDim = 'product'
  } else {
    return
  }

  dimension.value = nextDim
  void applyAndLoad()
}

onMounted(async () => {
  await loadOptions()
  await load()
})
</script>

<template>
  <div class="flex h-full flex-col gap-4">
    <div class="shrink-0">
      <h1 class="text-xl font-bold text-slate-800">クロス集計</h1>
      <p class="text-sm text-slate-500">
        集計単位ごとに複数のメトリクスを横並び表示。行クリックでドリルダウン可能。
      </p>
    </div>

    <div class="shrink-0">
      <CollapsiblePanel title="条件設定">
        <FilterControls />

        <div class="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-2">
          <div>
            <label class="mb-1 block text-xs font-medium text-slate-500">集計単位</label>
            <select
              v-model="dimension"
              class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
            >
              <option v-for="opt in dimensionOptions" :key="opt.value" :value="opt.value">
                {{ opt.label }}
              </option>
            </select>
          </div>
          <MultiSelect
            v-model="selectedMetrics"
            label="表示集計値"
            :options="metricSelectOptions"
          />
        </div>

        <p class="mt-2 text-xs text-slate-400">
          単品を選ぶと、基本項目（商品記号・カラー・サイズ・季節）も表示されます。
          在庫数・消化率は最新取込週スナップショット基準。
        </p>

        <div class="mt-4 flex flex-wrap gap-2">
          <button
            type="button"
            class="flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700"
            @click="applyAndLoad"
          >
            <Search class="h-4 w-4" />
            適用
          </button>
          <button
            type="button"
            class="flex items-center gap-1.5 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
            @click="resetAndLoad"
          >
            <RotateCcw class="h-4 w-4" />
            リセット
          </button>
        </div>
      </CollapsiblePanel>
    </div>

    <div class="flex min-h-0 flex-1 flex-col">
      <StatusBlock
        :loading="loading"
        :error="errorMessage"
        :empty="totalRows === 0"
        empty-message="該当するデータがありません。"
      >
        <div class="flex h-full flex-col gap-2">
          <div class="shrink-0 space-y-1">
            <p v-if="result?.latestWeek" class="text-xs text-slate-400">
              最新取込週: {{ result.latestWeek }}（在庫・消化率のスナップショット基準）
            </p>
            <p v-if="canDrill" class="text-xs text-indigo-600">
              行をクリックすると、その{{ keyLabel }}でさらに絞り込んで「{{ nextDimLabel }}」へドリルダウンします。
            </p>
          </div>

          <div ref="tableContainer" class="flex min-h-0 flex-1 flex-col">
            <DataTable
              :columns="tableColumns"
              :rows="visibleRows"
              :row-key="(row: CrosstabRow) => row.key"
              :clickable="canDrill"
              fill-height
              @row-click="handleRowDrill"
            >
              <template #thumbnail="{ row }">
                <img
                  v-if="(row as CrosstabRow).basicItems?.primaryImageUrl"
                  :src="(row as CrosstabRow).basicItems!.primaryImageUrl!"
                  :alt="(row as CrosstabRow).basicItems?.productName ?? ''"
                  loading="lazy"
                  class="h-10 w-10 rounded object-cover"
                >
                <div
                  v-else
                  class="flex h-10 w-10 items-center justify-center rounded bg-slate-100 text-slate-300"
                  title="マスタ未登録"
                >
                  <Package class="h-4 w-4" />
                </div>
              </template>
              <template #productName="{ row }">
                <div class="flex min-w-0 flex-col">
                  <NuxtLink
                    v-if="(row as CrosstabRow).basicItems?.masterProductId"
                    :to="`/product-analytics/${(row as CrosstabRow).basicItems!.masterProductId}`"
                    class="truncate text-indigo-600 hover:underline"
                    @click.stop
                  >
                    {{ (row as CrosstabRow).basicItems?.productName
                      ?? (row as CrosstabRow).basicItems?.hinmei
                      ?? '-' }}
                  </NuxtLink>
                  <span v-else class="truncate text-slate-800">
                    {{ (row as CrosstabRow).basicItems?.productName
                      ?? (row as CrosstabRow).basicItems?.hinmei
                      ?? '-' }}
                  </span>
                  <span
                    v-if="(row as CrosstabRow).basicItems?.brand"
                    class="truncate text-xs text-slate-400"
                  >{{ (row as CrosstabRow).basicItems!.brand }}</span>
                </div>
              </template>
            </DataTable>
          </div>

          <div class="shrink-0 flex flex-wrap items-center justify-between gap-2 text-sm text-slate-600">
            <span>
              全 {{ formatNumber(totalRows) }} 件中
              {{ formatNumber(rangeStart) }} - {{ formatNumber(rangeEnd) }} 件を表示
              （売上金額の降順、最大1000件）
            </span>
            <div class="flex items-center gap-2">
              <button
                type="button"
                class="flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 disabled:opacity-40"
                :disabled="currentPage <= 1"
                @click="changePage(-1)"
              >
                <ChevronLeft class="h-4 w-4" />
                前へ
              </button>
              <span>{{ currentPage }} / {{ totalPages }}</span>
              <button
                type="button"
                class="flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 disabled:opacity-40"
                :disabled="currentPage >= totalPages"
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
  </div>
</template>
