<script setup lang="ts">
import { Menu } from 'lucide-vue-next'

// サイドバーの開閉状態（PC: 折りたたみ / モバイル: ドロワー）と派生クラスは
// 全て useSidebar に集約されている。幅・パディング値は composable 側を SoT とする。
const {
  collapsed,
  mobileOpen,
  transitionsEnabled,
  mainPaddingClass,
  toggleCollapsed,
  closeMobile,
  toggleMobile,
  enableTransitions,
  subscribeStorageSync,
  subscribeBreakpoint,
} = useSidebar()

let detachStorageSync: (() => void) | null = null
let detachBreakpointSync: (() => void) | null = null

onMounted(() => {
  // 初期マウントが完了したフレームでトランジションを有効化する。
  // 永続化された collapsed=true で再描画されるユーザーが、最初の paint で
  // 展開→折りたたみのアニメーションを見ないようにする（FOUC 抑止）。
  nextTick(() => {
    enableTransitions()
  })

  detachStorageSync = subscribeStorageSync()
  detachBreakpointSync = subscribeBreakpoint()
})

onBeforeUnmount(() => {
  detachStorageSync?.()
  detachStorageSync = null
  detachBreakpointSync?.()
  detachBreakpointSync = null
})

const SIDEBAR_DOM_ID = 'app-sidebar'
const SIDEBAR_MOBILE_DOM_ID = 'app-sidebar-mobile-drawer'

const mainPaddingTransitionClass = computed(() =>
  transitionsEnabled.value ? 'transition-[padding] duration-200 ease-out' : '',
)
</script>

<template>
  <!--
    レイアウトの高さ戦略:
    - ルートは h-screen（= 100vh）で画面の高さに固定する。
    - PC: サイドバーは fixed inset-y-0 left-0 でビューポートに直接固定する。
      これによりメインコンテンツの高さ・スクロールの影響を一切受けない。
    - メイン領域は overflow-hidden + main の overflow-y-auto により、
      ページ全体ではなく main 内部だけがスクロールする。
    - サイドバーの幅は useSidebar の sidebarWidthClass で AppSidebar 側に一本化。
      外側の固定ラッパーは位置（fixed inset-y-0）のみを担当する。
  -->
  <div class="flex h-screen overflow-hidden">
    <!-- デスクトップ: ビューポート左端に固定するサイドバー（fixed） -->
    <div class="hidden lg:fixed lg:inset-y-0 lg:left-0 lg:z-30 lg:block">
      <AppSidebar
        :sidebar-id="SIDEBAR_DOM_ID"
        :collapsed="collapsed"
        :show-collapse-toggle="true"
        :transitions-enabled="transitionsEnabled"
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
        <AppSidebar
          :sidebar-id="SIDEBAR_MOBILE_DOM_ID"
          :show-close-button="true"
          @navigate="closeMobile"
          @close="closeMobile"
        />
      </div>
    </div>

    <!--
      メインコンテンツ。PC ではサイドバーが fixed なので、サイドバー幅分のパディングで
      本文の開始位置をずらす。パディング値は useSidebar.mainPaddingClass に一本化。
    -->
    <div
      class="flex min-w-0 flex-1 flex-col"
      :class="[mainPaddingClass, mainPaddingTransitionClass]"
    >
      <header
        class="flex items-center gap-3 border-b border-slate-200 bg-white px-4 py-3 lg:hidden"
      >
        <button
          type="button"
          class="flex h-11 w-11 items-center justify-center rounded-lg text-slate-600 hover:bg-slate-100"
          aria-label="メニューを開く"
          :aria-expanded="mobileOpen"
          :aria-controls="SIDEBAR_MOBILE_DOM_ID"
          @click="toggleMobile"
        >
          <Menu class="h-5 w-5" />
        </button>
        <span class="font-bold text-slate-800">UndeuxSales</span>
      </header>

      <main class="flex-1 overflow-y-auto p-4 lg:p-6">
        <slot />
      </main>
    </div>
  </div>
</template>
