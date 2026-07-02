<script setup lang="ts">
/**
 * 発注区分（売発注／情報発注）タブ。
 *
 * SKU ごとに「前週の区分」「当週の区分」「変化の有無」を表示し、週次の変化履歴をモーダルで確認できる。
 * スタースキーマに発注区分が無いため、SKU の週次区分をモック生成する（実データ接続時は
 * public.sales-weekly を発注区分つきでスタースキーマに取り込み置換する）。
 * 業態・部門はページ上部フィルターを引き継ぎ、商品記号は本タブの部分一致入力で絞り込む。
 */
import { History, RotateCcw, Search, X } from 'lucide-vue-next'
import type { BusinessTypeOption, CodeName } from '~/types/api'
import type { OrderClass, OrderClassRow } from '~/utils/inventoryMock'

const props = defineProps<{
  businessTypeOptions: BusinessTypeOption[]
  departmentOptions: CodeName[]
  selectedBusinessTypes: string[]
  selectedDepartments: string[]
  /** 直近の週（履歴生成に使う。昇順）。 */
  recentWeeks: string[]
}>()

const shohinKigou = ref('')
const appliedKigou = ref('')
const changedOnly = ref(false)

const targetBusinessTypes = computed(() =>
  props.selectedBusinessTypes.length > 0
    ? props.selectedBusinessTypes
    : props.businessTypeOptions.map((b) => b.code),
)
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

const allRows = computed<OrderClassRow[]>(() =>
  buildOrderClassMock(targetBusinessTypes.value, targetDepartments.value, props.recentWeeks),
)

const rows = computed<OrderClassRow[]>(() => {
  const kigo = appliedKigou.value.trim()
  return allRows.value.filter(
    (r) => (!kigo || r.shohinKigou.includes(kigo)) && (!changedOnly.value || r.changed),
  )
})

const changedCount = computed(() => allRows.value.filter((r) => r.changed).length)

function applyKigou(): void {
  appliedKigou.value = shohinKigou.value
}
function resetKigou(): void {
  shohinKigou.value = ''
  appliedKigou.value = ''
}

/** 区分バッジの配色。 */
function classBadge(cls: OrderClass | null): string {
  if (cls === 'sales') return 'bg-indigo-100 text-indigo-700'
  if (cls === 'info') return 'bg-amber-100 text-amber-700'
  return 'bg-slate-100 text-slate-500'
}
function classLabel(cls: OrderClass | null): string {
  return cls ? ORDER_CLASS_LABEL[cls] : '—'
}

// ---- 履歴モーダル ----
const historyRow = ref<OrderClassRow | null>(null)
function openHistory(row: OrderClassRow): void {
  historyRow.value = row
}
function closeHistory(): void {
  historyRow.value = null
}

// Escape キーでモーダルを閉じる（開いている間だけ window リスナを張る）。
function onModalKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') closeHistory()
}
watch(historyRow, (row) => {
  if (import.meta.server) return
  if (row) window.addEventListener('keydown', onModalKeydown)
  else window.removeEventListener('keydown', onModalKeydown)
})
onBeforeUnmount(() => {
  if (import.meta.server) return
  window.removeEventListener('keydown', onModalKeydown)
})
/** 履歴の各点が前週から変化したか（強調用）。 */
function isChangePoint(row: OrderClassRow, index: number): boolean {
  return index > 0 && row.history[index]!.orderClass !== row.history[index - 1]!.orderClass
}
</script>

