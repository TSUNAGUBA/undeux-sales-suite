<script setup lang="ts">
/**
 * クロス集計（/mart/crosstab）の条件設定パネル。
 *
 * - 折り畳み式のヘッダ。デフォルトは閉（フィルタ・表示条件は既定で折り畳む）。
 * - 行/列ディメンション選択 + 行 ⇄ 列 入替ボタン
 * - 表示メトリクス選択（チェックボックス、availableMetrics 外は disabled）
 * - 表示モード切替（複数メトリクス時のみ表示: セル内縦並び / 列を増やして横並び / 行を増やして横並び）
 * - 一次フィルタ: 業態（タグ・複数選択）・部門・年度・季節区分・棚割1・平均在庫日数（この並び順）
 * - 「詳細」トグルで品番フィルタ（ドリル経由で増える項目）
 * - 適用ボタン・リセットボタン
 *
 * FilterBar（FilterControls）を使う各分析ページと共有 state（useFilters）を共有しつつ、
 * クロス集計向けに集計単位・表示集計値の設定を加えた専用パネルとして構成している。
 *
 * 注: 業態/部門は本パネルではタブ（フィルター/集計単位/表示集計値）内のコンパクトなチップ複数選択
 * （CrossTabMultiSelectChips）を継続使用する。全社サマリー標準の ScopeFilterTags（ページ上部の
 * 単独タグ群）は条件パネルのタブ構成と整合しないため、#11 の標準とは意図的に併存させている。
 */

import { ArrowLeftRight, ChevronDown, Filter, RotateCcw, Search, Thermometer } from 'lucide-vue-next'
import type {
  CrosstabDimensionInfo,
  CrosstabMetricInfo,
  CrosstabMetricKey,
  MetricDisplayMode,
  SalesFilterState,
  TemperatureArea,
} from '~/types/api'

// useFilters の共通ヘルパーから整形済みの選択肢を取得する。
// useFilters は useState で共有状態を持つため、親（mart/crosstab.vue）と
// 同一の state を参照する（=SoT は1つ）。
const {
  departmentChipOptions,
  businessTypeChipOptions,
  seasonChipOptions,
  tanawari1ChipOptions,
} = useFilters()

// 平均在庫日数バケットのチップ選択肢（共通カタログ＝SoT）。
const stockDaysChipOptions = STOCK_DAYS_BUCKETS.map((b) => ({ value: b.value, label: b.label }))

const props = defineProps<{
  /** 利用可能な全ディメンション。 */
  dimensions: CrosstabDimensionInfo[]
  /** 利用可能な全メトリクスのカタログ（フロント側で定義）。 */
  metrics: CrosstabMetricInfo[]
  /** 選択中の行ディメンションキー。 */
  rowDimensionKey: string
  /** 選択中の列ディメンションキー。 */
  columnDimensionKey: string
  /** 選択中のメトリクスキー一覧。 */
  selectedMetrics: CrosstabMetricKey[]
  /** 複数メトリクスの表示モード。 */
  metricDisplayMode: MetricDisplayMode
  /** バックエンドから返ってきた利用可能メトリクスキー（時間軸絡みは在庫系を除外済み）。 */
  availableMetrics: CrosstabMetricKey[]
  /** 全社共通フィルタ state（useFilters の filter）。 */
  filterState: SalesFilterState
  /** 気温メトリクスのエリア種別（null=気温なし）。 */
  temperatureArea: TemperatureArea | null
  /** 行・列のいずれかが時間軸か（気温メトリクスの可否案内に使う）。 */
  hasTimeAxis: boolean
  /** フィルタ選択肢の取得エラー（useFilters の optionsError）。null はエラー無し。 */
  optionsError: string | null
  /** 利用可能な年度の一覧（useFilters.years）。 */
  availableYears: number[]
  /** ローディング中（適用ボタン等を無効化する）。 */
  loading: boolean
}>()

