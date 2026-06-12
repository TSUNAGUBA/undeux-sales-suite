<script setup lang="ts">
import type { InventoryDepartmentHealthRow, InventoryStatus } from '~/types/api'

/**
 * 部門別「在庫の鮮度」積上げ帯。部門ごとの在庫を健全/注意/滞留/不動の構成比で帯表示する。
 * チャートライブラリ不要の純CSS（div 幅%）。在庫数上位の部門から表示する。
 */
const props = withDefaults(
  defineProps<{
    departments: InventoryDepartmentHealthRow[]
    /** 表示する部門数の上限（在庫数降順）。 */
    limit?: number
  }>(),
  { limit: 10 },
)

interface StripSegment {
  status: InventoryStatus
  widthPercent: number
}

const rows = computed(() =>
  props.departments.slice(0, props.limit).map((dept) => {
    const stockByStatus: Record<InventoryStatus, number> = {
      healthy: dept.healthyStock,
      caution: dept.cautionStock,
      stagnant: dept.stagnantStock,
      dormant: dept.dormantStock,
    }
    const segments: StripSegment[] = INVENTORY_STATUS_ORDER
      .map((status) => ({
        status,
        widthPercent: dept.stock > 0 ? (stockByStatus[status] / dept.stock) * 100 : 0,
      }))
      .filter((segment) => segment.widthPercent > 0)
    return { key: dept.key, label: dept.label, stock: dept.stock, segments }
  }),
)
</script>

<template>
  <div>
    <div
      v-for="row in rows"
      :key="row.key"
      class="grid grid-cols-[3.5rem_1fr_7rem] items-center gap-3 py-1.5"
    >
      <span class="truncate text-xs font-bold text-slate-500" :title="row.label">
        {{ row.label }}
      </span>
      <div class="flex h-4 overflow-hidden rounded-md bg-slate-100" aria-hidden="true">
        <div
          v-for="segment in row.segments"
          :key="segment.status"
          class="h-full"
          :class="INVENTORY_STATUS_BAR_CLASSES[segment.status]"
          :style="{ width: `${segment.widthPercent}%` }"
          :title="`${INVENTORY_STATUSES[segment.status].label} ${segment.widthPercent.toFixed(1)}%`"
        />
      </div>
      <span class="text-right text-xs text-slate-500">
        <span class="font-bold text-slate-700">{{ formatNumber(row.stock) }}</span> 点
      </span>
    </div>

    <div class="mt-3 flex flex-wrap gap-x-4 gap-y-1.5 text-xs text-slate-500">
      <span
        v-for="status in INVENTORY_STATUS_ORDER"
        :key="status"
        class="inline-flex items-center gap-1.5"
      >
        <i
          class="inline-block h-2.5 w-2.5 rounded-sm"
          :class="INVENTORY_STATUS_BAR_CLASSES[status]"
          aria-hidden="true"
        />
        {{ INVENTORY_STATUSES[status].label }}（{{ INVENTORY_STATUSES[status].description }}）
      </span>
    </div>
  </div>
</template>
