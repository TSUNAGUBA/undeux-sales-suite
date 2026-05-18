<script setup lang="ts">
import { Search, RotateCcw } from 'lucide-vue-next'
import type { CodeName } from '~/types/api'

const emit = defineEmits<{ apply: [] }>()

const { filter, options, optionsError, loadOptions, reset } = useFilters()

onMounted(loadOptions)

function toSelectOptions(items: CodeName[]): { value: string; text: string }[] {
  return items.map((item) => ({
    value: item.code,
    text: item.name ? `${item.code}: ${item.name}` : item.code,
  }))
}

const departmentOptions = computed(() => toSelectOptions(options.value?.departments ?? []))
const customerOptions = computed(() => toSelectOptions(options.value?.customers ?? []))
const businessTypeOptions = computed(() => toSelectOptions(options.value?.businessTypes ?? []))
const seasonOptions = computed(() => toSelectOptions(options.value?.seasons ?? []))
const weeks = computed(() => options.value?.weeks ?? [])

function applyFilter(): void {
  emit('apply')
}

function resetFilter(): void {
  reset()
  emit('apply')
}
</script>

<template>
  <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
    <p
      v-if="optionsError"
      class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700"
    >
      フィルタ選択肢の取得に失敗しました: {{ optionsError }}
    </p>

    <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
      <div>
        <label class="mb-1 block text-xs font-medium text-slate-500">
          開始週（取込日）
        </label>
        <select
          v-model="filter.from"
          class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
        >
          <option :value="null">最古から</option>
          <option v-for="week in weeks" :key="week" :value="week">{{ week }}</option>
        </select>
      </div>

      <div>
        <label class="mb-1 block text-xs font-medium text-slate-500">
          終了週（取込日）
        </label>
        <select
          v-model="filter.to"
          class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
        >
          <option :value="null">最新まで</option>
          <option v-for="week in weeks" :key="week" :value="week">{{ week }}</option>
        </select>
      </div>

      <MultiSelect
        v-model="filter.departments"
        label="部門"
        :options="departmentOptions"
      />
      <MultiSelect
        v-model="filter.customers"
        label="取引先"
        :options="customerOptions"
      />
      <MultiSelect
        v-model="filter.businessTypes"
        label="業態"
        :options="businessTypeOptions"
      />
      <MultiSelect
        v-model="filter.seasons"
        label="季節区分"
        :options="seasonOptions"
      />
    </div>

    <div class="mt-4 flex flex-wrap gap-2">
      <button
        type="button"
        class="flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700"
        @click="applyFilter"
      >
        <Search class="h-4 w-4" />
        適用
      </button>
      <button
        type="button"
        class="flex items-center gap-1.5 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
        @click="resetFilter"
      >
        <RotateCcw class="h-4 w-4" />
        リセット
      </button>
    </div>
  </div>
</template>
