<script setup lang="ts">
/**
 * 残在庫（倉庫在庫）タブ。
 *
 * 倉庫在庫・取置・先付・発注は店頭在庫スナップショットに無いため、業態×部門でモック生成した SKU を
 * 表形式で表示する。業態・部門はページ上部のフィルター（ダッシュボードと同じ）を引き継ぎ、
 * 商品記号は本タブの部分一致入力で絞り込む。表示項目は EDI／基幹／算出／WMS の各ソースに対応する。
 */
import { RotateCcw, Search } from 'lucide-vue-next'
import type { BusinessTypeOption, CodeName } from '~/types/api'
import type { WarehouseRow } from '~/utils/inventoryMock'

const props = defineProps<{
  businessTypeOptions: BusinessTypeOption[]
  departmentOptions: CodeName[]
  /** ページ上部フィルター（ダッシュボードと共通）の選択（空＝すべて）。 */
  selectedBusinessTypes: string[]
  selectedDepartments: string[]
}>()

const shohinKigou = ref('')
const appliedKigou = ref('')

/** 対象業態コード（未選択＝全選択肢）。 */
const targetBusinessTypes = computed(() =>
  props.selectedBusinessTypes.length > 0
    ? props.selectedBusinessTypes
    : props.businessTypeOptions.map((b) => b.code),
)
/** 対象部門コード（未選択＝全選択肢）。 */
const targetDepartments = computed(() =>
  props.selectedDepartments.length > 0
    ? props.selectedDepartments
    : props.departmentOptions.map((d) => d.code),
)

const businessTypeName = (code: string): string =>
  props.businessTypeOptions.find((b) => b.code === code)?.shortName
  ?? props.businessTypeOptions.find((b) => b.code === code)?.name
  ?? code
const departmentName = (code: string): string =>
  props.departmentOptions.find((d) => d.code === code)?.name ?? code

const allRows = computed<WarehouseRow[]>(() =>
  buildWarehouseMock(targetBusinessTypes.value, targetDepartments.value),
)

const rows = computed<WarehouseRow[]>(() => {
  const kigo = appliedKigou.value.trim()
  if (!kigo) return allRows.value
  return allRows.value.filter((r) => r.shohinKigou.includes(kigo))
})

function applyKigou(): void {
  appliedKigou.value = shohinKigou.value
}
function resetKigou(): void {
  shohinKigou.value = ''
  appliedKigou.value = ''
}
</script>

