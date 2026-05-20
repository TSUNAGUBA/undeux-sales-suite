<script setup lang="ts">
import { Search, RotateCcw } from 'lucide-vue-next'

const emit = defineEmits<{ apply: [] }>()

const { reset } = useFilters()

function applyFilter(): void {
  emit('apply')
}

function resetFilter(): void {
  reset()
  emit('apply')
}
</script>

<template>
  <CollapsiblePanel title="フィルター">
    <FilterControls />

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
