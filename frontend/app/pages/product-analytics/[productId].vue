<script setup lang="ts">
/**
 * 廃止済みの旧・商品軸分析詳細（プロトタイプ段階）。productId（商品マスタID）は
 * 新・商品詳細分析でも同一のため、そのまま引き継いでリダイレクトする。
 */
definePageMeta({
  redirect: (to) => `/mart/products/${String(to.params.productId ?? '')}`,
})
</script>

<template>
  <div />
</template>
