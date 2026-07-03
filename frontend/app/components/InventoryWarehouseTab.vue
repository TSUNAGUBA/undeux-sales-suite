<script setup lang="ts">
/**
 * 残在庫（倉庫在庫）タブ。
 *
 * 倉庫在庫・取置・先付・発注は店頭在庫スナップショットに無いため、業態×部門でモック生成した SKU を
 * 表形式で表示する。業態・部門はページ上部のフィルター（ダッシュボードと同じ）を引き継ぎ、
 * 商品記号は本タブの部分一致入力で絞り込む。数量①〜⑬は 売上参照／算出／WMS の各ソースに対応する。
 * 帳票区分（売発注／情報発注）はチェックボックスで絞り込む（デフォルトは両方表示）。
 */
import { RotateCcw, Search } from 'lucide-vue-next'
import type { BusinessTypeOption, CodeName } from '~/types/api'
import type { OrderClass, WarehouseRow } from '~/utils/inventoryMock'

const props = defineProps<{
  businessTypeOptions: BusinessTypeOption[]
  departmentOptions: CodeName[]
  /** ページ上部フィルター（ダッシュボードと共通）の選択（空＝すべて）。 */
  selectedBusinessTypes: string[]
  selectedDepartments: string[]
}>()

const shohinKigou = ref('')
const appliedKigou = ref('')

/** 帳票区分フィルター（売発注／情報発注）。デフォルトは両方表示。空配列＝すべて表示。 */
const CHOHYO_OPTIONS: { value: OrderClass; label: string }[] = [
  { value: 'sales', label: ORDER_CLASS_LABEL.sales },
  { value: 'info', label: ORDER_CLASS_LABEL.info },
]
const selectedChohyo = ref<OrderClass[]>(['sales', 'info'])
function toggleChohyo(value: OrderClass): void {
  const set = new Set(selectedChohyo.value)
  if (set.has(value)) set.delete(value)
  else set.add(value)
  selectedChohyo.value = [...set]
}

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

/** 帳票区分バッジの配色（帳票区分タブと同じ体系）。 */
function chohyoBadgeClass(cls: OrderClass): string {
  if (cls === 'sales') return 'bg-indigo-100 text-indigo-700'
  if (cls === 'info') return 'bg-amber-100 text-amber-700'
  return 'bg-slate-100 text-slate-500'
}

const allRows = computed<WarehouseRow[]>(() =>
  buildWarehouseMock(targetBusinessTypes.value, targetDepartments.value),
)

