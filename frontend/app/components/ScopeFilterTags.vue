<script setup lang="ts">
/**
 * 業態／部門の絞り込みタグ（全社サマリーのタブ構成を標準化した共通コンポーネント）。
 *
 * 要件 #11: 業態/部門のフィルターは全社サマリーの構成を標準として共通実装する。本コンポーネントは
 * 「すべて」＋各タグ（業態は `{コード}: {名称}({略称})`、部門は `{コード}: {名称}`）で構成し、
 * 単一選択（全社サマリー等）と複数選択（分析ページ）を `multiple` プロップで切り替える。
 *
 * 状態は持たず、選択配列を受け取り変更を emit する（SoT は呼び出し側の共有フィルタ）。
 */
import type { BusinessTypeOption, CodeName } from '~/types/api'

const props = withDefaults(
  defineProps<{
    businessTypes: BusinessTypeOption[]
    departments: CodeName[]
    /** 選択中の業態コード（単一選択でも配列で受け渡す。空＝すべて）。 */
    selectedBusinessTypes: string[]
    /** 選択中の部門コード（空＝すべて）。 */
    selectedDepartments: string[]
    /** true で複数選択、false（既定）で単一選択。 */
    multiple?: boolean
  }>(),
  { multiple: false },
)

const emit = defineEmits<{
  'update:selectedBusinessTypes': [string[]]
  'update:selectedDepartments': [string[]]
}>()

/** 業態タグの表記: `{コード}: {名称} ({略称})`（例: 01: しまむら (sm)）。 */
function businessTypeLabel(bt: BusinessTypeOption): string {
  if (!bt.name) return bt.code
  return bt.shortName ? `${bt.code}: ${bt.name} (${bt.shortName})` : `${bt.code}: ${bt.name}`
}

/** 部門タグの表記: `{コード}: {名称}`。 */
function departmentLabel(d: CodeName): string {
  return d.name ? `${d.code}: ${d.name}` : d.code
}

/** 単一/複数に応じてトグルした次の選択配列を返す。 */
function nextSelection(list: string[], code: string): string[] {
  if (props.multiple) {
    return list.includes(code) ? list.filter((c) => c !== code) : [...list, code]
  }
  // 単一選択: 既選択の単独タグは維持（解除は「すべて」で行う）、それ以外は置換。
  return list.length === 1 && list[0] === code ? list : [code]
}

function selectBusinessType(code: string | null): void {
  emit('update:selectedBusinessTypes', code === null ? [] : nextSelection(props.selectedBusinessTypes, code))
}
function selectDepartment(code: string | null): void {
  emit('update:selectedDepartments', code === null ? [] : nextSelection(props.selectedDepartments, code))
}

const allBusinessTypesActive = computed(() => props.selectedBusinessTypes.length === 0)
const allDepartmentsActive = computed(() => props.selectedDepartments.length === 0)

const activeClass = 'border-indigo-500 bg-indigo-50 text-indigo-700'
const inactiveClass = 'border-slate-200 bg-white text-slate-600 hover:border-indigo-300'
</script>

<template>
  <div class="space-y-2">
    <!-- 業態 -->
    <div class="flex flex-wrap items-center gap-2">
      <span class="w-10 shrink-0 text-xs font-medium text-slate-400">業態</span>
      <button
        type="button"
        class="inline-flex items-center rounded-full border px-3 py-1.5 text-xs font-medium transition-colors"
        :class="allBusinessTypesActive ? activeClass : inactiveClass"
        :aria-pressed="allBusinessTypesActive"
        @click="selectBusinessType(null)"
      >
        すべて
      </button>
      <button
        v-for="bt in businessTypes"
        :key="bt.code"
        type="button"
        class="inline-flex items-center rounded-full border px-3 py-1.5 text-xs font-medium transition-colors"
        :class="selectedBusinessTypes.includes(bt.code) ? activeClass : inactiveClass"
        :aria-pressed="selectedBusinessTypes.includes(bt.code)"
        @click="selectBusinessType(bt.code)"
      >
        {{ businessTypeLabel(bt) }}
      </button>
    </div>

    <!-- 部門 ＋ 末尾スロット（年度など） -->
    <div class="flex flex-wrap items-center gap-2">
      <span class="w-10 shrink-0 text-xs font-medium text-slate-400">部門</span>
      <button
        type="button"
        class="inline-flex items-center rounded-full border px-3 py-1.5 text-xs font-medium transition-colors"
        :class="allDepartmentsActive ? activeClass : inactiveClass"
        :aria-pressed="allDepartmentsActive"
        @click="selectDepartment(null)"
      >
        すべて
      </button>
      <button
        v-for="dept in departments"
        :key="dept.code"
        type="button"
        class="inline-flex items-center rounded-full border px-3 py-1.5 text-xs font-medium transition-colors"
        :class="selectedDepartments.includes(dept.code) ? activeClass : inactiveClass"
        :aria-pressed="selectedDepartments.includes(dept.code)"
        @click="selectDepartment(dept.code)"
      >
        {{ departmentLabel(dept) }}
      </button>
      <div v-if="$slots.trailing" class="ml-auto flex items-center gap-2">
        <slot name="trailing" />
      </div>
    </div>
  </div>
</template>