<template>
  <div class="space-y-3">
    <p class="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700">
      倉庫在庫・取置・先付・発注は店頭在庫スナップショットに無いため、モックデータで表現しています。
      業態・部門は上部フィルターを引き継ぎ、商品記号は下記の部分一致で絞り込みます。
    </p>

    <div class="flex flex-wrap items-end gap-2">
      <div>
        <label class="mb-1 block text-xs font-medium text-slate-500">商品記号（部分一致）</label>
        <input
          v-model="shohinKigou"
          type="search"
          placeholder="例: LG11"
          class="w-56 rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
          @keydown.enter.prevent="applyKigou"
        >
      </div>
      <button
        type="button"
        class="flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-2 text-sm font-medium text-white hover:bg-indigo-700"
        @click="applyKigou"
      >
        <Search class="h-4 w-4" />
        適用
      </button>
      <button
        type="button"
        class="flex items-center gap-1.5 rounded-lg border border-slate-300 px-3 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
        @click="resetKigou"
      >
        <RotateCcw class="h-4 w-4" />
        クリア
      </button>
      <span class="ml-auto text-xs text-slate-400">{{ formatNumber(rows.length) }} 件</span>
    </div>

    <div
      v-if="rows.length === 0"
      class="rounded-xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-400"
    >
      該当する残在庫データがありません。フィルターを見直してください。
    </div>

    <!-- PC: 表（ソース別のグループ見出し） -->
    <div v-else class="hidden overflow-x-auto rounded-xl border border-slate-200 bg-white lg:block">
      <table class="w-full text-xs">
        <thead>
          <tr class="text-slate-500">
            <th colspan="6" class="border-b border-slate-200 bg-slate-50 px-2 py-1.5 text-left font-semibold">商品</th>
            <th colspan="2" class="border-b border-l border-slate-200 bg-emerald-50 px-2 py-1.5 text-center font-semibold text-emerald-700">EDI</th>
            <th colspan="3" class="border-b border-l border-slate-200 bg-sky-50 px-2 py-1.5 text-center font-semibold text-sky-700">基幹</th>
            <th colspan="1" class="border-b border-l border-slate-200 bg-violet-50 px-2 py-1.5 text-center font-semibold text-violet-700">算出</th>
            <th colspan="2" class="border-b border-l border-slate-200 bg-amber-50 px-2 py-1.5 text-center font-semibold text-amber-700">WMS</th>
          </tr>
          <tr class="text-slate-500">
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">業態</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">部門</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">商品記号</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">品番</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">単品</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">品名</th>
            <th class="whitespace-nowrap border-l border-slate-200 bg-slate-50 px-2 py-2 text-right font-medium">先付数</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">発注数</th>
            <th class="whitespace-nowrap border-l border-slate-200 bg-slate-50 px-2 py-2 text-right font-medium">納品数</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">売上数</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">在庫数(店頭)</th>
            <th class="whitespace-nowrap border-l border-slate-200 bg-slate-50 px-2 py-2 text-right font-medium">発注済未納品</th>
            <th class="whitespace-nowrap border-l border-slate-200 bg-slate-50 px-2 py-2 text-right font-medium">取置在庫</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">倉庫在庫数</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-slate-100">
          <tr v-for="row in rows" :key="row.key" class="hover:bg-slate-50">
            <td class="whitespace-nowrap px-2 py-1.5 text-slate-600">{{ businessTypeName(row.businessTypeCode) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-slate-600">{{ departmentName(row.departmentCode) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 font-mono text-slate-600">{{ row.shohinKigou }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 font-mono text-slate-600">{{ row.hinbanCode }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 font-mono text-slate-600">{{ row.tanpinCode }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-slate-700">{{ row.hinmei }}</td>
            <td class="whitespace-nowrap border-l border-slate-100 px-2 py-1.5 text-right tabular-nums text-slate-600">{{ formatNumber(row.sakizukeCount) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums text-slate-600">{{ formatDecimal(row.hatchuCount, 1) }}</td>
            <td class="whitespace-nowrap border-l border-slate-100 px-2 py-1.5 text-right tabular-nums text-slate-600">{{ formatNumber(row.ruikeiNohinCount) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums text-slate-600">{{ formatNumber(row.ruikeiUriageCount) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums text-slate-600">{{ formatNumber(row.zaikosu) }}</td>
            <td class="whitespace-nowrap border-l border-slate-100 px-2 py-1.5 text-right tabular-nums font-medium text-violet-700">{{ formatNumber(row.orderNotDelivered) }}</td>
            <td class="whitespace-nowrap border-l border-slate-100 px-2 py-1.5 text-right tabular-nums text-slate-600">{{ formatNumber(row.reservedStock) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums font-semibold text-amber-700">{{ formatNumber(row.warehouseStock) }}</td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- モバイル: カード -->
    <div v-if="rows.length > 0" class="space-y-2 lg:hidden">
      <div v-for="row in rows" :key="row.key" class="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
        <div class="mb-2 flex items-center justify-between gap-2">
          <span class="font-mono text-sm font-semibold text-slate-700">{{ row.shohinKigou }} / {{ row.hinbanCode }}-{{ row.tanpinCode }}</span>
          <span class="text-xs text-slate-400">{{ businessTypeName(row.businessTypeCode) }}・{{ departmentName(row.departmentCode) }}</span>
        </div>
        <p class="mb-2 text-xs text-slate-600">{{ row.hinmei }}</p>
        <div class="grid grid-cols-2 gap-x-3 gap-y-1 text-xs">
          <div class="flex justify-between"><span class="text-slate-500">先付数</span><span class="tabular-nums">{{ formatNumber(row.sakizukeCount) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">発注数</span><span class="tabular-nums">{{ formatDecimal(row.hatchuCount, 1) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">納品数</span><span class="tabular-nums">{{ formatNumber(row.ruikeiNohinCount) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">売上数</span><span class="tabular-nums">{{ formatNumber(row.ruikeiUriageCount) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">在庫数(店頭)</span><span class="tabular-nums">{{ formatNumber(row.zaikosu) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">発注済未納品</span><span class="tabular-nums font-medium text-violet-700">{{ formatNumber(row.orderNotDelivered) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">取置在庫</span><span class="tabular-nums">{{ formatNumber(row.reservedStock) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">倉庫在庫数</span><span class="tabular-nums font-semibold text-amber-700">{{ formatNumber(row.warehouseStock) }}</span></div>
        </div>
      </div>
    </div>
  </div>
</template>
