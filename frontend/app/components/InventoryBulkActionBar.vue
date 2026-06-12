<script setup lang="ts">
import { Ban, ListChecks, Tag, X } from 'lucide-vue-next'
import type { InventoryFlagType } from '~/types/api'

/**
 * 在庫明細の一括アクションバー。行選択が1件以上あるときに表示し、
 * 選択 SKU を発注停止候補／値下げ候補として一括登録する。
 * 登録 API（POST /api/inventory-flags/bulk）は冪等で、既存フラグの対応状況を巻き戻さない。
 */
const props = defineProps<{
  count: number
  submitting: boolean
  error: string | null
}>()

const emit = defineEmits<{
  register: [flagType: InventoryFlagType]
  clear: []
  selectPage: []
}>()
</script>

<template>
  <div
    class="sticky bottom-4 z-10 flex flex-wrap items-center gap-2 rounded-xl bg-slate-900 px-4 py-3 text-white shadow-lg shadow-slate-900/20"
    role="region"
    aria-label="一括アクション"
  >
    <span class="text-sm">
      <b class="tabular-nums">{{ props.count }}</b> 件 選択中
    </span>
    <button
      type="button"
      class="flex h-9 items-center gap-1.5 rounded-lg bg-white/10 px-2.5 text-xs font-medium transition-colors hover:bg-white/20"
      @click="emit('selectPage')"
    >
      <ListChecks class="h-3.5 w-3.5" />
      表示中ページを全選択
    </button>
    <button
      type="button"
      class="flex h-9 items-center gap-1.5 rounded-lg bg-white/10 px-2.5 text-xs font-medium transition-colors hover:bg-white/20"
      @click="emit('clear')"
    >
      <X class="h-3.5 w-3.5" />
      選択解除
    </button>

    <span v-if="props.error" class="min-w-0 truncate text-xs text-rose-300" role="alert" :title="props.error">
      {{ props.error }}
    </span>

    <div class="ml-auto flex flex-wrap items-center gap-2">
      <button
        type="button"
        class="flex h-9 items-center gap-1.5 rounded-lg bg-white/10 px-3 text-xs font-bold transition-colors hover:bg-white/20 disabled:cursor-not-allowed disabled:opacity-50"
        :disabled="props.submitting"
        @click="emit('register', 'stop-order')"
      >
        <Ban class="h-3.5 w-3.5" />
        発注停止候補に登録
      </button>
      <button
        type="button"
        class="flex h-9 items-center gap-1.5 rounded-lg bg-indigo-500 px-3 text-xs font-bold transition-colors hover:bg-indigo-400 disabled:cursor-not-allowed disabled:opacity-50"
        :disabled="props.submitting"
        @click="emit('register', 'markdown')"
      >
        <Tag class="h-3.5 w-3.5" />
        {{ props.submitting ? '登録中…' : '値下げ候補に登録' }}
      </button>
    </div>
  </div>
</template>
