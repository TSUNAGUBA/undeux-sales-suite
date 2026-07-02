<script setup lang="ts">
/**
 * 散布図カード（散布図・回帰分析ページ用）。
 * 全データセットを scatter として描画し、showLine で回帰直線・象限の基準線を引く
 * （混在チャート型を避け ChartData<'scatter'> 単一型で完結させる）。
 */
import { Scatter } from 'vue-chartjs'
import type { ChartData, ChartOptions } from 'chart.js'

export interface ScatterDataset {
  label: string
  color: string
  /** 点（バブルサイズを変えたい場合は pointRadius を併用）。 */
  points: { x: number; y: number }[]
  /** 点の半径（数値 or 点ごとの配列）。既定 4。 */
  pointRadius?: number | number[]
  /** true のとき点を線で結ぶ（回帰直線・基準線用。通常 pointRadius=0 で点は隠す）。 */
  showLine?: boolean
  /** 破線にするか（基準線用）。 */
  dashed?: boolean
}

const props = withDefaults(
  defineProps<{
    title: string
    xLabel: string
    yLabel: string
    datasets: ScatterDataset[]
    /** ツールチップの x 値接尾辞（例 "℃"）。 */
    xSuffix?: string
    /** ツールチップの y 値接尾辞（例 "点"）。 */
    ySuffix?: string
    /** 軸を 0 起点にするか（既定 false=データ範囲に自動フィット）。 */
    beginAtZero?: boolean
    /** 凡例（データセット一覧）を非表示にするか。系列が多い場合に邪魔なので隠す用途。 */
    hideLegend?: boolean
  }>(),
  { xSuffix: '', ySuffix: '', beginAtZero: false, hideLegend: false },
)

const chartData = computed<ChartData<'scatter'>>(() => ({
  datasets: props.datasets.map((ds) => ({
    label: ds.label,
    data: ds.points,
    backgroundColor: ds.showLine ? ds.color : ds.color + 'cc',
    borderColor: ds.color,
    borderWidth: ds.showLine ? 2 : 1,
    pointRadius: ds.pointRadius ?? (ds.showLine ? 0 : 4),
    pointHoverRadius: ds.showLine ? 0 : 6,
    showLine: ds.showLine ?? false,
    borderDash: ds.dashed ? [6, 4] : undefined,
    fill: false,
  })),
}))

const chartOptions = computed<ChartOptions<'scatter'>>(() => ({
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: { display: !props.hideLegend, position: 'top', labels: { boxWidth: 12 } },
    tooltip: {
      callbacks: {
        label: (ctx) => {
          const x = ctx.parsed.x
          const y = ctx.parsed.y
          return `${ctx.dataset.label}: (${x}${props.xSuffix}, ${y}${props.ySuffix})`
        },
      },
    },
  },
  scales: {
    x: {
      type: 'linear',
      title: { display: true, text: props.xLabel },
      beginAtZero: props.beginAtZero,
    },
    y: {
      type: 'linear',
      title: { display: true, text: props.yLabel },
      beginAtZero: props.beginAtZero,
    },
  },
}))
</script>

<template>
  <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
    <h3 class="mb-3 text-sm font-semibold text-slate-700">{{ title }}</h3>
    <div class="h-80">
      <Scatter :data="chartData" :options="chartOptions" />
    </div>
  </div>
</template>