<template>
  <div class="space-y-3">
    <p class="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700">
      発注区分（売発注／情報発注）はスタースキーマに無いため、SKU 週次の区分をモックで表現しています。
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
      <label class="ml-1 inline-flex cursor-pointer items-center gap-1.5 text-xs text-slate-600">
        <input v-model="changedOnly" type="checkbox" class="accent-indigo-600">
        変化があったSKUのみ
      </label>
      <span class="ml-auto text-xs text-slate-400">
        当週変化 {{ formatNumber(changedCount) }} 件 / {{ formatNumber(allRows.length) }} 件
      </span>
    </div>

    <div
      v-if="recentWeeks.length < 2"
      class="rounded-xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-400"
    >
      週次データが不足しているため、発注区分の変化を表示できません。
    </div>
    <div
      v-else-if="rows.length === 0"
      class="rounded-xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-400"
    >
      該当するSKUがありません。フィルターを見直してください。
    </div>

    <!-- PC: 表 -->
    <div v-else class="hidden overflow-x-auto rounded-xl border border-slate-200 bg-white md:block">
      <table class="w-full text-xs">
        <thead class="text-slate-500">
          <tr>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">業態</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">部門</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">商品記号</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">品番/単品</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium">品名</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-center font-medium">前週</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-center font-medium">当週</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-center font-medium">変化</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">変更回数</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-center font-medium">履歴</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-slate-100">
          <tr v-for="row in rows" :key="row.key" class="hover:bg-slate-50">
            <td class="whitespace-nowrap px-2 py-1.5 text-slate-600">{{ businessTypeName(row.businessTypeCode) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-slate-600">{{ departmentName(row.departmentCode) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 font-mono text-slate-600">{{ row.shohinKigou }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 font-mono text-slate-600">{{ row.hinbanCode }}-{{ row.tanpinCode }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-slate-700">{{ row.hinmei }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-center">
              <span class="inline-block rounded px-1.5 py-0.5 font-medium" :class="classBadge(row.previous)">{{ classLabel(row.previous) }}</span>
            </td>
            <td class="whitespace-nowrap px-2 py-1.5 text-center">
              <span class="inline-block rounded px-1.5 py-0.5 font-medium" :class="classBadge(row.current)">{{ classLabel(row.current) }}</span>
            </td>
            <td class="whitespace-nowrap px-2 py-1.5 text-center">
              <span
                v-if="row.changed"
                class="inline-block rounded bg-rose-50 px-1.5 py-0.5 font-semibold text-rose-700"
              >変更</span>
              <span v-else class="text-slate-400">変化なし</span>
            </td>
            <td class="whitespace-nowrap px-2 py-1.5 text-right tabular-nums text-slate-500">{{ formatNumber(row.changeCount) }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-center">
              <button
                type="button"
                class="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 text-slate-600 hover:bg-slate-50"
                @click="openHistory(row)"
              >
                <History class="h-3.5 w-3.5" />
                履歴
              </button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- モバイル: カード -->
    <div v-if="rows.length > 0 && recentWeeks.length >= 2" class="space-y-2 md:hidden">
      <div v-for="row in rows" :key="row.key" class="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
        <div class="mb-2 flex items-center justify-between gap-2">
          <span class="font-mono text-sm font-semibold text-slate-700">{{ row.shohinKigou }} / {{ row.hinbanCode }}-{{ row.tanpinCode }}</span>
          <button
            type="button"
            class="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 text-xs text-slate-600 hover:bg-slate-50"
            @click="openHistory(row)"
          >
            <History class="h-3.5 w-3.5" />履歴
          </button>
        </div>
        <div class="flex items-center gap-2 text-xs">
          <span class="rounded px-1.5 py-0.5 font-medium" :class="classBadge(row.previous)">{{ classLabel(row.previous) }}</span>
          <span class="text-slate-400">→</span>
          <span class="rounded px-1.5 py-0.5 font-medium" :class="classBadge(row.current)">{{ classLabel(row.current) }}</span>
          <span v-if="row.changed" class="ml-1 rounded bg-rose-50 px-1.5 py-0.5 font-semibold text-rose-700">変更</span>
          <span v-else class="ml-1 text-slate-400">変化なし</span>
        </div>
      </div>
    </div>

    <!-- 履歴モーダル -->
    <div
      v-if="historyRow"
      class="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/40 p-4"
      role="dialog"
      aria-modal="true"
      @click.self="closeHistory"
    >
      <div class="w-full max-w-lg rounded-xl border border-slate-200 bg-white shadow-xl">
        <div class="flex items-center justify-between gap-2 border-b border-slate-200 px-4 py-3">
          <div>
            <h3 class="text-sm font-bold text-slate-800">発注区分の変化履歴</h3>
            <p class="text-xs text-slate-500">
              {{ historyRow.shohinKigou }} / {{ historyRow.hinbanCode }}-{{ historyRow.tanpinCode }}・{{ historyRow.hinmei }}
            </p>
          </div>
          <button type="button" class="rounded-lg p-1 text-slate-400 hover:bg-slate-100" aria-label="閉じる" @click="closeHistory">
            <X class="h-4 w-4" />
          </button>
        </div>
        <div class="max-h-96 overflow-auto px-4 py-3">
          <ol class="space-y-1.5">
            <li
              v-for="(pt, i) in historyRow.history"
              :key="pt.week"
              class="flex items-center justify-between gap-2 rounded-lg px-2 py-1.5"
              :class="isChangePoint(historyRow, i) ? 'bg-rose-50' : 'bg-slate-50'"
            >
              <span class="font-mono text-xs text-slate-500">{{ pt.week }}</span>
              <span class="flex items-center gap-1.5">
                <span class="rounded px-1.5 py-0.5 text-xs font-medium" :class="classBadge(pt.orderClass)">{{ classLabel(pt.orderClass) }}</span>
                <span v-if="isChangePoint(historyRow, i)" class="rounded bg-rose-100 px-1.5 py-0.5 text-[10px] font-semibold text-rose-700">変更</span>
              </span>
            </li>
          </ol>
        </div>
      </div>
    </div>
  </div>
</template>
