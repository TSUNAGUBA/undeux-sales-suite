<script setup lang="ts">
import { Line } from 'vue-chartjs'
import type { ChartData, ChartOptions } from 'chart.js'

interface ChartSeries {
  label: string
  data: number[]
  color: string
}

const props = defineProps<{
  title: string
  labels: string[]
  series: ChartSeries[]
}>()

const chartData = computed<ChartData<'line'>>(() => ({
  labels: props.labels,
  datasets: props.series.map((series) => ({
    label: series.label,
    data: series.data,
    borderColor: series.color,
    backgroundColor: series.color + '22',
    tension: 0.3,
    fill: true,
    pointRadius: 2,
    borderWidth: 2,
  })),
}))

const chartOptions: ChartOptions<'line'> = {
  responsive: true,
  maintainAspectRatio: false,
  interaction: { intersect: false, mode: 'index' },
  plugins: {
    legend: { display: true, position: 'top', labels: { boxWidth: 12 } },
  },
  scales: {
    y: { beginAtZero: true, ticks: { precision: 0 } },
    x: { ticks: { maxRotation: 0, autoSkip: true } },
  },
}
</script>

<template>
  <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
    <h3 class="mb-3 text-sm font-semibold text-slate-700">{{ title }}</h3>
    <div class="h-72">
      <Line :data="chartData" :options="chartOptions" />
    </div>
  </div>
</template>
