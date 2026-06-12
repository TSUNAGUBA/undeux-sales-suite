<script setup lang="ts">
/**
 * ルート（/）はスタースキーマ適用後の全社サマリー（/mart）へリダイレクトする。
 * プロトタイプ段階の旧・全社サマリーは廃止済み（分析ページの正は /mart 配下）。
 * ブックマーク・外部リンクからの旧 URL 流入を新トップへ誘導するためルート自体は残す。
 */
definePageMeta({ redirect: '/mart' })
</script>

<template>
  <div />
</template>
