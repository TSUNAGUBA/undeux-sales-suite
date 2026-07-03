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

// ---- 変化種別フィルター（前週→当週の帳票区分変化） ----
/** 変化なしの行を表すフィルターキー。 */
const TRANSITION_NONE = '変化なし'
/** 行の変化種別キー（例: 「売→情」。変化なしは TRANSITION_NONE）。 */
function transitionKey(row: OrderClassRow): string {
  return orderClassTransitionLabel(row.previous, row.current) ?? TRANSITION_NONE
}
/** データ中に存在する変化種別（変化ありを先頭に安定ソート、変化なしを末尾）。 */
const transitionOptions = computed<string[]>(() => {
  const seen = new Set<string>()
  for (const r of allRows.value) seen.add(transitionKey(r))
  const changes = [...seen].filter((k) => k !== TRANSITION_NONE).sort()
  return seen.has(TRANSITION_NONE) ? [...changes, TRANSITION_NONE] : changes
})
// null = 全選択（デフォルト全表示）。1つでも外すと明示的な選択集合に確定する。
const selectedTransitions = ref<string[] | null>(null)
function isTransitionChecked(key: string): boolean {
  return selectedTransitions.value === null || selectedTransitions.value.includes(key)
}
function toggleTransition(key: string): void {
  const base = selectedTransitions.value ?? transitionOptions.value
  const set = new Set(base)
  if (set.has(key)) set.delete(key)
  else set.add(key)
  selectedTransitions.value = [...set]
}
function resetTransitions(): void {
  selectedTransitions.value = null
}

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
  const sel = selectedTransitions.value
  return allRows.value.filter(
    (r) =>
      (!kigo || r.shohinKigou.includes(kigo))
      && (sel === null || sel.includes(transitionKey(r))),
  )
})

const changedCount = computed(() => allRows.value.filter((r) => r.changed).length)

/** 変化列の略記（例: 「売→情」）。変化なしは null。 */
function transitionDisplay(row: OrderClassRow): string | null {
  return orderClassTransitionLabel(row.previous, row.current)
}

function applyKigou(): void {
  appliedKigou.value = shohinKigou.value
}
function resetKigou(): void {
  shohinKigou.value = ''
  appliedKigou.value = ''
}

// 横スクロール時に左固定する識別列（業態〜品名）の幅と左オフセット（px、累積）。
// z-index 規約: 固定ヘッダ z-30 ＞ 固定ボディ z-10 ＞ 通常 0。
const FROZEN = {
  gyotai: { left: 0, width: 72 },
  dept: { left: 72, width: 72 },
  kigo: { left: 144, width: 84 },
  hinban: { left: 228, width: 96 },
  hinmei: { left: 324, width: 120 },
} as const
/** 固定列セルの寸法・位置スタイル。 */
function fz(col: keyof typeof FROZEN): Record<string, string> {
  const c = FROZEN[col]
  return { left: `${c.left}px`, width: `${c.width}px`, minWidth: `${c.width}px`, maxWidth: `${c.width}px` }
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
      帳票区分（売発注／情報発注／無）はスタースキーマに無いため、SKU 週次の区分をモックで表現しています。
      変化列は前週→当週の区分変化を略記（売＝売発注／情＝情報発注／無＝発注なし）で表示します。
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
      <span class="ml-auto text-xs text-slate-400">
        当週変化 {{ formatNumber(changedCount) }} 件 / {{ formatNumber(allRows.length) }} 件
      </span>
    </div>

    <!-- 変化種別フィルター（複数選択・デフォルト全表示）。 -->
    <div v-if="transitionOptions.length > 0" class="flex flex-wrap items-center gap-x-3 gap-y-1.5 rounded-lg bg-slate-50 px-3 py-2">
      <span class="text-xs font-medium text-slate-500">変化種別</span>
      <label
        v-for="key in transitionOptions"
        :key="key"
        class="inline-flex cursor-pointer items-center gap-1.5 text-xs text-slate-600"
      >
        <input
          type="checkbox"
          class="accent-indigo-600"
          :checked="isTransitionChecked(key)"
          @change="toggleTransition(key)"
        >
        {{ key }}
      </label>
      <button
        type="button"
        class="ml-1 rounded-md border border-slate-300 px-2 py-0.5 text-xs text-slate-500 hover:bg-white"
        @click="resetTransitions"
      >
        すべて表示
      </button>
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
            <th class="sticky z-30 whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium" :style="fz('gyotai')">業態</th>
            <th class="sticky z-30 whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium" :style="fz('dept')">部門</th>
            <th class="sticky z-30 whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium" :style="fz('kigo')">商品記号</th>
            <th class="sticky z-30 whitespace-nowrap bg-slate-50 px-2 py-2 text-left font-medium" :style="fz('hinban')">品番/単品</th>
            <th class="sticky z-30 whitespace-nowrap border-r border-slate-200 bg-slate-50 px-2 py-2 text-left font-medium" :style="fz('hinmei')">品名</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-center font-medium">前週</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-center font-medium">当週</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-center font-medium">変化</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-right font-medium">変更回数</th>
            <th class="whitespace-nowrap bg-slate-50 px-2 py-2 text-center font-medium">履歴</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-slate-100">
          <tr v-for="row in rows" :key="row.key" class="hover:bg-slate-50">
            <td class="sticky z-10 truncate bg-white px-2 py-1.5 text-slate-600" :style="fz('gyotai')">{{ businessTypeName(row.businessTypeCode) }}</td>
            <td class="sticky z-10 truncate bg-white px-2 py-1.5 text-slate-600" :style="fz('dept')">{{ departmentName(row.departmentCode) }}</td>
            <td class="sticky z-10 truncate bg-white px-2 py-1.5 font-mono text-slate-600" :style="fz('kigo')">{{ row.shohinKigou }}</td>
            <td class="sticky z-10 truncate bg-white px-2 py-1.5 font-mono text-slate-600" :style="fz('hinban')">{{ row.hinbanCode }}-{{ row.tanpinCode }}</td>
            <td class="sticky z-10 truncate border-r border-slate-200 bg-white px-2 py-1.5 text-slate-700" :style="fz('hinmei')" :title="row.hinmei">{{ row.hinmei }}</td>
            <td class="whitespace-nowrap px-2 py-1.5 text-center">
              <span class="inline-block rounded px-1.5 py-0.5 font-medium" :class="classBadge(row.previous)">{{ classLabel(row.previous) }}</span>
            </td>
            <td class="whitespace-nowrap px-2 py-1.5 text-center">
              <span class="inline-block rounded px-1.5 py-0.5 font-medium" :class="classBadge(row.current)">{{ classLabel(row.current) }}</span>
            </td>
            <td class="whitespace-nowrap px-2 py-1.5 text-center">
              <span
                v-if="transitionDisplay(row)"
                class="inline-block rounded bg-rose-50 px-1.5 py-0.5 font-semibold text-rose-700"
              >{{ transitionDisplay(row) }}</span>
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
          <span v-if="transitionDisplay(row)" class="ml-1 rounded bg-rose-50 px-1.5 py-0.5 font-semibold text-rose-700">{{ transitionDisplay(row) }}</span>
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
            <h3 class="text-sm font-bold text-slate-800">帳票区分の変化履歴</h3>
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
