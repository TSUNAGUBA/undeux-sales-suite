<script setup lang="ts">
/**
 * 写真帳のスペックシートカード（投入企画書レイアウト）。
 *
 * 参照レイアウトに倣い、ヘッダ項目（品番・品名・PB・素材・下代・上代・投入予定日・投入日・
 * 減価・PB検査）＋商品画像＋（カラー）＋属性＋サイズ／カラー欄＋フッタ項目（初回各・股上・
 * 股下・裾巾・FOB・受注日・先付・伝発）を表示する。商品マスタに無い項目は空白（—）。
 * 1〜4件の選択に対応（チェックボックス）。項目定義は utils/photobook を共有する。
 */
import { ExternalLink } from 'lucide-vue-next'
import type { MasterProductSummary } from '~/types/api'

const props = defineProps<{
  product: MasterProductSummary
  selected: boolean
  /** 選択上限に達しており、未選択のこのカードを新規選択できない場合 true。 */
  disabled: boolean
  href: string
}>()

const emit = defineEmits<{ toggle: [] }>()

const headerFields = computed(() => photobookHeaderFields(props.product))
const statsFields = computed(() => photobookStatsFields(props.product))
const footerFields = computed(() => photobookFooterFields())
</script>

<template>
  <div
    class="flex flex-col overflow-hidden rounded-xl border bg-white shadow-sm transition-colors"
    :class="selected ? 'border-indigo-500 ring-1 ring-indigo-300' : 'border-slate-200'"
  >
    <!-- ヘッダ: 選択チェック + 詳細リンク -->
    <div class="flex items-center justify-between gap-2 border-b border-slate-200 bg-slate-50 px-2.5 py-1.5">
      <label
        class="inline-flex cursor-pointer items-center gap-1.5 text-xs font-medium text-slate-600"
        :class="disabled && !selected ? 'cursor-not-allowed opacity-40' : ''"
      >
        <input
          type="checkbox"
          class="h-4 w-4 accent-indigo-600"
          :checked="selected"
          :disabled="disabled && !selected"
          @change="emit('toggle')"
        >
        選択
      </label>
      <NuxtLink :to="href" class="inline-flex items-center gap-1 text-xs text-indigo-600 hover:text-indigo-800">
        詳細
        <ExternalLink class="h-3 w-3" />
      </NuxtLink>
    </div>

    <!-- ヘッダ項目グリッド（label|value を2列） -->
    <div class="grid grid-cols-2 gap-px bg-slate-200">
      <div v-for="f in headerFields" :key="f.label" class="bg-white px-2 py-1">
        <span class="block text-[10px] text-slate-400">{{ f.label }}</span>
        <span class="block truncate text-xs text-slate-700" :title="f.value">{{ f.value || '—' }}</span>
      </div>
    </div>

    <!-- 画像 -->
    <div class="aspect-square w-full bg-slate-100">
      <ProductImage :src="product.primaryImageUrl" :alt="product.productName" icon-class="h-10 w-10" :show-label="false" />
    </div>

    <!-- （カラー）欄（実カラー明細はマスタ一覧に無いため空欄） -->
    <div class="border-t border-slate-200 px-2 py-1">
      <span class="text-[10px] text-slate-400">（カラー）</span>
      <div class="h-4" />
    </div>

    <!-- 属性・実績 -->
    <div class="grid grid-cols-2 gap-px border-t border-slate-200 bg-slate-200">
      <div v-for="f in statsFields" :key="f.label" class="bg-white px-2 py-1">
        <span class="block text-[10px] text-slate-400">{{ f.label }}</span>
        <span class="block text-xs text-slate-700">{{ f.value || '—' }}</span>
      </div>
    </div>

    <!-- サイズ／カラー欄（数量データはマスタに無いため空欄で表現） -->
    <div class="border-t border-slate-200 px-2 py-1">
      <span class="text-[10px] text-slate-400">サイズ／カラー（初回数量）</span>
      <p class="text-[11px] text-slate-300">（数量データなし）</p>
    </div>

    <!-- フッタ項目 -->
    <div class="mt-auto grid grid-cols-2 gap-px border-t border-slate-200 bg-slate-200">
      <div v-for="f in footerFields" :key="f.label" class="bg-white px-2 py-1">
        <span class="block text-[10px] text-slate-400">{{ f.label }}</span>
        <span class="block text-xs text-slate-700">{{ f.value || '—' }}</span>
      </div>
    </div>
  </div>
</template>
