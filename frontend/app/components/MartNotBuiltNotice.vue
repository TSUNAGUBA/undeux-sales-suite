<script setup lang="ts">
// 分析 mart が未構築のときに各 mart ページで表示するガード。
// 再構築は全社サマリー（スタースキーマ）ページから実行する。
</script>

<template>
  <div
    class="rounded-xl border border-amber-200 bg-amber-50 p-8 text-center text-sm text-amber-700"
  >
    mart がまだ構築されていません。
    <NuxtLink to="/mart" class="font-medium text-amber-800 underline hover:text-amber-900">
      全社サマリー（スタースキーマ）
    </NuxtLink>
    で「mart を再構築」を実行してください。
  </div>
</template>
