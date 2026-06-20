<script setup lang="ts">
/**
 * アプリ共通レイアウト。rakuten-ec-suite のポータル型ナビゲーションに倣った
 * 「ヘッダー（パンくず・戻る）＋カテゴリタブバー＋メイン」の構成。
 * 旧サイドバー構成は廃止し、メニュー構成は utils/navigation.ts を SoT とする。
 *
 * 高さ戦略は旧レイアウトを踏襲する:
 * - ルートを h-screen（= 100vh）で画面の高さに固定し、main 内部のみスクロールさせる。
 * - テーブル類の sticky ヘッダーは各コンポーネント自身のスクロールコンテナ内で完結する
 *   一方、DataTable の fillHeight（h-full チェーン）は main の固定高に依存するため、
 *   この戦略（h-screen + main のみスクロール）は変更しないこと。
 *
 * タブバーは現在パスが属するカテゴリでのみ表示する（ホーム・非カテゴリページは
 * ヘッダーのみ）。カテゴリ判定は route.path から導出するため、ページ側の設定は不要。
 */
const route = useRoute()
const { accountType } = useAccountType()

// タブバーはアカウント種別で表示ページを絞り込む（ロール外ページのタブは出さない）。
const currentCategory = computed(() => visibleCategoryForPath(route.path, accountType.value))
</script>

<template>
  <div class="flex h-screen flex-col overflow-hidden">
    <AppHeader />
    <CategoryTabBar v-if="currentCategory" :category="currentCategory" />
    <main class="flex-1 overflow-y-auto p-4 lg:p-6">
      <slot />
    </main>
  </div>
</template>
