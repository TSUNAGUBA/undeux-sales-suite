<script setup lang="ts">
import { X } from 'lucide-vue-next'
import type { CodeName } from '~/types/api'

const props = withDefaults(
  defineProps<{
    /** useFilters のスコープキー。既定は全社共通の 'sales-filter'。 */
    scopeKey?: string
  }>(),
  { scopeKey: 'sales-filter' },
)

const { filter, options, optionsError, loadOptions, years } = useFilters(props.scopeKey)

onMounted(loadOptions)

function toSelectOptions(items: CodeName[]): { value: string; text: string }[] {
  return items.map((item) => ({
    value: item.code,
    text: item.name ? `${item.code}: ${item.name}` : item.code,
  }))
}

const departmentOptions = computed(() => toSelectOptions(options.value?.departments ?? []))
const customerOptions = computed(() => toSelectOptions(options.value?.customers ?? []))
const businessTypeOptions = computed(() =>
  (options.value?.businessTypes ?? []).map((b) => ({
    value: b.code,
    text: b.name
      ? b.shortName
        ? `${b.code}: ${b.name} (${b.shortName})`
        : `${b.code}: ${b.name}`
      : b.code,
  })),
)
const seasonOptions = computed(() => toSelectOptions(options.value?.seasons ?? []))

function removeHinban(value: string): void {
  filter.value.hinbans = filter.value.hinbans.filter((h) => h !== value)
}
</script>

<template>
  <div>
    <p
      v-if="optionsError"
      class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700"
    >
      フィルタ選択肢の取得に失敗しました: {{ optionsError }}
    </p>

    <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
      <div>
        <label class="mb-1 block text-xs font-medium text-slate-500">
          年度（1月〜12月）
        </label>
        <select
          v-model="filter.year"
          class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
        >
          <option :value="null">全期間</option>
          <option v-for="y in years" :key="y" :value="y">{{ y }}年</option>
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

    <div v-if="filter.hinbans.length > 0" class="mt-3">
      <p class="mb-1 text-xs font-medium text-slate-500">品番フィルター（ドリルダウン適用中）</p>
      <div class="flex flex-wrap gap-1.5">
        <span
          v-for="hinban in filter.hinbans"
          :key="hinban"
          class="inline-flex items-center gap-1 rounded-full bg-indigo-50 px-2.5 py-1 text-xs font-medium text-indigo-700"
        >
          {{ hinban }}
          <button
            type="button"
            class="text-indigo-500 hover:text-indigo-900"
            :aria-label="`品番 ${hinban} を解除`"
            @click="removeHinban(hinban)"
          >
            <X class="h-3 w-3" />
          </button>
        </span>
      </div>
    </div>
  </div>
</template>
