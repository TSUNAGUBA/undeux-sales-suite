<script setup lang="ts">
/**
 * 商品詳細分析の週別マトリクス（SKU × 週）。
 *
 * 左メタ列（品番/単品・商品記号/棚割1/棚割2・品名/カラー/サイズ・上代）＋ 行区分（区分）＋ 週列 で構成する。
 * 行区分（売数・在庫数・在日・販売価格・気温[東京/札幌/沖縄]）は visibleCategories で表示中のものだけ描画する。
 * SKU グループごとに上罫線を強調し、値下げ週（売数）・在日の色分けを表示する。
 *
 * 商品詳細分析ページと商品の詳細分析ページ（配下SKU表）で共用する純粋な表示コンポーネント。
 */
import type { ItemDetailRowCategory, ItemDetailViewRow } from '~/utils/itemDetail'

const props = defineProps<{
  weeks: string[]
  rows: ItemDetailViewRow[]
  /** 表示中の行区分（順序どおりに描画する）。 */
  visibleCategories: ItemDetailRowCategory[]
}>()

/** 週セルのクラス（グループ上罫線・区分別の強調色）。 */
function weekCellClass(cat: ItemDetailRowCategory, row: ItemDetailViewRow, week: string, first: boolean): string {
  const parts = ['border-l', 'border-l-slate-100', 'px-2', 'py-1', 'text-right', 'tabular-nums']
  parts.push(first ? 'border-t-2 border-t-slate-300' : 'border-t border-t-slate-100')
  const p = row.pointByWeek.get(week)
  if (cat === 'quantity' && p?.isMarkdown) {
    parts.push('bg-rose-100 font-semibold text-rose-700')
  } else if (cat === 'stockDays') {
    parts.push(stockDaysColorClass(p?.stockDays ?? null))
  } else if (cat === 'tempTokyo' || cat === 'tempSapporo' || cat === 'tempOkinawa') {
    parts.push('bg-sky-50/50 text-sky-700')
  } else if (cat === 'salePrice') {
    parts.push('text-slate-600')
  } else {
    parts.push('text-slate-700')
  }
  return parts.join(' ')
}

/** 区分ラベルセルのクラス（気温行は淡い青地）。 */
function labelCellClass(cat: ItemDetailRowCategory, first: boolean): string {
  const parts = ['border-r', 'border-r-slate-200', 'px-2', 'py-1', 'text-center', 'text-slate-500']
  parts.push(first ? 'border-t-2 border-t-slate-300' : 'border-t border-t-slate-100')
  if (cat === 'tempTokyo' || cat === 'tempSapporo' || cat === 'tempOkinawa') parts.push('bg-sky-50/50')
  return parts.join(' ')
}

/** 値下げ週の売数セルにツールチップ（売価）を出す。 */
function cellTitle(cat: ItemDetailRowCategory, row: ItemDetailViewRow, week: string): string | undefined {
  if (cat !== 'quantity') return undefined
  const p = row.pointByWeek.get(week)
  return p?.isMarkdown ? `値下げ週（売価 ${formatCurrency(p.salePrice)}）` : undefined
}
</script>

<template>
  <div class="overflow-auto rounded-xl border border-slate-200 bg-white">
    <table class="text-xs" style="border-collapse: separate; border-spacing: 0">
      <thead>
        <tr>
          <th class="sticky left-0 z-20 whitespace-nowrap border-b border-r border-slate-200 bg-slate-50 px-2 py-2 text-left font-semibold text-slate-600">品番/単品</th>
          <th class="whitespace-nowrap border-b border-r border-slate-200 bg-slate-50 px-2 py-2 text-left font-semibold text-slate-600">商品記号/棚割1/棚割2</th>
          <th class="whitespace-nowrap border-b border-r border-slate-200 bg-slate-50 px-2 py-2 text-left font-semibold text-slate-600">品名 / カラー / サイズ</th>
          <th class="whitespace-nowrap border-b border-r border-slate-200 bg-slate-50 px-2 py-2 text-right font-semibold text-slate-600">上代</th>
          <th class="whitespace-nowrap border-b border-r border-slate-200 bg-slate-50 px-2 py-2 text-center font-semibold text-slate-600">区分</th>
          <th
            v-for="w in weeks"
            :key="w"
            class="whitespace-nowrap border-b border-l border-slate-200 bg-slate-50 px-2 py-2 text-right font-medium text-slate-500"
          >{{ w.slice(5) }}</th>
        </tr>
      </thead>
      <tbody>
        <template v-for="row in rows" :key="row.key">
          <tr v-for="(cat, ci) in visibleCategories" :key="`${row.key}-${cat}`">
            <!-- メタ列（各 SKU グループの先頭行にのみ rowspan で描画） -->
            <template v-if="ci === 0">
              <th
                :rowspan="visibleCategories.length"
                class="sticky left-0 z-10 whitespace-nowrap border-t-2 border-t-slate-300 border-r border-r-slate-200 bg-white px-2 py-1 text-left align-top font-semibold text-slate-700"
              >
                {{ row.hinbanCode }}-{{ row.tanpinCode }}
              </th>
              <td :rowspan="visibleCategories.length" class="border-t-2 border-t-slate-300 border-r border-r-slate-200 px-2 py-1 align-top">
                <div class="font-mono text-slate-600">{{ row.shohinKigou || '—' }}</div>
                <div class="text-[11px] text-slate-400">{{ row.tanawari1 || '—' }} / {{ row.tanawari2 || '—' }}</div>
              </td>
              <td :rowspan="visibleCategories.length" class="border-t-2 border-t-slate-300 border-r border-r-slate-200 px-2 py-1 align-top text-slate-600">
                <div class="max-w-[160px] truncate font-medium text-slate-700" :title="row.hinmei">{{ row.hinmei || '—' }}</div>
                <div class="text-[11px] text-slate-400">{{ row.colorName || '—' }} / {{ row.sizeName || '—' }}</div>
              </td>
              <td :rowspan="visibleCategories.length" class="border-t-2 border-t-slate-300 border-r border-r-slate-200 px-2 py-1 text-right align-top tabular-nums text-slate-600">
                {{ formatCurrency(row.listPrice) }}
              </td>
            </template>
            <!-- 区分ラベル -->
            <td :class="labelCellClass(cat, ci === 0)">{{ itemDetailRowCategoryLabel(cat) }}</td>
            <!-- 週セル -->
            <td
              v-for="w in weeks"
              :key="`${cat}-${w}`"
              :class="weekCellClass(cat, row, w, ci === 0)"
              :title="cellTitle(cat, row, w)"
            >
              {{ itemDetailCellText(cat, row, w) }}
            </td>
          </tr>
        </template>
      </tbody>
    </table>
  </div>
</template>
