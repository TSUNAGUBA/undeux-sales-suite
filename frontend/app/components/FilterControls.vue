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

const { filter, options, optionsError, loadOptions, years, businessTypeChipOptions } = useFilters(props.scopeKey)

// 棚割1・平均在庫日数（在日）は sales 系・mart 系の両方が解釈する
// （mart は dim_product.attributes / fact_inventory_snapshot.stock_days に適用）。
// そのため全スコープで同じコントロールを表示する。

onMounted(loadOptions)

function toSelectOptions(items: CodeName[]): { value: string; text: string }[] {
  return items.map((item) => ({
    value: item.code,
    text: item.name ? `${item.code}: ${item.name}` : item.code,
  }))
}

const departmentOptions = computed(() => toSelectOptions(options.value?.departments ?? []))
const seasonOptions = computed(() => toSelectOptions(options.value?.seasons ?? []))
const tanawari1Options = computed(() =>
  (options.value?.tanawari1 ?? []).map((t) => ({ value: t, text: t })),
)
const stockDaysOptions = STOCK_DAYS_BUCKETS.map((b) => ({ value: b.value, text: b.label }))

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

    <!-- 並び順は 業態 → 部門 → 年度 → 季節（→ 棚割1 → 平均在庫日数）。 -->
    <!-- 業態はタグ（チップ・複数選択）。少数の選択肢をワンタップで切り替えられるようにする。 -->
    <div class="mb-3">
      <p class="mb-1 text-xs font-medium text-slate-500">業態</p>
      <CrossTabMultiSelectChips
        :options="businessTypeChipOptions"
        :model-value="filter.businessTypes"
        empty-label="業態候補がありません"
        @update:model-value="(v: string[]) => (filter.businessTypes = v)"
      />
    </div>

    <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
      <MultiSelect
        v-model="filter.departments"
        label="部門"
        :options="departmentOptions"
      />
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
        v-model="filter.seasons"
        label="季節区分"
        :options="seasonOptions"
      />
      <MultiSelect
        v-model="filter.tanawari1"
        label="棚割1"
        :options="tanawari1Options"
      />
      <MultiSelect
        v-model="filter.stockDaysBuckets"
        label="平均在庫日数（在日）"
        :options="stockDaysOptions"
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