const emit = defineEmits<{
  'update:rowDimensionKey': [string]
  'update:columnDimensionKey': [string]
  'update:selectedMetrics': [CrosstabMetricKey[]]
  'update:metricDisplayMode': [MetricDisplayMode]
  'update:filterState': [SalesFilterState]
  'update:temperatureArea': [TemperatureArea | null]
  swapDimensions: []
  apply: []
  reset: []
  removeHinban: [string]
}>()

// 気温エリア種別セレクト（共通カタログ＝SoT）。
const temperatureAreaOptions = TEMPERATURE_AREAS

function onTemperatureAreaSelect(event: Event): void {
  const value = (event.target as HTMLSelectElement).value
  emit('update:temperatureArea', value === '' ? null : (value as TemperatureArea))
}

// パネルの開閉状態。デフォルトは閉（全ページのフィルタ・表示条件は既定で折り畳む）。
// SSR でも閉で描画されるためハイドレーション不整合が起きない。
const panelOpen = ref(false)

function togglePanel(): void {
  panelOpen.value = !panelOpen.value
}

// 詳細フィルタ（品番タグ群）の表示状態。
const showAdvanced = ref(false)

// 条件設定のセクション切替タブ。縦積みは高さを消費して表を隠すため、
// 「フィルター」「集計単位」「表示する集計値」を1セクションずつ横幅広く表示する。
type ConditionTab = 'filter' | 'dimension' | 'metric'
const conditionTab = ref<ConditionTab>('filter')
const CONDITION_TABS: ReadonlyArray<{ key: ConditionTab; label: string }> = [
  { key: 'filter', label: 'フィルター' },
  { key: 'dimension', label: '集計単位' },
  { key: 'metric', label: '表示する集計値' },
]

// 同じディメンションを行・列双方に置くのは無意味なので、選択中のもう一方を除外。
const rowChoices = computed(() =>
  props.dimensions.filter((d) => d.key !== props.columnDimensionKey),
)
const colChoices = computed(() =>
  props.dimensions.filter((d) => d.key !== props.rowDimensionKey),
)

function setRow(value: string): void {
  emit('update:rowDimensionKey', value)
}
function setCol(value: string): void {
  emit('update:columnDimensionKey', value)
}

function toggleMetric(key: CrosstabMetricKey): void {
  const set = new Set(props.selectedMetrics)
  if (set.has(key)) {
    if (set.size === 1) return // 最低1つは残す
    set.delete(key)
  } else {
    set.add(key)
  }
  emit('update:selectedMetrics', [...set])
}

function setDisplayMode(mode: MetricDisplayMode): void {
  emit('update:metricDisplayMode', mode)
}

function isMetricDisabled(key: CrosstabMetricKey): boolean {
  return !props.availableMetrics.includes(key)
}

// フィルタ state の双方向バインディング（個別フィールド単位で更新を伝播）。
function updateFilter<K extends keyof SalesFilterState>(
  field: K,
  value: SalesFilterState[K],
): void {
  emit('update:filterState', { ...props.filterState, [field]: value })
}

// 部門・業態・季節区分の選択肢は useFilters の共通ヘルパーから取得する。
const departmentOptions = departmentChipOptions
const businessTypeOptions = businessTypeChipOptions
const seasonOptions = seasonChipOptions

// 年は単一選択（全期間=null）。select-element で扱う。
function onYearSelect(event: Event): void {
  const target = event.target as HTMLSelectElement
  const value = target.value
  if (value === '') {
    updateFilter('year', null)
    return
  }
  const parsed = Number.parseInt(value, 10)
  updateFilter('year', Number.isFinite(parsed) ? parsed : null)
}

// アクティブフィルタの数。
const activeFilterCount = computed(() => {
  let n = 0
  if (props.filterState.year !== null) n += 1
  n += props.filterState.departments.length
  n += props.filterState.businessTypes.length
  n += props.filterState.seasons.length
  n += props.filterState.hinbans.length
  n += props.filterState.tanawari1.length
  n += props.filterState.stockDaysBuckets.length
  return n
})

