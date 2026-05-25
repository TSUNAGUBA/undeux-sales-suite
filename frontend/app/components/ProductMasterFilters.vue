<script setup lang="ts">
import { Search, RotateCcw, X } from 'lucide-vue-next'
import type { MasterFilterOptions, ProductMasterFilterState } from '~/types/api'

const props = defineProps<{
  modelValue: ProductMasterFilterState
  options: MasterFilterOptions | null
  optionsError: string | null
}>()

const emit = defineEmits<{
  'update:modelValue': [ProductMasterFilterState]
  apply: []
  reset: []
}>()

// 内部編集用の作業コピー（適用時に親へ反映）。
const draft = ref<ProductMasterFilterState>(cloneFilter(props.modelValue))
watch(
  () => props.modelValue,
  (next) => {
    draft.value = cloneFilter(next)
  },
  { deep: true },
)

// MultiSelect は string[] を取るため、divisionCds (number[]) を string[] にブリッジする。
const divisionCdsAsStrings = computed<string[]>({
  get: () => draft.value.divisionCds.map((d) => String(d)),
  set: (next) => {
    draft.value.divisionCds = next
      .map((s) => Number.parseInt(s, 10))
      .filter((n) => Number.isFinite(n))
  },
})

const businessTypeOptions = computed(() =>
  (props.options?.businessTypes ?? []).map((b) => ({
    value: b.code,
    text: b.name
      ? b.shortName
        ? `${b.code}: ${b.name} (${b.shortName})`
        : `${b.code}: ${b.name}`
      : b.code,
  })),
)

const divisionOptions = computed(() =>
  (props.options?.divisions ?? []).map((d) => ({
    value: d.code,
    text: d.name ? `${d.code}: ${d.name}` : d.code,
  })),
)

const brandOptions = computed(() =>
  (props.options?.brands ?? []).map((b) => ({ value: b, text: b })),
)

const managerOptions = computed(() =>
  (props.options?.managers ?? []).map((m) => ({ value: m, text: m })),
)

const activeChips = computed(() => {
  const chips: { label: string; remove: () => void }[] = []
  if (draft.value.search) {
    chips.push({
      label: `検索: "${draft.value.search}"`,
      remove: () => {
        draft.value.search = ''
      },
    })
  }
  for (const code of draft.value.businessCategoryCds) {
    const opt = businessTypeOptions.value.find((o) => o.value === code)
    chips.push({
      label: `業態: ${opt?.text ?? code}`,
      remove: () => {
        draft.value.businessCategoryCds = draft.value.businessCategoryCds.filter(
          (c) => c !== code,
        )
      },
    })
  }
  for (const code of draft.value.divisionCds) {
    const opt = divisionOptions.value.find((o) => o.value === String(code))
    chips.push({
      label: `部門: ${opt?.text ?? code}`,
      remove: () => {
        draft.value.divisionCds = draft.value.divisionCds.filter((c) => c !== code)
      },
    })
  }
  for (const brand of draft.value.brands) {
    chips.push({
      label: `ブランド: ${brand}`,
      remove: () => {
        draft.value.brands = draft.value.brands.filter((b) => b !== brand)
      },
    })
  }
  for (const manager of draft.value.managers) {
    chips.push({
      label: `担当: ${manager}`,
      remove: () => {
        draft.value.managers = draft.value.managers.filter((m) => m !== manager)
      },
    })
  }
  return chips
})

function apply(): void {
  emit('update:modelValue', cloneFilter(draft.value))
  emit('apply')
}

function reset(): void {
  draft.value = emptyFilter()
  emit('update:modelValue', cloneFilter(draft.value))
  emit('reset')
}

function emptyFilter(): ProductMasterFilterState {
  return {
    search: '',
    businessCategoryCds: [],
    divisionCds: [],
    brands: [],
    managers: [],
  }
}

function cloneFilter(value: ProductMasterFilterState): ProductMasterFilterState {
  return {
    search: value.search,
    businessCategoryCds: [...value.businessCategoryCds],
    divisionCds: [...value.divisionCds],
    brands: [...value.brands],
    managers: [...value.managers],
  }
}
</script>

<template>
  <CollapsiblePanel title="検索フィルター">
    <p
      v-if="optionsError"
      class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700"
    >
      フィルタ選択肢の取得に失敗しました: {{ optionsError }}
    </p>

    <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
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

      <MultiSelect
        v-model="draft.businessCategoryCds"
        label="業態"
        :options="businessTypeOptions"
      />
      <MultiSelect
        v-model="divisionCdsAsStrings"
        label="部門"
        :options="divisionOptions"
      />
      <MultiSelect
        v-model="draft.brands"
        label="ブランド"
        :options="brandOptions"
      />
      <MultiSelect
        v-model="draft.managers"
        label="担当者"
        :options="managerOptions"
      />
    </div>

    <div v-if="activeChips.length > 0" class="mt-3 flex flex-wrap gap-1.5">
      <span
        v-for="(chip, i) in activeChips"
        :key="i"
        class="inline-flex items-center gap-1 rounded-full bg-indigo-50 px-2.5 py-1 text-xs font-medium text-indigo-700"
      >
        {{ chip.label }}
        <button
          type="button"
          class="text-indigo-500 hover:text-indigo-900"
          aria-label="この条件を解除"
          @click="chip.remove"
        >
          <X class="h-3 w-3" />
        </button>
      </span>
    </div>

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
        @click="reset"
      >
        <RotateCcw class="h-4 w-4" />
        リセット
      </button>
    </div>
  </CollapsiblePanel>
</template>
