<script setup lang="ts">
import { Shirt, ArrowRight } from 'lucide-vue-next'

useHead({ title: '商品軸分析 | UndeuxSales' })
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">商品軸分析</h1>
      <p class="text-sm text-slate-500">
        商品マスタの 1 商品を選択すると、期間内の売上 KPI、週次トレンド、SKU 別売上、業態別売上を一画面で確認できます。
      </p>
    </div>

    <div class="rounded-xl border border-dashed border-slate-300 bg-white p-6 text-center">
      <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-indigo-50 text-indigo-600">
        <Shirt class="h-6 w-6" />
      </div>
      <p class="mt-3 text-sm font-semibold text-slate-700">商品マスタから分析対象を選択してください</p>
      <p class="mt-1 text-xs text-slate-500">
        商品カードをクリックすると、その商品の包括分析画面が開きます。
      </p>
      <NuxtLink
        to="/product-master"
        class="mt-4 inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700"
      >
        商品マスタを開く
        <ArrowRight class="h-4 w-4" />
      </NuxtLink>
    </div>
  </div>
</template>
