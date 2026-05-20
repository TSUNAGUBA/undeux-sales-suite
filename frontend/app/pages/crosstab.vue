<script setup lang="ts">
import { Search, RotateCcw } from 'lucide-vue-next'
import type { CrosstabResponse, CrosstabRow } from '~/types/api'

useHead({ title: 'クロス集計 | UndeuxSales' })

const route = useRoute()
const router = useRouter()
const { filter, toQuery, reset, addToFilter } = useFilters()
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

onMounted(load)
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">クロス集計</h1>
      <p class="text-sm text-slate-500">
        集計単位ごとに複数のメトリクスを横並び表示。行クリックでドリルダウン可能。
      </p>
    </div>

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

    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="(result?.rows.length ?? 0) === 0"
      empty-message="該当するデータがありません。"
    >
      <div class="space-y-3">
        <p v-if="result?.latestWeek" class="text-xs text-slate-400">
          最新取込週: {{ result.latestWeek }}（在庫・消化率のスナップショット基準）
        </p>
        <p v-if="canDrill" class="text-xs text-indigo-600">
          行をクリックすると、その{{ keyLabel }}でさらに絞り込んで「{{ nextDimLabel }}」へドリルダウンします。
        </p>
        <DataTable
          :columns="tableColumns"
          :rows="result?.rows ?? []"
          :row-key="(row: CrosstabRow) => row.key"
          :clickable="canDrill"
          @row-click="handleRowDrill"
        />
        <p class="text-xs text-slate-400">
          上位 {{ formatNumber(result?.rows.length ?? 0) }} 件（売上金額の降順、最大1000件）
        </p>
      </div>
    </StatusBlock>
  </div>
</template>
