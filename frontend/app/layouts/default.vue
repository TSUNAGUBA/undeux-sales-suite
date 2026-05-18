<script setup lang="ts">
import { Menu, X } from 'lucide-vue-next'

const sidebarOpen = ref(false)
</script>

<template>
  <div class="flex min-h-screen">
    <!-- デスクトップ: 固定サイドバー -->
    <div class="hidden lg:block">
      <AppSidebar />
    </div>

    <!-- モバイル: ドロワー -->
    <div v-if="sidebarOpen" class="fixed inset-0 z-40 lg:hidden">
      <div
        class="absolute inset-0 bg-slate-900/50"
        @click="sidebarOpen = false"
      />
      <div class="absolute inset-y-0 left-0 z-50">
        <AppSidebar @navigate="sidebarOpen = false" />
      </div>
    </div>

    <div class="flex min-w-0 flex-1 flex-col">
      <header
        class="flex items-center gap-3 border-b border-slate-200 bg-white px-4 py-3 lg:hidden"
      >
        <button
          type="button"
          class="rounded-lg p-2 text-slate-600 hover:bg-slate-100"
          aria-label="メニューを開く"
          @click="sidebarOpen = !sidebarOpen"
        >
          <X v-if="sidebarOpen" class="h-5 w-5" />
          <Menu v-else class="h-5 w-5" />
        </button>
        <span class="font-bold text-slate-800">UndeuxSales</span>
      </header>

      <main class="flex-1 p-4 lg:p-6">
        <slot />
      </main>
    </div>
  </div>
</template>
