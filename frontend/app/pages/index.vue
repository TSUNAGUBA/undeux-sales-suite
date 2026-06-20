<script setup lang="ts">
/**
 * ホーム（目的別メニュー）。rakuten-ec-suite のポータル型ナビゲーションに倣い、
 * 目的（カテゴリ）を選んでドリルダウンし、配下ページへタブで移動する構成の入口。
 * 旧仕様の `/` → `/mart` リダイレクトは本ページに置き換えた（全社サマリーへは
 * 業績モニタリングカードの先頭タブとして到達できる）。
 *
 * カードはアカウント種別（サプライヤー/バイヤー）で出し分ける（categoriesForRole）。
 * バイヤーは OTB管理を起点に、サプライヤーは販売モニタリングを起点にする。
 */
useHead({ title: 'ホーム | UndeuxSales' })

const { accountType, meta } = useAccountType()
const categories = computed(() => categoriesForRole(accountType.value))
</script>

<template>
  <div class="mx-auto w-full max-w-5xl">
    <div class="mb-6">
      <h1 class="text-xl font-bold text-slate-800">ホーム</h1>
      <p class="mt-1 text-sm text-slate-500">
        {{ meta.label }}としてログイン中です。目的を選んでください。選んだ先では、関連するページだけがタブで並びます。
      </p>
    </div>

    <div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
      <HomeCategoryCard
        v-for="category in categories"
        :key="category.id"
        :category="category"
      />
    </div>
  </div>
</template>
