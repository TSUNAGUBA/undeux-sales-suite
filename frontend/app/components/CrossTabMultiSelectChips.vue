<script setup lang="ts">
/**
 * クロス集計の条件パネルで使うチップ形式のマルチセレクト。
 * - options が searchThreshold 件以上ならフィルタ検索バーを表示する
 * - 「全選択 / 全解除」ショートカット
 * - 選択中チップはアクセント色で強調
 *
 * tokutake-ai-platform の MultiSelectChips.tsx を Vue 3 へポートしたコンポーネント。
 * 既存の MultiSelect.vue（ドロップダウン型）とは UX が異なるため独立コンポーネントとして提供する。
 */

interface ChipOption {
  value: string
  label: string
}

const props = withDefaults(
  defineProps<{
    options: ChipOption[]
    modelValue: string[]
    label?: string
    /** 検索フィールドを表示する閾値（候補がこの数を超えたら表示）。 */
    searchThreshold?: number
    /** 候補が空の時のメッセージ。 */
    emptyLabel?: string
    /** 初期表示で先頭 N 件 + 選択済みのみ表示し、残りを「さらに」で展開する。 */
    collapsible?: boolean
    /** 折り畳み時に最初に見せる件数。既定 12。 */
    collapseHead?: number
  }>(),
  {
    label: undefined,
    searchThreshold: 12,
    emptyLabel: '候補がありません',
    collapsible: true,
    collapseHead: 12,
  },
)

const emit = defineEmits<{ 'update:modelValue': [string[]] }>()

const query = ref('')
const expanded = ref(false)

const selectedSet = computed(() => new Set(props.modelValue))
const showSearch = computed(() => props.options.length >= props.searchThreshold)

const filteredOptions = computed(() => {
  const q = query.value.trim().toLowerCase()
  if (!q) return props.options
  return props.options.filter(
    (o) =>
      o.label.toLowerCase().includes(q) || o.value.toLowerCase().includes(q),
  )
})

// 折り畳み時は先頭 collapseHead 件 + 選択済みのみ表示。
const visibleOptions = computed(() => {
  if (!props.collapsible || expanded.value) {
    return filteredOptions.value
  }
  const head = filteredOptions.value.slice(0, props.collapseHead)
  if (head.length === filteredOptions.value.length) {
    return filteredOptions.value
  }
  const headValues = new Set(head.map((o) => o.value))
  const extraSelected = filteredOptions.value.filter(
    (o) => selectedSet.value.has(o.value) && !headValues.has(o.value),
  )
  return [...head, ...extraSelected]
})

function toggle(value: string): void {
  if (selectedSet.value.has(value)) {
    emit(
      'update:modelValue',
      props.modelValue.filter((v) => v !== value),
    )
  } else {
    emit('update:modelValue', [...props.modelValue, value])
  }
}

function selectAll(): void {
  emit(
    'update:modelValue',
    props.options.map((o) => o.value),
  )
}

function clearAll(): void {
  emit('update:modelValue', [])
}
</script>

<template>
  <div>
    <p v-if="label" class="mb-1 text-xs font-medium text-slate-500">{{ label }}</p>

    <div v-if="options.length === 0" class="text-xs text-slate-400">
      {{ emptyLabel }}
    </div>

    <template v-else>
      <div class="mb-2 flex flex-wrap items-center gap-2">
        <input
          v-if="showSearch"
          v-model="query"
          type="search"
          placeholder="検索..."
          class="rounded-md border border-slate-300 px-2 py-1 text-xs focus:border-indigo-400 focus:outline-none"
          style="width: 10rem"
        >
        <button
          type="button"
          class="text-xs text-slate-500 underline hover:text-slate-700"
          @click="selectAll"
        >
          全選択
        </button>
        <button
          type="button"
          class="text-xs text-slate-500 underline hover:text-slate-700"
          @click="clearAll"
        >
          全解除
        </button>
        <span class="ml-auto text-xs text-slate-400">
          {{ modelValue.length }}/{{ options.length }} 選択
        </span>
      </div>

      <div class="flex flex-wrap gap-1.5">
        <button
          v-for="opt in visibleOptions"
          :key="opt.value"
          type="button"
          class="rounded-full border px-2 py-1 text-xs transition-colors"
          :class="
            selectedSet.has(opt.value)
              ? 'border-indigo-500 bg-indigo-50 text-indigo-700 font-semibold'
              : 'border-slate-300 bg-white text-slate-600 hover:border-slate-400'
          "
          @click="toggle(opt.value)"
        >
          {{ opt.label }}
        </button>
        <button
          v-if="collapsible && !expanded && filteredOptions.length > collapseHead"
          type="button"
          class="px-2 py-1 text-xs text-slate-500 underline hover:text-slate-700"
          @click="expanded = true"
        >
          さらに {{ filteredOptions.length - collapseHead }} 件
        </button>
        <button
          v-if="collapsible && expanded && filteredOptions.length > collapseHead"
          type="button"
          class="px-2 py-1 text-xs text-slate-500 underline hover:text-slate-700"
          @click="expanded = false"
        >
          折りたたむ
        </button>
      </div>
    </template>
  </div>
</template>
