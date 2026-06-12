<script setup lang="ts">
import { ChevronRight } from 'lucide-vue-next'
import type { InventoryActionItem, InventoryActionTargetTab } from '~/types/api'

/**
 * 「今週のアクション」フィード。取込データからルールベースで自動抽出された
 * 気づき（サーバ生成）を重大度つきで列挙し、該当タブへの導線を付ける。
 * compact は全社サマリーのダイジェスト用（上位 limit 件のみ・余白圧縮）。
 */
const props = withDefaults(
  defineProps<{
    actions: InventoryActionItem[]
    compact?: boolean
    /** compact 時の最大表示件数。 */
    limit?: number
  }>(),
  { compact: false, limit: 3 },
)

const emit = defineEmits<{ navigate: [tab: InventoryActionTargetTab] }>()

const visibleActions = computed(() =>
  props.compact ? props.actions.slice(0, props.limit) : props.actions,
)
</script>

<template>
  <div v-if="visibleActions.length === 0" class="py-6 text-center text-sm text-slate-400">
    表示できるアクションはありません。
  </div>
  <ul v-else class="divide-y divide-slate-100">
    <li
      v-for="action in visibleActions"
      :key="action.code"
      class="flex items-start gap-3"
      :class="props.compact ? 'py-2.5' : 'py-3'"
    >
      <span
        class="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg"
        :class="ACTION_SEVERITIES[action.severity].className"
        aria-hidden="true"
      >
        <component :is="ACTION_SEVERITIES[action.severity].icon" class="h-4.5 w-4.5" />
      </span>
      <p class="min-w-0 flex-1 text-sm leading-relaxed text-slate-700">
        {{ action.message }}
      </p>
      <button
        type="button"
        class="flex shrink-0 items-center gap-0.5 rounded-lg border border-slate-200 px-2.5 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:border-indigo-400 hover:text-indigo-700"
        @click="emit('navigate', action.targetTab)"
      >
        {{ ACTION_TARGET_LABELS[action.targetTab] }}
        <ChevronRight class="h-3.5 w-3.5" />
      </button>
    </li>
  </ul>
</template>
