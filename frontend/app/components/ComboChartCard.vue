<script setup lang="ts">
/**
 * 折れ線と棒グラフを混在させる複合チャートカード（複数Y軸対応）。
 *
 * 週次売上推移グラフ（売上数量/売上金額=折れ線、店頭在庫=棒、気温=折れ線）のように
 * 単位の異なる系列を1つのチャートへ重ねる用途で使う。LineChartCard / BarChartCard では
 * 系列ごとの種類・軸を指定できないため独立コンポーネントとして提供する。
 * BarController / LineController は chart.client.ts プラグインで登録済み。
 */
import { Chart } from 'vue-chartjs'
import type { ChartData, ChartOptions } from 'chart.js'

export interface ComboChartSeries {
  label: string
  data: number[]
  color: string
  /** 系列の描画種類。 */
  type: 'line' | 'bar'
  /** 紐づける Y 軸の ID（axes の id と一致させる）。省略時は 'y'。 */
  yAxisId?: string
}

export interface ComboChartAxis {
  /** Chart.js の軸 ID（'y' / 'y1' / 'y2' ...）。 */
  id: string
  position: 'left' | 'right'
  /** 軸タイトル（単位の明示に使う）。 */
  label?: string
  /** 0 起点にするか。気温など負値があり得る軸では false にする。 */
  beginAtZero?: boolean
  /** グリッド線を消すか（複数軸の重なりを避ける）。 */
  gridOff?: boolean
}

const props = defineProps<{
  title: string
  labels: string[]
  series: ComboChartSeries[]
  axes: ComboChartAxis[]
}>()

// 複合チャートでは dataset 単位の type 指定が必要なため、ルートの type は 'bar' を渡しつつ
// 各 dataset で上書きする（Chart.js の mixed chart の標準的な構成）。
const chartData = computed<ChartData>(() => ({
  labels: props.labels,
  datasets: props.series.map((series) =>
    series.type === 'bar'
      ? {
          type: 'bar' as const,
          label: series.label,
          data: series.data,
          backgroundColor: series.color + '66',
          borderColor: series.color,
          borderWidth: 1,
          yAxisID: series.yAxisId ?? 'y',
        }
      : {
          type: 'line' as const,
          label: series.label,
          data: series.data,
          borderColor: series.color,
          backgroundColor: series.color + '22',
          tension: 0.3,
          fill: false,
          pointRadius: 2,
          borderWidth: 2,
          yAxisID: series.yAxisId ?? 'y',
        },
  ),
}))

const chartOptions = computed<ChartOptions>(() => {
  const scales: NonNullable<ChartOptions['scales']> = {
    x: { ticks: { maxRotation: 0, autoSkip: true } },
  }
  for (const axis of props.axes) {
    scales[axis.id] = {
      type: 'linear',
      position: axis.position,
      beginAtZero: axis.beginAtZero ?? true,
      title: axis.label ? { display: true, text: axis.label } : undefined,
      grid: axis.gridOff ? { drawOnChartArea: false } : undefined,
    }
  }
  return {
    responsive: true,
    maintainAspectRatio: false,
    interaction: { intersect: false, mode: 'index' },
    plugins: {
      legend: { display: true, position: 'top', labels: { boxWidth: 12 } },
    },
    scales,
  }
})
</script>

<template>
  <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
    <h3 class="mb-3 text-sm font-semibold text-slate-700">{{ title }}</h3>
    <div class="h-80">
      <Chart type="bar" :data="chartData" :options="chartOptions" />
    </div>
  </div>
</template>
