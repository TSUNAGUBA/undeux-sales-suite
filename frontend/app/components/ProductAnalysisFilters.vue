<script setup lang="ts">
/**
 * 商品別分析（/mart/products）の検索フィルター。
 *
 * 「全社サマリー」のフィルター（業態・部門・年度・季節・棚割1・平均在庫日数）を FilterControls で
 * そのまま踏襲し、末尾に「ブランド」「担当者」「キーワード」を追加する。
 * 全社サマリー踏襲分は useFilters(scopeKey)（SalesFilterState）へ書き込み、ブランド・担当者・
 * キーワードは v-model（ProductExtraFilterState）で親へ反映する。
 *
 * 商品マスタ（/product-master）の ProductMasterFilters とは別系統（あちらは商品マスタ属性のみ）。
 */
import { RotateCcw, Search } from 'lucide-vue-next'
import type { MasterFilterOptions } from '~/types/api'

/** 全社サマリー踏襲フィルターの末尾に付与する追加フィルター。 */
export interface ProductExtraFilterState {
  brands: string[]
  managers: string[]
  search: string
}

const props = defineProps<{
  /** useFilters のスコープキー（全社サマリー踏襲フィルター用）。 */
  scopeKey: string
  /** ブランド・担当者の選択肢（/api/product-master/options）。 */
  options: MasterFilterOptions | null
  optionsError: string | null
  /** ブランド・担当者・キーワード（v-model）。 */
  modelValue: ProductExtraFilterState
}>()

const emit = defineEmits<{
  'update:modelValue': [ProductExtraFilterState]
  apply: []
  reset: []
}>()

// 全社サマリー踏襲フィルター（業態・部門・年度・季節・棚割1・平均在庫日数）は FilterControls が
// useFilters(scopeKey) へ直接書き込む。リセット時はそちらも初期化する。
const { reset } = useFilters(props.scopeKey)

function emptyExtra(): ProductExtraFilterState {
  return { brands: [], managers: [], search: '' }
}
function cloneExtra(value: ProductExtraFilterState): ProductExtraFilterState {
  return { brands: [...value.brands], managers: [...value.managers], search: value.search }
}

// 内部編集用の作業コピー（適用時に親へ反映。ProductMasterFilters と同じ方式）。
const draft = ref<ProductExtraFilterState>(cloneExtra(props.modelValue))
watch(
  () => props.modelValue,
  (next) => {
    draft.value = cloneExtra(next)
  },
  { deep: true },
)

const brandOptions = computed(() => (props.options?.brands ?? []).map((b) => ({ value: b, text: b })))
const managerOptions = computed(() => (props.options?.managers ?? []).map((m) => ({ value: m, text: m })))

function apply(): void {
  emit('update:modelValue', cloneExtra(draft.value))
  emit('apply')
}

function resetAll(): void {
  reset()
  draft.value = emptyExtra()
  emit('update:modelValue', cloneExtra(draft.value))
  emit('reset')
}
</script>

<template>
  <CollapsiblePanel title="フィルター" :default-open="false">
    <!-- 全社サマリー踏襲フィルター（業態・部門・年度・季節・棚割1・平均在庫日数） -->
    <FilterControls :scope-key="scopeKey" />

    <!-- 末尾: ブランド → 担当者 → キーワード -->
    <div class="mt-3 border-t border-dashed border-slate-200 pt-3">
      <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <MultiSelect v-model="draft.brands" label="ブランド" :options="brandOptions" />
        <MultiSelect v-model="draft.managers" label="担当者" :options="managerOptions" />
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">
            キーワード（商品名・記号・品番・ブランド）
          </label>
          <input
            v-model="draft.search"
            type="search"
            placeholder="例: パーカー / 100 / S100"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700 focus:border-indigo-400 focus:outline-none"
            @keydown.enter.prevent="apply"
          >
        </div>
      </div>
    </div>

    <p v-if="optionsError" class="mt-3 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700">
      ブランド・担当者の選択肢の取得に失敗しました: {{ optionsError }}
    </p>

    <div class="mt-4 flex flex-wrap gap-2">
      <button
        type="button"
        class="flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700"
        @click="apply"
      >
        <Search class="h-4 w-4" />
        適用
      </button>
      <button
        type="button"
        class="flex items-center gap-1.5 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
        @click="resetAll"
      >
        <RotateCcw class="h-4 w-4" />
        リセット
      </button>
    </div>
  </CollapsiblePanel>
</template>
