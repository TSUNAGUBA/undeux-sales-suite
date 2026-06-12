<script setup lang="ts">
import type { NuxtError } from '#app'

/**
 * アプリ全体のエラーページ。未定義ルート（廃止済みの旧分析ページ URL を含む）への
 * アクセスは 404 としてここに到達する。素の既定エラー画面ではなく、全社サマリー
 * （/mart）への復帰導線を備えたブランド一貫の画面を出す。
 */
const props = defineProps<{ error: NuxtError }>()

const isNotFound = computed(() => props.error.statusCode === 404)

function backToHome(): void {
  // エラー状態をクリアしてトップ（全社サマリー）へ。
  clearError({ redirect: '/mart' })
}
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-slate-50 p-6">
    <div class="w-full max-w-md rounded-xl border border-slate-200 bg-white p-8 text-center shadow-sm">
      <p class="text-5xl font-bold text-slate-300">{{ error.statusCode }}</p>
      <h1 class="mt-3 text-lg font-bold text-slate-800">
        {{ isNotFound ? 'ページが見つかりません' : 'エラーが発生しました' }}
      </h1>
      <p class="mt-2 text-sm text-slate-500">
        <template v-if="isNotFound">
          URL をご確認ください。プロトタイプ段階の旧分析ページは廃止され、
          分析機能は全社サマリー配下のページに移行しています。
        </template>
        <template v-else>
          {{ error.statusMessage || '時間をおいて再度お試しください。' }}
        </template>
      </p>
      <button
        type="button"
        class="mt-5 inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700"
        @click="backToHome"
      >
        全社サマリーへ戻る
      </button>
    </div>
  </div>
</template>
