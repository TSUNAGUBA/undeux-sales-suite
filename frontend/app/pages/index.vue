<script setup lang="ts">
import {
  CircleDollarSign,
  ShoppingCart,
  TrendingUp,
  Percent,
  Package,
  Boxes,
  Gauge,
} from 'lucide-vue-next'
import type { KpiCardItem, SummaryResponse } from '~/types/api'

useHead({ title: '全社サマリー | UndeuxSales' })

const { toQuery, loadOptions } = useFilters()
const { get } = useApi()

const summary = ref<SummaryResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const kpiItems = computed<KpiCardItem[]>(() => {
  const kpi = summary.value?.kpi
  if (!kpi) {
    return []
  }
  return [
    {
      label: '売上金額',
      value: formatCurrency(kpi.amount),
      icon: CircleDollarSign,
      accentClass: 'bg-indigo-50 text-indigo-600',
    },
    {
      label: '売上数量',
      value: `${formatNumber(kpi.quantity)} 点`,
      icon: ShoppingCart,
      accentClass: 'bg-sky-50 text-sky-600',
    },
    {
      label: '粗利',
      value: formatCurrency(kpi.grossProfit),
      icon: TrendingUp,
      accentClass: 'bg-emerald-50 text-emerald-600',
    },
    {
      label: '粗利率',
      value: formatRatioAsPercent(kpi.grossProfitRate),
      icon: Percent,
      accentClass: 'bg-amber-50 text-amber-600',
    },
    {
      label: '商品数',
      value: `${formatNumber(kpi.productCount)} 品`,
      icon: Package,
      accentClass: 'bg-violet-50 text-violet-600',
    },
    {
      label: '在庫数（最新週）',
      value: `${formatNumber(kpi.currentStock)} 点`,
      icon: Boxes,
      accentClass: 'bg-rose-50 text-rose-600',
    },
    {
      label: '消化率（最新週）',
      value: formatRatioAsPercent(kpi.sellThroughRate),
      icon: Gauge,
      accentClass: 'bg-teal-50 text-teal-600',
    },
  ]
})

const trendLabels = computed(() =>
  (summary.value?.weeklyTrend ?? []).map((point) => point.date),
)

const trendSeries = computed(() => {
  const trend = summary.value?.weeklyTrend ?? []
  return [
    { label: '売上金額', data: trend.map((p) => p.amount), color: '#4f46e5' },
    { label: '粗利', data: trend.map((p) => p.grossProfit), color: '#059669' },
  ]
})

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    summary.value = await get<SummaryResponse>('/api/summary', toQuery())
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

function handleKpiDrill(): void {
  // 新クロス集計仕様: 行=部門、列=年（カテゴリ × 時間軸の代表組合せ）。
  navigateTo({
    path: '/crosstab',
    query: { rowDimension: 'category:department', columnDimension: 'time:year' },
  })
}

onMounted(async () => {
  await loadOptions()
  await load()
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">全社サマリー</h1>
      <p class="text-sm text-slate-500">主要KPIと週次売上トレンド</p>
    </div>

    <FilterBar @apply="load" />

    <StatusBlock :loading="loading" :error="errorMessage">
      <div class="space-y-4">
        <div class="grid grid-cols-2 gap-3 md:grid-cols-3 lg:grid-cols-4">
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

        <p v-if="summary?.kpi.latestWeek" class="text-xs text-slate-400">
          最新取込週: {{ summary.kpi.latestWeek }}
        </p>

        <LineChartCard
          v-if="trendLabels.length > 0"
          title="週次売上推移"
          :labels="trendLabels"
          :series="trendSeries"
        />
        <div
          v-else
          class="rounded-xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-400"
        >
          選択した条件に該当する売上データがありません。
        </div>
      </div>
    </StatusBlock>
  </div>
</template>
