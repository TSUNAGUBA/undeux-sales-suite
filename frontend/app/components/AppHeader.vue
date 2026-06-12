<script setup lang="ts">
import { ArrowLeft, ChevronRight, LogOut } from 'lucide-vue-next'

/**
 * 全ページ共通ヘッダー。ブランド（ホームへのリンク）＋現在カテゴリのパンくず＋
 * 戻るボタン（1つ上の階層へ）＋ユーザー情報・ログアウトを担う。
 * パンくず・戻る先は utils/navigation.ts（メニュー構成の SoT）から導出する。
 */
const route = useRoute()
const router = useRouter()
const { user, logout } = useAuth()

const currentCategory = computed(() => findCategoryByPath(route.path))
const parentPath = computed(() => findParentPath(route.path))

async function handleLogout(): Promise<void> {
  await logout()
  await router.push('/login')
}
</script>

<template>
  <header
    class="flex h-14 shrink-0 items-center gap-1.5 border-b border-slate-200 bg-white px-3 lg:px-6"
  >
    <NuxtLink
      v-if="parentPath"
      :to="parentPath"
      class="flex h-11 w-11 shrink-0 items-center justify-center rounded-lg text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-800"
      aria-label="1つ上の階層へ戻る"
      title="1つ上の階層へ戻る"
    >
      <ArrowLeft class="h-4 w-4" />
    </NuxtLink>

    <nav class="flex min-w-0 items-center gap-1.5" aria-label="パンくず">
      <NuxtLink
        to="/"
        class="shrink-0 text-sm font-bold text-slate-800 transition-colors hover:text-indigo-700"
        title="ホーム（目的別メニュー）へ"
      >
        UndeuxSales
      </NuxtLink>
      <template v-if="currentCategory">
        <ChevronRight class="h-3.5 w-3.5 shrink-0 text-slate-400" aria-hidden="true" />
        <NuxtLink
          :to="categoryDefaultPath(currentCategory)"
          class="flex min-w-0 items-center gap-1.5 text-sm font-medium text-slate-600 transition-colors hover:text-indigo-700"
          :title="`${currentCategory.label}の先頭ページへ`"
        >
          <component :is="currentCategory.icon" class="h-4 w-4 shrink-0" />
          <span class="truncate">{{ currentCategory.label }}</span>
        </NuxtLink>
      </template>
    </nav>

    <div class="ml-auto flex shrink-0 items-center gap-2">
      <span
        class="hidden max-w-44 truncate text-xs text-slate-400 sm:block"
        :title="user?.email ?? ''"
      >
        {{ user?.email ?? 'ゲスト' }}
      </span>
      <button
        type="button"
        class="flex h-11 items-center gap-1.5 rounded-lg px-2.5 text-sm font-medium text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-800"
        aria-label="ログアウト"
        title="ログアウト"
        @click="handleLogout"
      >
        <LogOut class="h-4 w-4 shrink-0" />
        <span class="hidden sm:inline">ログアウト</span>
      </button>
    </div>
  </header>
</template>
