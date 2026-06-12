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
      <span class="flex min-w-0 flex-1 items-baseline gap-3">
        <span class="shrink-0 text-sm font-medium text-slate-700">{{ title }}</span>
        <!-- 現在の条件サマリー等の補足表示（任意）。クリック領域は行全体のまま変わらない。 -->
        <span v-if="$slots.summary" class="min-w-0 truncate text-xs text-slate-500">
          <slot name="summary" />
        </span>
      </span>
      <component
        :is="isOpen ? ChevronUp : ChevronDown"
        class="h-4 w-4 shrink-0 text-slate-400"
      />
    </button>
    <div v-show="isOpen" class="border-t border-slate-100 p-4">
      <slot />
    </div>
  </div>
</template>
