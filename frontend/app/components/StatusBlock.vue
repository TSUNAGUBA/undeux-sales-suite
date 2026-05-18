<script setup lang="ts">
import { LoaderCircle, TriangleAlert, Inbox } from 'lucide-vue-next'

// 読み込み中・エラー・データなしの状態表示をまとめて扱う。
defineProps<{
  loading: boolean
  error: string | null
  empty?: boolean
  emptyMessage?: string
}>()
</script>

<template>
  <div
    v-if="loading"
    class="flex flex-col items-center justify-center gap-3 py-16 text-slate-500"
  >
    <LoaderCircle class="h-8 w-8 animate-spin text-indigo-500" />
    <p class="text-sm">読み込み中...</p>
  </div>

  <div
    v-else-if="error"
    class="flex flex-col items-center justify-center gap-2 rounded-xl border border-red-200 bg-red-50 py-12 text-center"
  >
    <TriangleAlert class="h-8 w-8 text-red-500" />
    <p class="text-sm font-medium text-red-700">{{ error }}</p>
    <p class="text-xs text-red-500">時間をおいて再度お試しください。</p>
  </div>

  <div
    v-else-if="empty"
    class="flex flex-col items-center justify-center gap-2 py-16 text-slate-400"
  >
    <Inbox class="h-8 w-8" />
    <p class="text-sm">{{ emptyMessage ?? '表示できるデータがありません。' }}</p>
  </div>

  <slot v-else />
</template>
