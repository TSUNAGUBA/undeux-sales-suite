<script setup lang="ts">
import { ChevronRight } from 'lucide-vue-next'
import type { NavCategory } from '~/utils/navigation'

/**
 * ホーム（目的別メニュー）のカテゴリカード。押下でカテゴリの既定ページ
 * （先頭タブ）へ遷移する。配下ページ名も列挙し、ドリルダウンする前に
 * 行き先に何があるかを把握できるようにする（チップは装飾であり個別リンクではない）。
 */
const props = defineProps<{ category: NavCategory }>()
</script>

<template>
  <NuxtLink
    :to="categoryDefaultPath(props.category)"
    class="group flex h-full flex-col gap-3 rounded-xl border border-slate-200 bg-white p-5 shadow-sm transition-all hover:-translate-y-0.5 hover:border-indigo-400 hover:shadow-md focus-visible:-translate-y-0.5 focus-visible:border-indigo-400"
  >
    <div class="flex items-center gap-3">
      <span
        class="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-indigo-50 text-indigo-600"
      >
        <component :is="props.category.icon" class="h-5 w-5" />
      </span>
      <span class="min-w-0 truncate text-base font-bold text-slate-800 group-hover:text-indigo-700">
        {{ props.category.label }}
      </span>
      <ChevronRight
        class="ml-auto h-4 w-4 shrink-0 text-slate-300 transition-colors group-hover:text-indigo-500"
        aria-hidden="true"
      />
    </div>

    <p class="text-sm text-slate-500">{{ props.category.description }}</p>

    <ul class="mt-auto flex flex-wrap gap-1.5" aria-label="含まれるページ">
      <li
        v-for="page in props.category.pages"
        :key="page.path"
        class="flex items-center gap-1 rounded-md bg-slate-100 px-2 py-1 text-xs text-slate-600"
      >
        <component :is="page.icon" class="h-3.5 w-3.5 shrink-0 text-slate-400" />
        {{ page.label }}
      </li>
    </ul>
  </NuxtLink>
</template>
