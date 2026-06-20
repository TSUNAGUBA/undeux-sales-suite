<script setup lang="ts">
import type { ScatterDataset } from '~/components/ScatterChartCard.vue'
import type { InventoryDepartmentHealthRow, InventoryThresholds } from '~/types/api'

/**
 * ポジショニング4象限（横軸: 消化率% / 縦軸: 平均在庫日数 / 円の大きさ: 在庫数）。
 * 基準線は閾値（消化率の滞留上限・滞留在庫日数）を API レスポンスの thresholds から引く。
 * 散布図・回帰分析ページ（消化率×値引き率）の4象限ガイドと同じ表現パターン。
 * 1行＝1データセットにすることで、ツールチップに区分名（部門名／品番3桁）が表示される。
 * `rows` は部門別／品番3桁別いずれの健全性行も受け取れる（同一形状）。`title` で軸名を切り替える。
 */
const props = withDefaults(
  defineProps<{
    rows: InventoryDepartmentHealthRow[]
    thresholds: InventoryThresholds
    title?: string
  }>(),
  { title: 'ポジショニング' },
)

/** 象限の色（基準線に対する位置で決定）。scatter ページの判定色と同系。 */
const QUADRANT_COLORS = {
  attention: '#e11d48', // 左上: 売れず・積み上がり（要対応）
  overstock: '#d97706', // 右上: 売れているが在庫過多（仕込み過多）
  healthy: '#059669', // 右下: 健全
  shrink: '#64748b', // 左下: 動きが小さい（縮小・終売候補）
} as const

function quadrantColor(sellThroughPercent: number, stockDays: number): string {
  const highDays = stockDays > props.thresholds.stagnantStockDays
  const lowSellThrough = sellThroughPercent < props.thresholds.stagnantSellThroughCeiling * 100
  if (highDays && lowSellThrough) return QUADRANT_COLORS.attention
  if (highDays) return QUADRANT_COLORS.overstock
  if (lowSellThrough) return QUADRANT_COLORS.shrink
  return QUADRANT_COLORS.healthy
}

/** 在庫数 → 円の半径（面積比例＝平方根スケール。視認可能な下限/上限でクランプ）。 */
function bubbleRadius(stock: number, maxStock: number): number {
  if (maxStock <= 0 || stock <= 0) return 4
  return Math.min(18, Math.max(4, Math.sqrt(stock / maxStock) * 18))
}

const datasets = computed<ScatterDataset[]>(() => {
  const departments = props.rows.filter((d) => d.stock > 0)
  if (departments.length === 0) return []

  const maxStock = Math.max(...departments.map((d) => d.stock))
  const maxDays = Math.max(
    props.thresholds.stagnantStockDays * 1.5,
    ...departments.map((d) => d.averageStockDays),
  )

  const bubbles: ScatterDataset[] = departments.map((dept) => {
    const x = dept.sellThroughRate * 100
    return {
      label: dept.label,
      color: quadrantColor(x, dept.averageStockDays),
      points: [{ x, y: dept.averageStockDays }],
      pointRadius: bubbleRadius(dept.stock, maxStock),
    }
  })

  // 基準線（縦: 消化率の滞留上限 / 横: 滞留在庫日数）。
  const xLine = props.thresholds.stagnantSellThroughCeiling * 100
  const yLine = props.thresholds.stagnantStockDays
  const guides: ScatterDataset[] = [
    {
      label: `消化率 ${Math.round(xLine)}% 基準`,
      color: '#cbd5e1',
      points: [
        { x: xLine, y: 0 },
        { x: xLine, y: maxDays * 1.05 },
      ],
      pointRadius: 0,
      showLine: true,
      dashed: true,
    },
    {
      label: `在庫日数 ${yLine} 日基準`,
      color: '#cbd5e1',
      points: [
        { x: 0, y: yLine },
        { x: 105, y: yLine },
      ],
      pointRadius: 0,
      showLine: true,
      dashed: true,
    },
  ]

  return [...bubbles, ...guides]
})

/** 4象限の読み方ガイド（基準線に対する位置 → 意味と打ち手）。 */
const quadrantGuide = [
  { label: '左上: 要対応', className: 'bg-rose-50 text-rose-700', text: '売れず積み上がり。発注抑制・値下げ候補' },
  { label: '右上: 仕込み過多', className: 'bg-amber-50 text-amber-700', text: '売れているが在庫日数が長い。発注ペース確認' },
  { label: '右下: 健全', className: 'bg-emerald-50 text-emerald-700', text: '消化良好・在庫適正' },
  { label: '左下: 縮小・終売候補', className: 'bg-slate-100 text-slate-600', text: '動きが小さい。品揃えの見直し対象' },
]
</script>

<template>
  <div>
    <ScatterChartCard
      :title="title"
      x-label="消化率（%）"
      y-label="平均在庫日数（日）"
      :datasets="datasets"
      x-suffix="%"
      y-suffix="日"
      begin-at-zero
    />
    <div class="mt-3 grid grid-cols-1 gap-1.5 sm:grid-cols-2">
      <p
        v-for="guide in quadrantGuide"
        :key="guide.label"
        class="rounded-lg px-2.5 py-1.5 text-xs"
        :class="guide.className"
      >
        <span class="font-bold">{{ guide.label }}</span> — {{ guide.text }}
      </p>
    </div>
  </div>
</template>