const rows = computed<WarehouseRow[]>(() => {
  const kigo = appliedKigou.value.trim()
  const chohyo = selectedChohyo.value
  return allRows.value.filter(
    (r) =>
      (!kigo || r.shohinKigou.includes(kigo))
      // 空選択（両方オフ）は全表示扱いにして行が消える手詰まりを避ける。
      && (chohyo.length === 0 || chohyo.includes(r.chohyoKubun)),
  )
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
      数量①〜⑬は売上参照・WMS・算出の各ソースに対応します。業態・部門は上部フィルターを引き継ぎ、
      商品記号は下記の部分一致で絞り込みます。
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
      <div class="flex items-end gap-2">
        <div>
          <span class="mb-1 block text-xs font-medium text-slate-500">帳票区分</span>
          <div class="flex items-center gap-3">
            <label
              v-for="opt in CHOHYO_OPTIONS"
              :key="opt.value"
              class="inline-flex cursor-pointer items-center gap-1.5 text-xs text-slate-600"
            >
              <input
                type="checkbox"
                class="accent-indigo-600"
                :checked="selectedChohyo.includes(opt.value)"
                @change="toggleChohyo(opt.value)"
              >
              {{ opt.label }}
            </label>
          </div>
        </div>
      </div>
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
            <th colspan="1" class="border-b border-l border-slate-200 bg-rose-50 px-2 py-1.5 text-center font-semibold text-rose-700">帳票区分</th>
            <th colspan="5" class="border-b border-l border-slate-200 bg-emerald-50 px-2 py-1.5 text-center font-semibold text-emerald-700">売上参照</th>
            <th colspan="1" class="border-b border-l border-slate-200 bg-violet-50 px-2 py-1.5 text-center font-semibold text-violet-700">算出</th>
            <th colspan="2" class="border-b border-l border-slate-200 bg-amber-50 px-2 py-1.5 text-center font-semibold text-amber-700">WMS</th>
            <th colspan="5" class="border-b border-l border-slate-200 bg-violet-50 px-2 py-1.5 text-center font-semibold text-violet-700">算出</th>
          </tr>
          <tr class="text-slate-500">
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">業態</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">部門</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">商品記号</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">品番</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">単品</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">品名</th>
            <th class="whitespace-nowrap border-l border-slate-200 bg-slate-50 px-2 py-2 text-center font-medium">帳票区分</th>
            <th class="whitespace-nowrap border-l border-slate-200 bg-slate-50 px-2 py-2 text-right font-medium">①先付数</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">②発注数</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">③納品数</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">④売上数</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">⑤店頭在庫</th>
            <th class="whitespace-nowrap border-l border-slate-200 bg-slate-50 px-2 py-2 text-right font-medium">⑥発注済未納品</th>
            <th class="whitespace-nowrap border-l border-slate-200 bg-slate-50 px-2 py-2 text-right font-medium">⑦取置在庫数</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">⑧論理在庫数</th>
            <th class="whitespace-nowrap border-l border-slate-200 bg-slate-50 px-2 py-2 text-right font-medium">⑨出荷可能数(取置)</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">⑩出荷可能数(発注済未納品)</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">⑪累計在庫数</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">⑫先付増減数</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">⑬先付増減率</th>
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
            <td class="whitespace-nowrap border-l border-slate-100 px-2 py-1.5 text-center">
              <span class="inline-block rounded px-1.5 py-0.5 font-medium" :class="chohyoBadgeClass(row.chohyoKubun)">
                {{ ORDER_CLASS_LABEL[row.chohyoKubun] }}
              </span>
            </td>
            <td class="whitespace-nowrap border-l border-slate-100 px-2 py-1.5 text-right tabular-nums text-slate-600">{{ formatNumber(row.sakizukeCount) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums text-slate-600">{{ formatNumber(row.hatchuCount) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums text-slate-600">{{ formatNumber(row.ruikeiNohinCount) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums text-slate-600">{{ formatNumber(row.ruikeiUriageCount) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums text-slate-600">{{ formatNumber(row.zaikosu) }}</td>
            <td class="whitespace-nowrap border-l border-slate-100 px-2 py-1.5 text-right tabular-nums font-medium text-violet-700">{{ formatNumber(row.orderNotDelivered) }}</td>
            <td class="whitespace-nowrap border-l border-slate-100 px-2 py-1.5 text-right tabular-nums text-slate-600">{{ formatNumber(row.reservedStock) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums font-semibold text-amber-700">{{ formatNumber(row.logicalStock) }}</td>
            <td class="whitespace-nowrap border-l border-slate-100 px-2 py-1.5 text-right tabular-nums text-violet-700">{{ formatNumber(row.shippableReserved) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums text-violet-700">{{ formatNumber(row.shippableOrder) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums font-semibold text-violet-700">{{ formatNumber(row.cumulativeStock) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums text-violet-700">{{ formatNumber(row.sakizukeDelta) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums text-violet-700">{{ formatRatioAsPercent(row.sakizukeRate, 0) }}</td>
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
        <div class="mb-2 flex items-center justify-between gap-2">
          <p class="text-xs text-slate-600">{{ row.hinmei }}</p>
          <span class="rounded px-1.5 py-0.5 text-[10px] font-medium" :class="chohyoBadgeClass(row.chohyoKubun)">
            {{ ORDER_CLASS_LABEL[row.chohyoKubun] }}
          </span>
        </div>
        <div class="grid grid-cols-2 gap-x-3 gap-y-1 text-xs">
          <div class="flex justify-between"><span class="text-slate-500">①先付数</span><span class="tabular-nums">{{ formatNumber(row.sakizukeCount) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">②発注数</span><span class="tabular-nums">{{ formatNumber(row.hatchuCount) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">③納品数</span><span class="tabular-nums">{{ formatNumber(row.ruikeiNohinCount) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">④売上数</span><span class="tabular-nums">{{ formatNumber(row.ruikeiUriageCount) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">⑤店頭在庫</span><span class="tabular-nums">{{ formatNumber(row.zaikosu) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">⑥発注済未納品</span><span class="tabular-nums font-medium text-violet-700">{{ formatNumber(row.orderNotDelivered) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">⑦取置在庫数</span><span class="tabular-nums">{{ formatNumber(row.reservedStock) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">⑧論理在庫数</span><span class="tabular-nums font-semibold text-amber-700">{{ formatNumber(row.logicalStock) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">⑨出荷可能(取置)</span><span class="tabular-nums text-violet-700">{{ formatNumber(row.shippableReserved) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">⑩出荷可能(未納品)</span><span class="tabular-nums text-violet-700">{{ formatNumber(row.shippableOrder) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">⑪累計在庫数</span><span class="tabular-nums font-semibold text-violet-700">{{ formatNumber(row.cumulativeStock) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">⑫先付増減数</span><span class="tabular-nums text-violet-700">{{ formatNumber(row.sakizukeDelta) }}</span></div>
          <div class="flex justify-between"><span class="text-slate-500">⑬先付増減率</span><span class="tabular-nums text-violet-700">{{ formatRatioAsPercent(row.sakizukeRate, 0) }}</span></div>
        </div>
      </div>
    </div>
  </div>
</template>
