<script setup lang="ts">
/**
 * 順位変動スロープチャート（SVG）。
 *
 * 比較期間（前期）と主期間（今期）の順位を2列に並べ、各アイテムを線で結ぶ。
 * - 緑: 順位上昇 / 赤: 下降 / 灰: 横ばい / 青: 新規（比較期間に存在せず）
 * - 前期順位が表示範囲（今期 Top-N）より下位 or 新規のアイテムは下部「圏外」帯から立ち上がる。
 *
 * chart.js では順位反転軸＋複数ラベル線の表現が煩雑なため、表現の自由度を優先して SVG で実装する。
 */
import type { MoverItem } from '~/utils/ranking'

const props = withDefaults(
  defineProps<{
    items: MoverItem[]
    /** 表示する最大件数（多すぎると重なるため制限）。 */
    maxItems?: number
  }>(),
  { maxItems: 15 },
)

// レイアウト定数（viewBox 座標系）。
const WIDTH = 640
const TOP = 30
const ROW_GAP = 26
const LEFT_X = 64
const RIGHT_X = 320
const LABEL_X = 336
const OUTSIDE_GAP = 34

const COLOR_UP = '#059669' // emerald-600
const COLOR_DOWN = '#dc2626' // red-600
const COLOR_FLAT = '#94a3b8' // slate-400
const COLOR_NEW = '#2563eb' // blue-600

const shown = computed(() =>
  props.items.slice().sort((a, b) => a.rank - b.rank).slice(0, props.maxItems),
)

// 今期順位の最大（表示範囲）。前期がこれを超える／新規は「圏外」帯に置く。
const maxRank = computed(() =>
  shown.value.reduce((max, item) => Math.max(max, item.rank), 1),
)

const outsideY = computed(() => TOP + (maxRank.value - 1) * ROW_GAP + OUTSIDE_GAP)
const height = computed(() => outsideY.value + 24)

function yForRank(rank: number): number {
  return TOP + (rank - 1) * ROW_GAP
}

/** 前期側の Y 座標。表示範囲内ならその順位、圏外/新規なら下部帯。 */
function prevY(item: MoverItem): number {
  if (item.prevRank !== null && item.prevRank <= maxRank.value) {
    return yForRank(item.prevRank)
  }
  return outsideY.value
}

function colorOf(item: MoverItem): string {
  if (item.isNew) return COLOR_NEW
  if (item.delta === null) return COLOR_FLAT
  if (item.delta > 0) return COLOR_UP
  if (item.delta < 0) return COLOR_DOWN
  return COLOR_FLAT
}

/** 変動チップの表記（▲n / ▼n / NEW / →）。 */
function deltaText(item: MoverItem): string {
  if (item.isNew) return 'NEW'
  if (item.delta === null) return '—'
  if (item.delta > 0) return `▲${item.delta}`
  if (item.delta < 0) return `▼${Math.abs(item.delta)}`
  return '→'
}

const hasOutsideRow = computed(() =>
  shown.value.some((item) => item.isNew || (item.prevRank !== null && item.prevRank > maxRank.value)),
)
</script>

<template>
  <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
    <div class="mb-1 flex flex-wrap items-center justify-between gap-2">
      <h3 class="text-sm font-semibold text-slate-700">順位変動（前期 → 今期）</h3>
      <div class="flex items-center gap-3 text-xs text-slate-500">
        <span class="inline-flex items-center gap-1"><span class="inline-block h-2 w-3 rounded-sm" :style="{ backgroundColor: COLOR_UP }" />上昇</span>
        <span class="inline-flex items-center gap-1"><span class="inline-block h-2 w-3 rounded-sm" :style="{ backgroundColor: COLOR_DOWN }" />下降</span>
        <span class="inline-flex items-center gap-1"><span class="inline-block h-2 w-3 rounded-sm" :style="{ backgroundColor: COLOR_NEW }" />新規</span>
      </div>
    </div>

    <div v-if="shown.length === 0" class="py-10 text-center text-sm text-slate-400">
      表示できる順位変動がありません。
    </div>

    <svg
      v-else
      :viewBox="`0 0 ${WIDTH} ${height}`"
      class="w-full"
      :style="{ maxHeight: `${height}px` }"
      role="img"
      aria-label="前期から今期への順位変動スロープチャート"
    >
      <!-- 列見出し -->
      <text :x="LEFT_X" :y="16" text-anchor="middle" class="fill-slate-400" style="font-size: 11px">前期</text>
      <text :x="RIGHT_X" :y="16" text-anchor="middle" class="fill-slate-400" style="font-size: 11px">今期</text>

      <!-- 圏外帯のラベル -->
      <text
        v-if="hasOutsideRow"
        :x="LEFT_X"
        :y="outsideY + 4"
        text-anchor="middle"
        class="fill-slate-300"
        style="font-size: 9px"
      >圏外</text>

      <g v-for="item in shown" :key="item.key">
        <!-- 接続線 -->
        <line
          :x1="LEFT_X"
          :y1="prevY(item)"
          :x2="RIGHT_X"
          :y2="yForRank(item.rank)"
          :stroke="colorOf(item)"
          :stroke-width="2"
          :stroke-dasharray="item.isNew || (item.prevRank !== null && item.prevRank > maxRank) ? '3 3' : undefined"
          stroke-linecap="round"
          opacity="0.85"
        />
        <!-- 前期ドット -->
        <circle :cx="LEFT_X" :cy="prevY(item)" r="3.5" :fill="colorOf(item)" />
        <!-- 今期ドット -->
        <circle :cx="RIGHT_X" :cy="yForRank(item.rank)" r="3.5" :fill="colorOf(item)" />
        <!-- 今期側ラベル: 順位・名称・変動 -->
        <text
          :x="LABEL_X"
          :y="yForRank(item.rank) + 4"
          class="fill-slate-700"
          style="font-size: 11px"
        >
          <tspan class="fill-slate-400">{{ item.rank }}.</tspan>
          <tspan dx="4" style="font-weight: 600">{{ item.label }}</tspan>
          <tspan dx="6" :fill="colorOf(item)" style="font-size: 10px">{{ deltaText(item) }}</tspan>
        </text>
      </g>
    </svg>
  </div>
</template>
