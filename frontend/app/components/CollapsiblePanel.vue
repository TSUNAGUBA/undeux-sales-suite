<script setup lang="ts">
import { ChevronDown, ChevronUp } from 'lucide-vue-next'

const props = withDefaults(
  defineProps<{
    title: string
    /** 初期状態の展開有無（既定: 展開）。 */
    defaultOpen?: boolean
  }>(),
  { defaultOpen: true },
)

const isOpen = ref(props.defaultOpen)
</script>

<template>
  <div class="rounded-xl border border-slate-200 bg-white shadow-sm">
    <button
      type="button"
      class="flex w-full items-center justify-between gap-2 px-4 py-3 text-left hover:bg-slate-50"
      :aria-expanded="isOpen"
      @click="isOpen = !isOpen"
    >
      <span class="text-sm font-medium text-slate-700">{{ title }}</span>
      <component
        :is="isOpen ? ChevronUp : ChevronDown"
        class="h-4 w-4 text-slate-400"
      />
    </button>
    <div v-show="isOpen" class="border-t border-slate-100 p-4">
      <slot />
    </div>
  </div>
</template>
