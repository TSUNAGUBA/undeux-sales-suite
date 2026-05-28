<script setup lang="ts">
import { Menu, X } from 'lucide-vue-next'

// サイドバーの開閉状態（PC: 折りたたみ / モバイル: ドロワー）は composable に集約。
// PC の折りたたみ状態は localStorage で永続化される。
const {
  collapsed,
  mobileOpen,
  toggleCollapsed,
  closeMobile,
  toggleMobile,
} = useSidebar()
</script>

<template>
  <!--
    レイアウトの高さ戦略:
    - ルートは h-screen（= 100vh）で画面の高さに固定する。
    - PC: サイドバーは fixed inset-y-0 left-0 でビューポートに直接固定する。
      これによりメインコンテンツの高さ・スクロールの影響を一切受けない。
    - メイン領域は overflow-hidden + main の overflow-y-auto により、
      ページ全体ではなく main 内部だけがスクロールする。
  -->
  <div class="flex h-screen overflow-hidden">
    <!-- デスクトップ: 画面に固定するサイドバー（fixed） -->
    <div
      class="hidden lg:fixed lg:inset-y-0 lg:left-0 lg:z-30 lg:block"
      :class="collapsed ? 'lg:w-16' : 'lg:w-64'"
    >
      <AppSidebar
        :collapsed="collapsed"
        :show-collapse-toggle="true"
        @toggle-collapsed="toggleCollapsed"
      />
    </div>

    <!-- モバイル: ドロワー（オーバーレイ） -->
    <div v-if="mobileOpen" class="fixed inset-0 z-40 lg:hidden">
      <div
        class="absolute inset-0 bg-slate-900/50"
        aria-hidden="true"
        @click="closeMobile"
      />
      <div class="absolute inset-y-0 left-0 z-50">
        <AppSidebar @navigate="closeMobile" />
      </div>
    </div>

    <!--
      メインコンテンツ。PC ではサイドバーが fixed なので、サイドバー幅分のパディング
      で本文の開始位置をずらす。折りたたみ状態に応じて pl-16 / pl-64 を切替える。
    -->
    <div
      class="flex min-w-0 flex-1 flex-col transition-[padding] duration-200 ease-out"
      :class="collapsed ? 'lg:pl-16' : 'lg:pl-64'"
    >
      <header
        class="flex items-center gap-3 border-b border-slate-200 bg-white px-4 py-3 lg:hidden"
      >
        <button
          type="button"
          class="rounded-lg p-2 text-slate-600 hover:bg-slate-100"
          :aria-label="mobileOpen ? 'メニューを閉じる' : 'メニューを開く'"
          :aria-expanded="mobileOpen"
          @click="toggleMobile"
        >
          <X v-if="mobileOpen" class="h-5 w-5" />
          <Menu v-else class="h-5 w-5" />
        </button>
        <span class="font-bold text-slate-800">UndeuxSales</span>
      </header>

      <main class="flex-1 overflow-y-auto p-4 lg:p-6">
        <slot />
      </main>
    </div>
  </div>
</template>
