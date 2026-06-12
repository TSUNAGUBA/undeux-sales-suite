<script setup lang="ts">
/**
 * 滞留・不動タブの経過バケットカード（3区分）。
 * 区分ラベルは API レスポンスの境界値（thresholds.*AgingBoundaries）から組み立てる。
 * 例: [60,90,120]×日 → 「60〜90日 / 90〜120日 / 120日超」、
 *     [8,12,16]×週 → 「8〜12週 / 12〜16週 / 16週以上」。
 */
const props = defineProps<{
  /** 経過バケットの境界値（昇順3要素）。 */
  boundaries: number[]
  /** 各区分の件数（boundaries に対応する3要素）。 */
  counts: number[]
  unit: '日' | '週'
  /** バーの色（区分順）。 */
  barClasses: string[]
}>()

const buckets = computed(() => {
  const [low, mid, high] = props.boundaries
  if (low === undefined || mid === undefined || high === undefined) {
    return []
  }
  const labels = [
    `${low}〜${mid}${props.unit}`,
    `${mid}〜${high}${props.unit}`,
    `${high}${props.unit}${props.unit === '日' ? '超' : '以上'}`,
  ]
  const total = props.counts.reduce((sum, n) => sum + n, 0)
  return labels.map((label, i) => ({
    label,
    count: props.counts[i] ?? 0,
    widthPercent: total > 0 ? ((props.counts[i] ?? 0) / total) * 100 : 0,
    barClass: props.barClasses[i] ?? 'bg-slate-400',
  }))
})
</script>

<template>
  <div class="grid grid-cols-1 gap-3 sm:grid-cols-3">
    <div
      v-for="bucket in buckets"
      :key="bucket.label"
      class="rounded-xl border border-slate-200 bg-white p-4"
    >
      <p class="text-xs text-slate-500">{{ bucket.label }}</p>
      <p class="mt-1 text-lg font-bold text-slate-800">
        {{ formatNumber(bucket.count) }} <span class="text-xs font-medium text-slate-400">SKU</span>
      </p>
      <div class="mt-2.5 h-1.5 overflow-hidden rounded-full bg-slate-100" aria-hidden="true">
        <div
          class="h-full rounded-full"
          :class="bucket.barClass"
          :style="{ width: `${bucket.widthPercent}%` }"
        />
      </div>
    </div>
  </div>
</template>