const summaryText = computed(() => {
  const row = props.dimensions.find((d) => d.key === props.rowDimensionKey)?.label ?? '(未設定)'
  const col = props.dimensions.find((d) => d.key === props.columnDimensionKey)?.label ?? '(未設定)'
  return `${row} × ${col} ／ 集計値 ${props.selectedMetrics.length} 個 ／ フィルタ ${activeFilterCount.value} 件`
})
</script>

<template>
  <section
    class="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm"
    aria-label="集計条件パネル"
  >
    <!-- ヘッダ -->
    <div class="flex items-center justify-between gap-3 px-4 py-2.5">
      <div class="flex min-w-0 flex-col">
        <h2 class="text-base font-bold text-slate-800">集計条件</h2>
        <span class="truncate text-xs text-slate-500">{{ summaryText }}</span>
      </div>

      <!-- 折り畳みトグルは狭幅（縦積み）でのみ表示。デスクトップはサイドバーで常時開く。 -->
      <button
        type="button"
        class="inline-flex shrink-0 items-center gap-1.5 rounded-lg border border-slate-300 px-3 py-1.5 text-xs text-slate-700 hover:bg-slate-50 lg:hidden"
        :aria-expanded="panelOpen"
        aria-controls="cross-tab-condition-panel-body"
        @click="togglePanel"
      >
        <Filter class="h-3.5 w-3.5" />
        条件設定
        <span
          v-if="activeFilterCount > 0"
          class="inline-flex items-center justify-center rounded-full bg-indigo-50 px-1.5 text-xs font-semibold text-indigo-700"
          :aria-label="`${activeFilterCount} 件のフィルター適用中`"
          style="min-width: 1.125rem"
        >
          {{ activeFilterCount }}
        </span>
        <ChevronDown
          class="h-3 w-3 text-slate-400 transition-transform"
          :class="panelOpen ? 'rotate-180' : ''"
          aria-hidden="true"
        />
      </button>
    </div>

    <!-- フィルタ選択肢の取得失敗時の通知。FilterControls.vue:45-49 / ProductMasterFilters.vue:147-150 と同じパターン。
         panel 開閉に関わらず常時表示する（ユーザーが気付かないと「フィルタが効かない」状態に陥るため）。 -->
    <p
      v-if="optionsError"
      class="mx-4 mb-2 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700"
    >
      フィルタ選択肢の取得に失敗しました: {{ optionsError }}
    </p>

    <!-- 本体。狭幅は panelOpen で折り畳み、デスクトップ（lg+）はサイドバーとして常時表示する。 -->
    <div
      v-show="panelOpen"
      id="cross-tab-condition-panel-body"
      class="border-t border-slate-100 px-4 pb-4 pt-3 lg:!block"
    >
      <!-- 条件タブ（縦積みでの高さ消費を避け、1セクションずつ横幅広く操作して表を常に見える状態に保つ） -->
      <nav class="mb-3 border-b border-slate-200" aria-label="条件設定の種別切替">
        <ul class="-mb-px flex flex-wrap gap-1">
          <li v-for="tab in CONDITION_TABS" :key="tab.key">
            <button
              type="button"
              class="flex items-center gap-1.5 whitespace-nowrap border-b-2 px-3 py-2 text-sm font-medium transition-colors"
              :class="
                conditionTab === tab.key
                  ? 'border-indigo-600 text-indigo-700'
                  : 'border-transparent text-slate-500 hover:border-slate-300 hover:text-slate-800'
              "
              :aria-current="conditionTab === tab.key ? 'page' : undefined"
              @click="conditionTab = tab.key"
            >
              {{ tab.label }}
              <span
                v-if="tab.key === 'filter' && activeFilterCount > 0"
                class="inline-flex items-center justify-center rounded-full bg-indigo-50 px-1.5 text-xs font-semibold text-indigo-700"
                style="min-width: 1.125rem"
              >{{ activeFilterCount }}</span>
              <span
                v-else-if="tab.key === 'metric'"
                class="inline-flex items-center justify-center rounded-full bg-slate-100 px-1.5 text-xs font-semibold text-slate-600"
                style="min-width: 1.125rem"
              >{{ selectedMetrics.length }}</span>
            </button>
          </li>
        </ul>
      </nav>

      <!-- フィルター（主要 + 詳細） -->
      <div v-show="conditionTab === 'filter'">
        <div class="grid grid-cols-1 gap-x-4">
          <!-- 業態（タグ・複数選択） -->
          <details class="mb-2" open>
            <summary class="cursor-pointer text-sm text-slate-700">
              業態
              <span v-if="filterState.businessTypes.length > 0" class="ml-2 text-xs text-indigo-600">
                ●{{ filterState.businessTypes.length }}
              </span>
            </summary>
            <div class="mt-2">
              <CrossTabMultiSelectChips
                :options="businessTypeOptions"
                :model-value="filterState.businessTypes"
                empty-label="業態候補がありません"
                @update:model-value="(v: string[]) => updateFilter('businessTypes', v)"
              />
            </div>
          </details>

          <!-- 部門 -->
          <details class="mb-2">
            <summary class="cursor-pointer text-sm text-slate-700">
              部門
              <span v-if="filterState.departments.length > 0" class="ml-2 text-xs text-indigo-600">
                ●{{ filterState.departments.length }}
              </span>
            </summary>
            <div class="mt-2">
              <CrossTabMultiSelectChips
                :options="departmentOptions"
                :model-value="filterState.departments"
                empty-label="部門候補がありません"
                @update:model-value="(v: string[]) => updateFilter('departments', v)"
              />
            </div>
          </details>

          <!-- 年度（単一選択） -->
          <details class="mb-2" open>
            <summary class="cursor-pointer text-sm text-slate-700">
              年度
              <span v-if="filterState.year !== null" class="ml-2 text-xs text-indigo-600">
                ●{{ filterState.year }}年
              </span>
            </summary>
            <div class="mt-2">
              <select
                :value="filterState.year === null ? '' : String(filterState.year)"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                @change="onYearSelect"
              >
                <option value="">全期間</option>
                <option v-for="y in availableYears" :key="y" :value="String(y)">
                  {{ y }}年
                </option>
              </select>
            </div>
          </details>

          <!-- 季節区分 -->
          <details class="mb-2">
            <summary class="cursor-pointer text-sm text-slate-700">
              季節区分
              <span v-if="filterState.seasons.length > 0" class="ml-2 text-xs text-indigo-600">
                ●{{ filterState.seasons.length }}
              </span>
            </summary>
            <div class="mt-2">
              <CrossTabMultiSelectChips
                :options="seasonOptions"
                :model-value="filterState.seasons"
                empty-label="季節区分候補がありません"
                @update:model-value="(v: string[]) => updateFilter('seasons', v)"
              />
            </div>
          </details>

          <!-- 棚割1 -->
          <details class="mb-2">
            <summary class="cursor-pointer text-sm text-slate-700">
              棚割1
              <span v-if="filterState.tanawari1.length > 0" class="ml-2 text-xs text-indigo-600">
                ●{{ filterState.tanawari1.length }}
              </span>
            </summary>
            <div class="mt-2">
              <CrossTabMultiSelectChips
                :options="tanawari1ChipOptions"
                :model-value="filterState.tanawari1"
                empty-label="棚割1候補がありません"
                @update:model-value="(v: string[]) => updateFilter('tanawari1', v)"
              />
            </div>
          </details>

          <!-- 平均在庫日数 -->
          <details class="mb-2">
            <summary class="cursor-pointer text-sm text-slate-700">
              平均在庫日数
              <span v-if="filterState.stockDaysBuckets.length > 0" class="ml-2 text-xs text-indigo-600">
                ●{{ filterState.stockDaysBuckets.length }}
              </span>
            </summary>
            <div class="mt-2">
              <CrossTabMultiSelectChips
                :options="stockDaysChipOptions"
                :model-value="filterState.stockDaysBuckets"
                empty-label="候補がありません"
                @update:model-value="(v: string[]) => updateFilter('stockDaysBuckets', v)"
              />
            </div>
          </details>
        </div>

        <!-- 詳細（品番フィルタ等）。品番ドリル経由で増える絞り込み。フィルタータブ全幅の下段に置く。 -->
        <div class="mt-3 border-t border-dashed border-slate-200 pt-3">
            <button
              type="button"
              class="inline-flex w-full items-center justify-between gap-2 rounded-lg border border-slate-300 px-3 py-1.5 text-xs text-slate-700 hover:bg-slate-50"
              :aria-expanded="showAdvanced"
              @click="showAdvanced = !showAdvanced"
            >
              <span class="flex items-center gap-2">
                詳細フィルター
                <span v-if="filterState.hinbans.length > 0" class="text-xs text-indigo-600">
                  ●{{ filterState.hinbans.length }}
                </span>
              </span>
              <ChevronDown
                class="h-3 w-3 transition-transform"
                :class="showAdvanced ? 'rotate-180' : ''"
                aria-hidden="true"
              />
            </button>

            <div v-if="showAdvanced" class="mt-2 space-y-2">
              <div v-if="filterState.hinbans.length > 0">
                <p class="mb-1 text-xs font-medium text-slate-500">品番フィルター</p>
                <div class="flex flex-wrap gap-1.5">
                  <span
                    v-for="h in filterState.hinbans"
                    :key="h"
                    class="inline-flex items-center gap-1 rounded-full bg-indigo-50 px-2.5 py-1 text-xs font-medium text-indigo-700"
                  >
                    {{ h }}
                    <button
                      type="button"
                      class="text-indigo-500 hover:text-indigo-900"
                      :aria-label="`品番 ${h} を解除`"
                      @click="emit('removeHinban', h)"
                    >
                      ×
                    </button>
                  </span>
                </div>
              </div>
              <p v-else class="text-xs text-slate-400">
                品番フィルターは指定されていません。
              </p>
            </div>
          </div>
        </div>
        <!-- 集計単位（行・列） -->
        <div v-show="conditionTab === 'dimension'">
          <p class="mb-2 text-xs text-slate-400">
            変更すると即座に再集計されます。
          </p>
          <div class="grid grid-cols-1 gap-3">
            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">行（縦軸）</label>
              <select
                :value="rowDimensionKey"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                :disabled="loading"
                @change="setRow(($event.target as HTMLSelectElement).value)"
              >
                <option v-for="d in rowChoices" :key="d.key" :value="d.key">
                  {{ d.label }}
                </option>
              </select>
            </div>
            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">列（横軸）</label>
              <select
                :value="columnDimensionKey"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                :disabled="loading"
                @change="setCol(($event.target as HTMLSelectElement).value)"
              >
                <option v-for="d in colChoices" :key="d.key" :value="d.key">
                  {{ d.label }}
                </option>
              </select>
            </div>
            <button
              type="button"
              class="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 px-3 py-1.5 text-xs text-slate-700 hover:bg-slate-50"
              :disabled="loading"
              @click="emit('swapDimensions')"
            >
              <ArrowLeftRight class="h-3.5 w-3.5" />
              行 ⇄ 列 を入替
            </button>
          </div>
        </div>

        <!-- 表示する集計値（メトリクス） -->
        <div v-show="conditionTab === 'metric'">
          <!-- 気温エリア種別。時間軸（年/四半期/月）と組み合わせると気温メトリクスが選択可能になる。 -->
          <div class="mb-2 rounded-lg border border-sky-100 bg-sky-50/40 p-2">
            <label class="mb-1 flex items-center gap-1.5 text-xs font-medium text-sky-700">
              <Thermometer class="h-3.5 w-3.5" />
              気温エリア（標準気候）
            </label>
            <select
              :value="temperatureArea ?? ''"
              class="w-full rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm"
              @change="onTemperatureAreaSelect"
            >
              <option value="">気温を表示しない</option>
              <option v-for="a in temperatureAreaOptions" :key="a.value" :value="a.value">
                {{ a.label }}
              </option>
            </select>
            <p v-if="temperatureArea && !hasTimeAxis" class="mt-1 text-xs text-amber-600">
              気温は時間軸（年・四半期・月）を行または列に設定すると表示できます。
            </p>
            <p v-else-if="temperatureArea" class="mt-1 text-xs text-slate-400">
              週平均/最高/最低気温を集計値として選べます。
            </p>
          </div>

          <div class="grid grid-cols-1 gap-x-4 gap-y-1.5">
            <label
              v-for="m in metrics"
              :key="m.key"
              class="flex items-center gap-2 text-sm"
              :class="
                isMetricDisabled(m.key)
                  ? 'cursor-not-allowed text-slate-300'
                  : 'cursor-pointer text-slate-700'
              "
            >
              <input
                type="checkbox"
                :checked="selectedMetrics.includes(m.key)"
                :disabled="isMetricDisabled(m.key)"
                class="accent-indigo-600 disabled:opacity-40"
                @change="toggleMetric(m.key)"
              >
              {{ m.label }}
              <span v-if="isMetricDisabled(m.key)" class="text-xs text-slate-400">
                （時間軸では利用不可）
              </span>
            </label>
          </div>

          <div v-if="selectedMetrics.length >= 2" class="mt-3">
            <label class="mb-1 block text-xs font-medium text-slate-500">複数集計値の表示方法</label>
            <div class="flex flex-wrap gap-2">
              <button
                type="button"
                class="rounded-lg border px-3 py-1.5 text-xs"
                :class="
                  metricDisplayMode === 'stacked'
                    ? 'border-indigo-500 bg-indigo-50 text-indigo-700'
                    : 'border-slate-300 bg-white text-slate-600 hover:bg-slate-50'
                "
                @click="setDisplayMode('stacked')"
              >
                セル内で縦並び
              </button>
              <button
                type="button"
                class="rounded-lg border px-3 py-1.5 text-xs"
                :class="
                  metricDisplayMode === 'metricRows'
                    ? 'border-indigo-500 bg-indigo-50 text-indigo-700'
                    : 'border-slate-300 bg-white text-slate-600 hover:bg-slate-50'
                "
                @click="setDisplayMode('metricRows')"
              >
                行を増やして横並び
              </button>
              <button
                type="button"
                class="rounded-lg border px-3 py-1.5 text-xs"
                :class="
                  metricDisplayMode === 'inlineColumns'
                    ? 'border-indigo-500 bg-indigo-50 text-indigo-700'
                    : 'border-slate-300 bg-white text-slate-600 hover:bg-slate-50'
                "
                @click="setDisplayMode('inlineColumns')"
              >
                列を増やして横並び
              </button>
            </div>
          </div>
        </div>

      <!-- 操作ボタン -->
      <div class="mt-4 flex flex-wrap gap-2">
        <button
          type="button"
          class="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-60"
          :disabled="loading"
          @click="emit('apply')"
        >
          <Search class="h-4 w-4" />
          適用
        </button>
        <button
          type="button"
          class="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 disabled:opacity-60"
          :disabled="loading"
          @click="emit('reset')"
        >
          <RotateCcw class="h-4 w-4" />
          リセット
        </button>
      </div>
    </div>
  </section>
</template>
