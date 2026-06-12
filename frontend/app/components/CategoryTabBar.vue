<script setup lang="ts">
import type { NavCategory } from '~/utils/navigation'

/**
 * カテゴリ配下ページのタブバー。アクティブ判定は URL（route.path）と同期するため、
 * ブラウザの戻る/進む・ブックマーク・直リンクでもタブ表示が正しく追従する。
 * 詳細サブルート（matchSubroutes を持つページ）でも親タブをアクティブ表示する。
 */
const props = defineProps<{ category: NavCategory }>()

const route = useRoute()
</script>

<template>
  <nav
    class="shrink-0 border-b border-slate-200 bg-white px-3 lg:px-6"
    :aria-label="`${props.category.label}のページ切替`"
  >
    <ul class="scrollbar-hide -mb-px flex gap-1 overflow-x-auto">
      <li v-for="page in props.category.pages" :key="page.path" class="shrink-0">
        <NuxtLink
          :to="page.path"
          class="flex items-center gap-1.5 whitespace-nowrap border-b-2 px-3 py-3 text-sm font-medium transition-colors"
          :class="
            isNavPageActive(page, route.path)
              ? 'border-indigo-600 text-indigo-700'
              : 'border-transparent text-slate-500 hover:border-slate-300 hover:text-slate-800'
          "
          :aria-current="isNavPageActive(page, route.path) ? 'page' : undefined"
        >
          <component :is="page.icon" class="h-4 w-4 shrink-0" />
          <span>{{ page.label }}</span>
        </NuxtLink>
      </li>
    </ul>
  </nav>
</template>
