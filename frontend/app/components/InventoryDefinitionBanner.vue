<script setup lang="ts">
import { AlertOctagon, Snowflake } from 'lucide-vue-next'
import type { InventoryThresholds } from '~/types/api'

/**
 * 滞留・不動タブの定義バナー。「何を滞留/不動と呼んでいるか」を閾値つきで明示する。
 * 閾値は必ず API レスポンスの thresholds から描画する（SoT はバックエンド
 * InventoryHealthRules。フロントに閾値リテラルを置かない）。
 */
const props = defineProps<{
  variant: 'stagnant' | 'dormant'
  thresholds: InventoryThresholds
}>()

const presentation = computed(() =>
  props.variant === 'stagnant'
    ? {
        icon: AlertOctagon,
        className: 'border-rose-200 bg-rose-50 text-rose-800',
        title: '滞留在庫の定義',
        text:
          `在庫日数 ${props.thresholds.stagnantStockDays} 日超 かつ `
          + `消化率 ${Math.round(props.thresholds.stagnantSellThroughCeiling * 100)}% 未満`
          + '（最新取込週スナップショット基準）',
      }
    : {
        icon: Snowflake,
        className: 'border-slate-300 bg-slate-100 text-slate-700',
        title: '不動在庫の定義',
        text:
          `直近 ${props.thresholds.dormantLookbackWeeks} 週 連続で出荷ゼロの在庫あり SKU`
          + `（経過週数は直近 ${props.thresholds.dormantScanWeeks} 週まで計測）`,
      },
)
</script>

<template>
  <div
    class="flex items-start gap-2.5 rounded-xl border p-3 text-sm sm:items-center"
    :class="presentation.className"
  >
    <component :is="presentation.icon" class="mt-0.5 h-4 w-4 shrink-0 sm:mt-0" />
    <p>
      <span class="font-bold">{{ presentation.title }}:</span>
      {{ presentation.text }}
    </p>
  </div>
</template>
