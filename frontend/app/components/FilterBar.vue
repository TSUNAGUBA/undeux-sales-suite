<script setup lang="ts">
import { Search, RotateCcw } from 'lucide-vue-next'

const props = withDefaults(
  defineProps<{
    /** useFilters のスコープキー。利用側（/mart 配下）は 'mart-filter' を渡す。 */
    scopeKey?: string
    /** 年度フィルタを隠す（年月 from-to など独自の期間 UI を持つページ用）。 */
    hideYear?: boolean
  }>(),
  { scopeKey: 'sales-filter', hideYear: false },
)

const emit = defineEmits<{ apply: [] }>()

const { reset } = useFilters(props.scopeKey)

function applyFilter(): void {
  emit('apply')
}

function resetFilter(): void {
  reset()
  emit('apply')
}
</script>

<template>
  <CollapsiblePanel title="フィルター" :default-open="false">
    <FilterControls :scope-key="scopeKey" :hide-year="hideYear" />

    <div class="mt-4 flex flex-wrap gap-2">
      <button
        type="button"
        class="flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700"
        @click="applyFilter"
      >
        <Search class="h-4 w-4" />
        適用
      </button>
      <button
        type="button"
        class="flex items-center gap-1.5 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
        @click="resetFilter"
      >
        <RotateCcw class="h-4 w-4" />
        リセット
      </button>
    </div>
  </CollapsiblePanel>
</template>
