<script setup lang="ts">
import { Boxes, PackagePlus, Hourglass, Truck, Gauge, CalendarClock } from 'lucide-vue-next'
import type { InventoryBreakdownRow, InventoryResponse, KpiCardItem } from '~/types/api'

useHead({ title: '在庫・発注分析 | UndeuxSales' })

const { toQuery, addToFilter } = useFilters()
const { get } = useApi()

const inventory = ref<InventoryResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const kpiItems = computed<KpiCardItem[]>(() => {
  const kpi = inventory.value?.kpi
  if (!kpi) {
    return []
  }
  return [
    {
      label: '在庫数',
      value: `${formatNumber(kpi.totalStock)} 点`,
      icon: Boxes,
      accentClass: 'bg-rose-50 text-rose-600',
    },
    {
      label: '発注数',
      value: formatDecimal(kpi.totalOrderQuantity, 1),
      icon: PackagePlus,
      accentClass: 'bg-indigo-50 text-indigo-600',
    },
    {
      label: '先付数',
      value: `${formatNumber(kpi.totalAdvanceQuantity)} 点`,
      icon: Hourglass,
      accentClass: 'bg-amber-50 text-amber-600',
    },
    {
      label: '累計納品数',
      value: `${formatNumber(kpi.cumulativeDelivery)} 点`,
      icon: Truck,
      accentClass: 'bg-sky-50 text-sky-600',
    },
    {
      label: '消化率',
      value: formatRatioAsPercent(kpi.sellThroughRate),
      icon: Gauge,
      accentClass: 'bg-teal-50 text-teal-600',
    },
    {
      label: '平均在庫日数',
      value: `${formatDecimal(kpi.averageStockDays, 1)} 日`,
      icon: CalendarClock,
      accentClass: 'bg-violet-50 text-violet-600',
    },
  ]
})

const departmentLabels = computed(() =>
  (inventory.value?.byDepartment ?? []).map((row) => row.label),
)
const departmentStock = computed(() =>
  (inventory.value?.byDepartment ?? []).map((row) => row.stock),
)

const columns = [
  { key: 'label', label: '部門' },
  {
    key: 'stock',
    label: '在庫数',
    align: 'right' as const,
    format: (row: InventoryBreakdownRow) => formatNumber(row.stock),
  },
  {
    key: 'orderQuantity',
    label: '発注数',
    align: 'right' as const,
    format: (row: InventoryBreakdownRow) => formatDecimal(row.orderQuantity, 1),
  },
  {
    key: 'advanceQuantity',
    label: '先付数',
    align: 'right' as const,
    format: (row: InventoryBreakdownRow) => formatNumber(row.advanceQuantity),
  },
  {
    key: 'sellThroughRate',
    label: '消化率',
    align: 'right' as const,
    format: (row: InventoryBreakdownRow) => formatRatioAsPercent(row.sellThroughRate),
  },
]

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    inventory.value = await get<InventoryResponse>('/api/inventory', toQuery())
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

function handleKpiDrill(): void {
  navigateTo({ path: '/crosstab', query: { dimension: 'department' } })
}

function handleDepartmentDrill(row: InventoryBreakdownRow): void {
  addToFilter('departments', row.key)
  navigateTo({ path: '/crosstab', query: { dimension: 'hinban' } })
}

onMounted(load)
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">在庫・発注分析</h1>
      <p class="text-sm text-slate-500">最新取込週スナップショット基準</p>
    </div>

    <FilterBar @apply="load" />

    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="!inventory?.kpi.latestWeek"
      empty-message="該当する在庫データがありません。"
    >
      <div class="space-y-4">
        <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
          <KpiCard
            v-for="item in kpiItems"
            :key="item.label"
            :label="item.label"
            :value="item.value"
            :icon="item.icon"
            :accent-class="item.accentClass"
            clickable
            @click="handleKpiDrill"
          />
        </div>

        <p v-if="inventory?.kpi.latestWeek" class="text-xs text-slate-400">
          最新取込週: {{ inventory.kpi.latestWeek }}
        </p>

        <BarChartCard
          v-if="departmentLabels.length > 0"
          title="部門別 在庫数"
          :labels="departmentLabels"
          :data="departmentStock"
          series-label="在庫数"
          color="#e11d48"
        />

        <DataTable
          :columns="columns"
          :rows="inventory?.byDepartment ?? []"
          :row-key="(row: InventoryBreakdownRow) => row.key"
          clickable
          @row-click="handleDepartmentDrill"
        />
      </div>
    </StatusBlock>
  </div>
</template>
