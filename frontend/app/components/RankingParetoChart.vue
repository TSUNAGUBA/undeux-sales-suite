<script setup lang="ts">
/**
 * ABC / パレート図。
 *
 * - 棒: 構成比の基準指標（売上金額など）の実額。ABC ランクで色分け（A=indigo / B=sky / C=slate）。
 * - 折れ線: 累積構成比（%）。右側の第2軸（0〜100%）に描画する。
 * - 棒は基準指標の降順（パレート順）で渡される前提。表示件数は親が読みやすい件数に制限する。
 *
 * 棒（bar）と折れ線（line）を1チャートに混在させる複合チャート。chart.js + vue-chartjs。
 */
import { Bar } from 'vue-chartjs'
import type { ChartData, ChartDataset, ChartOptions } from 'chart.js'
import type { AbcTier, ParetoBar } from '~/utils/ranking'

const props = defineProps<{
  bars: ParetoBar[]
  /** 基準指標の表示名（例「売上金額」）。 */
  metricLabel: string
  /** 表示件数が母集団より少ない場合の総数（注記用。null は注記なし）。 */
  totalCount?: number | null
}>()

const TIER_COLOR: Record<AbcTier, string> = {
  A: '#4f46e5', // indigo-600
  B: '#0ea5e9', // sky-500
  C: '#94a3b8', // slate-400
}

const CUMULATIVE_COLOR = '#f59e0b' // amber-500

const chartData = computed<ChartData<'bar'>>(() => {
  const barDataset: ChartDataset<'bar'> = {
    label: props.metricLabel,
    data: props.bars.map((b) => b.value),
    backgroundColor: props.bars.map((b) => TIER_COLOR[b.tier]),
    borderRadius: 3,
    maxBarThickness: 42,
    order: 2,
    yAxisID: 'y',
  }

  // 累積構成比は折れ線。chart.js は混在チャートでデータセット単位の type を解釈するが、
  // ChartData<'bar'> の型には line 固有プロパティが含まれないため、既知の制約として
  // line データセットを ChartDataset<'bar'> にキャストする（実行時は type:'line' で描画）。
  const lineDataset = {
    type: 'line',
    label: '累積構成比',
    data: props.bars.map((b) => b.cumulative),
    borderColor: CUMULATIVE_COLOR,
    backgroundColor: CUMULATIVE_COLOR,
    borderWidth: 2,
    tension: 0.25,
    pointRadius: 2,
    pointHoverRadius: 4,
    order: 1,
    yAxisID: 'y1',
  } as unknown as ChartDataset<'bar'>

  return {
    labels: props.bars.map((b) => b.label),
    datasets: [barDataset, lineDataset],
  }
})

const chartOptions = computed<ChartOptions<'bar'>>(() => ({
  responsive: true,
  maintainAspectRatio: false,
  interaction: { intersect: false, mode: 'index' },
  plugins: {
    legend: { display: true, position: 'top', labels: { boxWidth: 12 } },
    tooltip: {
      callbacks: {
        label: (ctx) => {
          const raw = ctx.parsed.y
          if (raw === null || raw === undefined) {
            return ''
          }
          if (ctx.dataset.yAxisID === 'y1') {
            return `累積構成比: ${raw.toFixed(1)}%`
          }
          return `${props.metricLabel}: ${Math.round(raw).toLocaleString('ja-JP')}`
        },
      },
    },
  },
  scales: {
    x: { ticks: { maxRotation: 60, minRotation: 0, autoSkip: false, font: { size: 10 } } },
    y: {
      beginAtZero: true,
      position: 'left',
      ticks: { precision: 0 },
      title: { display: true, text: props.metricLabel },
    },
    y1: {
      beginAtZero: true,
      max: 100,
      position: 'right',
      grid: { drawOnChartArea: false },
      ticks: { callback: (value) => `${value}%` },
      title: { display: true, text: '累積構成比' },
    },
  },
}))
</script>

<template>
  <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
    <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
      <h3 class="text-sm font-semibold text-slate-700">
        ABC / パレート分析（{{ metricLabel }}基準）
      </h3>
      <div class="flex items-center gap-3 text-xs text-slate-500">
        <span class="inline-flex items-center gap-1">
          <span class="inline-block h-2.5 w-2.5 rounded-sm" :style="{ backgroundColor: TIER_COLOR.A }" />A
        </span>
        <span class="inline-flex items-center gap-1">
          <span class="inline-block h-2.5 w-2.5 rounded-sm" :style="{ backgroundColor: TIER_COLOR.B }" />B
        </span>
        <span class="inline-flex items-center gap-1">
          <span class="inline-block h-2.5 w-2.5 rounded-sm" :style="{ backgroundColor: TIER_COLOR.C }" />C
        </span>
      </div>
    </div>
    <div class="h-80">
      <Bar :data="chartData" :options="chartOptions" />
    </div>
    <p
      v-if="totalCount != null && totalCount > bars.length"
      class="mt-2 text-xs text-slate-400"
    >
      上位 {{ bars.length }} 件を表示（全 {{ totalCount.toLocaleString('ja-JP') }} 件）。ABC ランクは全件を母集団に算出しています。
    </p>
  </div>
</template>
