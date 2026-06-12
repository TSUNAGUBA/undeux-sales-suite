<script setup lang="ts">
import type { InventoryStatus } from '~/types/api'

/**
 * 在庫健全性の状態バッジ（健全/注意/滞留/不動）。
 * 表示定義は utils/skuStatus.ts の INVENTORY_STATUSES（表示カタログの SoT）。
 */
const props = defineProps<{ status: InventoryStatus }>()

const presentation = computed(() => INVENTORY_STATUSES[props.status])
</script>

<template>
  <span
    class="inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-medium"
    :class="presentation.className"
    :title="presentation.description"
  >
    <component :is="presentation.icon" class="h-3.5 w-3.5 shrink-0" />
    {{ presentation.label }}
  </span>
</template>
