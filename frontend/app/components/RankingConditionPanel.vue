<script setup lang="ts">
/**
 * ランキング分析の条件設定パネル。
 *
 * - 折り畳み式ヘッダ（モバイル時デフォルト閉 / PC時開）。CrossTabConditionPanel と同じ作法。
 * - フィルター: 年度・業態・部門・季節区分（チップ）＋ 品番タグ。共有 state（useFilters）を流用。
 * - 集計・並び替え: 集計軸 / 並び替え指標（指標 or 複合スコア）/ 上位下位 / 表示件数。
 * - 複合スコア: 指標ごとの重みスライダー（正規化%表示）。
 * - 期間比較: なし / 前年同期 / 任意の年（年度未選択時は無効）。
 * - ABC 閾値: A / B の上限累積%。
 * - 表示指標: テーブルに出す指標列のチェックボックス。
 *
 * 集計軸・期間比較はサーバ再取得が必要なため変更で自動取得（親が watch）。
 * フィルターは「適用」ボタンで明示取得。並び替え・件数・重み・ABC・表示指標はクライアント射影で即時反映。
 */
import { ChevronDown, Filter, RotateCcw, Scale, Search } from 'lucide-vue-next'
import type { RankingMetricKey, SalesFilterState } from '~/types/api'
import type { RankingControls, RankingSortKey } from '~/utils/ranking'

const {
  departmentChipOptions,
  businessTypeChipOptions,
  seasonChipOptions,
  tanawari1ChipOptions,
} = useFilters()

// 平均在庫日数バケットのチップ選択肢（共通カタログ＝SoT）。
const stockDaysChipOptions = STOCK_DAYS_BUCKETS.map((b) => ({ value: b.value, label: b.label }))

const props = defineProps<{
  controls: RankingControls
  /** API が返した利用可能指標（並び替え・複合スコア・表示指標の候補を律する）。 */
  availableMetrics: RankingMetricKey[]
  filterState: SalesFilterState
  optionsError: string | null
  availableYears: number[]
  loading: boolean
  /** 件数が上限で切り詰められたか（注記表示）。 */
  truncated: boolean
}>()

const emit = defineEmits<{
  'update:controls': [RankingControls]
  'update:filterState': [SalesFilterState]
  apply: []
  reset: []
  removeHinban: [string]
}>()

// モバイル幅判定（CrossTabConditionPanel と同一パターン）。
const isMobile = ref(false)
let detachListener: (() => void) | null = null

onMounted(() => {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return
  }
  const mql = window.matchMedia('(max-width: 767px)')
  const listener = (event: MediaQueryListEvent): void => {
    isMobile.value = event.matches
  }
  isMobile.value = mql.matches
  mql.addEventListener('change', listener)
  detachListener = () => mql.removeEventListener('change', listener)
})

onBeforeUnmount(() => {
  detachListener?.()
  detachListener = null
})

const panelOpenOverride = ref<boolean | null>(null)
const panelOpen = computed(() =>
  panelOpenOverride.value === null ? !isMobile.value : panelOpenOverride.value,
)
function togglePanel(): void {
  panelOpenOverride.value = !panelOpen.value
}

const showAdvanced = ref(false)

/** controls の部分更新を親に伝播する。 */
function patch(partial: Partial<RankingControls>): void {
  emit('update:controls', { ...props.controls, ...partial })
}

function updateFilter<K extends keyof SalesFilterState>(field: K, value: SalesFilterState[K]): void {
  emit('update:filterState', { ...props.filterState, [field]: value })
}

// 並び替え指標の選択肢（利用可能指標 + 複合スコア）。
const sortMetricOptions = computed(() =>
  RANKING_METRICS.filter((m) => props.availableMetrics.includes(m.key)),
)

// 表示指標の候補（利用可能指標）。
const displayMetricOptions = computed(() =>
  RANKING_METRICS.filter((m) => props.availableMetrics.includes(m.key)),
)

const isComposite = computed(() => props.controls.sortKey === 'composite')

function onSortKeyChange(event: Event): void {
  patch({ sortKey: (event.target as HTMLSelectElement).value as RankingSortKey })
}

function onDimensionChange(event: Event): void {
  patch({ dimensionKey: (event.target as HTMLSelectElement).value as RankingControls['dimensionKey'] })
}

function onTopNChange(event: Event): void {
  const v = Number.parseInt((event.target as HTMLSelectElement).value, 10)
  patch({ topN: Number.isFinite(v) && v > 0 ? v : 20 })
}

function setOrder(order: RankingControls['order']): void {
  patch({ order })
}

// --- 複合スコア重み ---
function weightFor(key: RankingMetricKey): number {
  return props.controls.weights.find((w) => w.key === key)?.weight ?? 0
}
const weightSum = computed(() =>
  props.availableMetrics.reduce((sum, k) => sum + weightFor(k), 0),
)
function normalizedWeightPercent(key: RankingMetricKey): number {
  return weightSum.value > 0 ? Math.round((weightFor(key) / weightSum.value) * 100) : 0
}
function setWeight(key: RankingMetricKey, value: number): void {
  const others = props.controls.weights.filter((w) => w.key !== key)
  patch({ weights: [...others, { key, weight: value }] })
}
function onWeightInput(key: RankingMetricKey, event: Event): void {
  const v = Number.parseInt((event.target as HTMLInputElement).value, 10)
  setWeight(key, Number.isFinite(v) ? v : 0)
}

// --- 表示指標 ---
function toggleDisplayMetric(key: RankingMetricKey): void {
  const set = new Set(props.controls.displayMetrics)
  if (set.has(key)) {
    if (set.size === 1) return // 最低1つは残す
    set.delete(key)
  } else {
    set.add(key)
  }
  // カタログ順に整列して安定させる。
  const ordered = RANKING_METRICS.filter((m) => set.has(m.key)).map((m) => m.key)
  patch({ displayMetrics: ordered })
}

// --- 期間比較 ---
const comparisonDisabled = computed(() => props.filterState.year === null)
function onComparisonModeChange(event: Event): void {
  patch({ comparisonMode: (event.target as HTMLSelectElement).value as RankingControls['comparisonMode'] })
}
function onCustomYearChange(event: Event): void {
  const raw = (event.target as HTMLSelectElement).value
  patch({ customYear: raw === '' ? null : Number.parseInt(raw, 10) })
}
// 比較年候補（主期間の年を除く）。
const comparisonYearChoices = computed(() =>
  props.availableYears.filter((y) => y !== props.filterState.year),
)

// --- ABC 閾値 ---
function onThresholdAInput(event: Event): void {
  const v = Number.parseFloat((event.target as HTMLInputElement).value)
  if (!Number.isFinite(v)) return
  // A は 1..(B-1) にクランプし、A < B を保証する。
  const clamped = Math.min(Math.max(v, 1), props.controls.thresholdB - 1)
  patch({ thresholdA: clamped })
}
function onThresholdBInput(event: Event): void {
  const v = Number.parseFloat((event.target as HTMLInputElement).value)
  if (!Number.isFinite(v)) return
  const clamped = Math.min(Math.max(v, props.controls.thresholdA + 1), 99)
  patch({ thresholdB: clamped })
}

function onYearSelect(event: Event): void {
  const value = (event.target as HTMLSelectElement).value
  updateFilter('year', value === '' ? null : Number.parseInt(value, 10))
}

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

const comparisonSummary = computed(() => {
  switch (props.controls.comparisonMode) {
    case 'previousYear':
      return '前年同期比較'
    case 'customYear':
      return props.controls.customYear !== null ? `${props.controls.customYear}年と比較` : '比較年未選択'
    default:
      return '比較なし'
  }
})

const sortSummary = computed(() => {
  const sortKey = props.controls.sortKey
  return sortKey === 'composite' ? '複合スコア' : rankingMetricInfo(sortKey).label
})

const summaryText = computed(() =>
  `${rankingDimensionLabel(props.controls.dimensionKey)}`
  + ` ／ 並び替え: ${sortSummary.value}（${props.controls.order === 'top' ? '上位' : '下位'} ${props.controls.topN}）`
  + ` ／ ${comparisonSummary.value} ／ フィルタ ${activeFilterCount.value} 件`,
)
</script>

<template>
  <section class="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm" aria-label="ランキング条件パネル">
    <!-- ヘッダ -->
    <div class="flex items-center justify-between gap-3 px-4 py-2.5">
      <div class="flex min-w-0 items-baseline gap-3">
        <h1 class="text-lg font-bold text-slate-800">ランキング分析</h1>
        <span class="truncate text-xs text-slate-500">{{ summaryText }}</span>
      </div>
      <button
        type="button"
        class="inline-flex shrink-0 items-center gap-1.5 rounded-lg border border-slate-300 px-3 py-1.5 text-xs text-slate-700 hover:bg-slate-50"
        :aria-expanded="panelOpen"
        aria-controls="ranking-condition-panel-body"
        @click="togglePanel"
      >
        <Filter class="h-3.5 w-3.5" />
        条件設定
        <span
          v-if="activeFilterCount > 0"
          class="inline-flex items-center justify-center rounded-full bg-indigo-50 px-1.5 text-xs font-semibold text-indigo-700"
          style="min-width: 1.125rem"
        >{{ activeFilterCount }}</span>
        <ChevronDown class="h-3 w-3 text-slate-400 transition-transform" :class="panelOpen ? 'rotate-180' : ''" aria-hidden="true" />
      </button>
    </div>

    <p v-if="optionsError" class="mx-4 mb-2 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700">
      フィルタ選択肢の取得に失敗しました: {{ optionsError }}
    </p>

    <div v-if="panelOpen" id="ranking-condition-panel-body" class="border-t border-slate-100 px-4 pb-4 pt-3">
      <div class="grid grid-cols-1 gap-4 lg:grid-cols-3">
        <!-- フィルター -->
        <div class="lg:col-span-1">
          <h3 class="mb-2 text-xs uppercase tracking-wider text-slate-500">フィルター</h3>

          <details class="mb-2" open>
            <summary class="cursor-pointer text-sm text-slate-700">
              年度
              <span v-if="filterState.year !== null" class="ml-2 text-xs text-indigo-600">●{{ filterState.year }}年</span>
            </summary>
            <div class="mt-2">
              <select
                :value="filterState.year === null ? '' : String(filterState.year)"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                @change="onYearSelect"
              >
                <option value="">全期間</option>
                <option v-for="y in availableYears" :key="y" :value="String(y)">{{ y }}年</option>
              </select>
              <p v-if="filterState.year === null" class="mt-1 text-xs text-slate-400">
                ※ 期間比較を使うには年度を選択してください。
              </p>
            </div>
          </details>

          <details class="mb-2" open>
            <summary class="cursor-pointer text-sm text-slate-700">
              業態
              <span v-if="filterState.businessTypes.length > 0" class="ml-2 text-xs text-indigo-600">●{{ filterState.businessTypes.length }}</span>
            </summary>
            <div class="mt-2">
              <CrossTabMultiSelectChips
                :options="businessTypeChipOptions"
                :model-value="filterState.businessTypes"
                empty-label="業態候補がありません"
                @update:model-value="(v: string[]) => updateFilter('businessTypes', v)"
              />
            </div>
          </details>

          <details class="mb-2">
            <summary class="cursor-pointer text-sm text-slate-700">
              部門
              <span v-if="filterState.departments.length > 0" class="ml-2 text-xs text-indigo-600">●{{ filterState.departments.length }}</span>
            </summary>
            <div class="mt-2">
              <CrossTabMultiSelectChips
                :options="departmentChipOptions"
                :model-value="filterState.departments"
                empty-label="部門候補がありません"
                @update:model-value="(v: string[]) => updateFilter('departments', v)"
              />
            </div>
          </details>

          <details class="mb-2">
            <summary class="cursor-pointer text-sm text-slate-700">
              季節区分
              <span v-if="filterState.seasons.length > 0" class="ml-2 text-xs text-indigo-600">●{{ filterState.seasons.length }}</span>
            </summary>
            <div class="mt-2">
              <CrossTabMultiSelectChips
                :options="seasonChipOptions"
                :model-value="filterState.seasons"
                empty-label="季節区分候補がありません"
                @update:model-value="(v: string[]) => updateFilter('seasons', v)"
              />
            </div>
          </details>

          <details class="mb-2">
            <summary class="cursor-pointer text-sm text-slate-700">
              棚割1
              <span v-if="filterState.tanawari1.length > 0" class="ml-2 text-xs text-indigo-600">●{{ filterState.tanawari1.length }}</span>
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

          <details class="mb-2">
            <summary class="cursor-pointer text-sm text-slate-700">
              平均在庫日数
              <span v-if="filterState.stockDaysBuckets.length > 0" class="ml-2 text-xs text-indigo-600">●{{ filterState.stockDaysBuckets.length }}</span>
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

          <div class="mt-3 border-t border-dashed border-slate-200 pt-3">
            <button
              type="button"
              class="inline-flex w-full items-center justify-between gap-2 rounded-lg border border-slate-300 px-3 py-1.5 text-xs text-slate-700 hover:bg-slate-50"
              :aria-expanded="showAdvanced"
              @click="showAdvanced = !showAdvanced"
            >
              <span class="flex items-center gap-2">
                品番フィルター
                <span v-if="filterState.hinbans.length > 0" class="text-xs text-indigo-600">●{{ filterState.hinbans.length }}</span>
              </span>
              <ChevronDown class="h-3 w-3 transition-transform" :class="showAdvanced ? 'rotate-180' : ''" aria-hidden="true" />
            </button>
            <div v-if="showAdvanced" class="mt-2 space-y-2">
              <div v-if="filterState.hinbans.length > 0" class="flex flex-wrap gap-1.5">
                <span
                  v-for="h in filterState.hinbans"
                  :key="h"
                  class="inline-flex items-center gap-1 rounded-full bg-indigo-50 px-2.5 py-1 text-xs font-medium text-indigo-700"
                >
                  {{ h }}
                  <button type="button" class="text-indigo-500 hover:text-indigo-900" :aria-label="`品番 ${h} を解除`" @click="emit('removeHinban', h)">×</button>
                </span>
              </div>
              <p v-else class="text-xs text-slate-400">品番フィルターは指定されていません。</p>
            </div>
          </div>
        </div>

        <!-- 集計・並び替え -->
        <div class="lg:col-span-1">
          <h3 class="mb-2 text-xs uppercase tracking-wider text-slate-500">集計・並び替え</h3>
          <p class="mb-2 text-xs text-slate-400">集計軸・期間比較は変更で即再集計されます。</p>
          <div class="space-y-2">
            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">集計軸</label>
              <select :value="controls.dimensionKey" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm" :disabled="loading" @change="onDimensionChange">
                <option v-for="d in RANKING_DIMENSIONS" :key="d.key" :value="d.key">{{ d.label }}</option>
              </select>
            </div>
            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">並び替え</label>
              <select :value="controls.sortKey" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm" @change="onSortKeyChange">
                <option value="composite">複合スコア</option>
                <option v-for="m in sortMetricOptions" :key="m.key" :value="m.key">{{ m.label }}</option>
              </select>
            </div>
            <div class="flex items-end gap-2">
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">並び順</label>
                <div class="inline-flex overflow-hidden rounded-lg border border-slate-300">
                  <button
                    type="button"
                    class="px-3 py-1.5 text-xs"
                    :class="controls.order === 'top' ? 'bg-indigo-600 text-white' : 'bg-white text-slate-600'"
                    @click="setOrder('top')"
                  >上位</button>
                  <button
                    type="button"
                    class="px-3 py-1.5 text-xs"
                    :class="controls.order === 'bottom' ? 'bg-indigo-600 text-white' : 'bg-white text-slate-600'"
                    @click="setOrder('bottom')"
                  >下位</button>
                </div>
              </div>
              <div class="flex-1">
                <label class="mb-1 block text-xs font-medium text-slate-500">表示件数</label>
                <select :value="String(controls.topN)" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm" @change="onTopNChange">
                  <option value="10">上位10</option>
                  <option value="20">上位20</option>
                  <option value="30">上位30</option>
                  <option value="50">上位50</option>
                  <option value="100">上位100</option>
                </select>
              </div>
            </div>

            <!-- 複合スコア重み -->
            <div v-if="isComposite" class="rounded-lg border border-indigo-100 bg-indigo-50/40 p-2.5">
              <p class="mb-1.5 flex items-center gap-1.5 text-xs font-medium text-indigo-700">
                <Scale class="h-3.5 w-3.5" />
                複合スコアの重み
              </p>
              <div v-for="m in sortMetricOptions" :key="m.key" class="mb-1.5">
                <div class="flex items-center justify-between text-xs text-slate-600">
                  <span>{{ m.label }}</span>
                  <span class="tabular-nums text-slate-400">{{ weightFor(m.key) }}（{{ normalizedWeightPercent(m.key) }}%）</span>
                </div>
                <input
                  type="range"
                  min="0"
                  max="100"
                  step="5"
                  :value="weightFor(m.key)"
                  class="w-full accent-indigo-600"
                  :aria-label="`${m.label} の重み`"
                  @input="onWeightInput(m.key, $event)"
                >
              </div>
              <p v-if="weightSum <= 0" class="text-xs text-amber-600">重みを1つ以上設定してください。</p>
              <p class="mt-1 text-xs text-slate-400">各指標を0〜1に正規化し、重み（合計を100%換算）で加重平均します。</p>
            </div>
          </div>
        </div>

        <!-- 期間比較・ABC・表示指標 -->
        <div class="lg:col-span-1">
          <h3 class="mb-2 text-xs uppercase tracking-wider text-slate-500">期間比較・ABC・表示</h3>
          <div class="space-y-3">
            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">期間比較</label>
              <select
                :value="controls.comparisonMode"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm disabled:bg-slate-100 disabled:text-slate-400"
                :disabled="comparisonDisabled || loading"
                @change="onComparisonModeChange"
              >
                <option value="none">比較なし</option>
                <option value="previousYear">前年同期</option>
                <option value="customYear">任意の年と比較</option>
              </select>
              <select
                v-if="controls.comparisonMode === 'customYear' && !comparisonDisabled"
                :value="controls.customYear === null ? '' : String(controls.customYear)"
                class="mt-2 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                :disabled="loading"
                @change="onCustomYearChange"
              >
                <option value="">比較年を選択</option>
                <option v-for="y in comparisonYearChoices" :key="y" :value="String(y)">{{ y }}年</option>
              </select>
              <p v-if="comparisonDisabled" class="mt-1 text-xs text-slate-400">年度を選択すると比較できます。</p>
            </div>

            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">ABC 閾値（累積%）</label>
              <div class="flex items-center gap-2 text-sm">
                <span class="text-xs text-slate-500">A ≤</span>
                <input
                  type="number" min="1" max="98" :value="controls.thresholdA"
                  class="w-16 rounded-lg border border-slate-300 px-2 py-1 text-sm tabular-nums"
                  @change="onThresholdAInput"
                >
                <span class="text-xs text-slate-500">% ／ B ≤</span>
                <input
                  type="number" min="2" max="99" :value="controls.thresholdB"
                  class="w-16 rounded-lg border border-slate-300 px-2 py-1 text-sm tabular-nums"
                  @change="onThresholdBInput"
                >
                <span class="text-xs text-slate-500">%</span>
              </div>
            </div>

            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">表示する指標列</label>
              <div class="flex flex-col gap-1">
                <label v-for="m in displayMetricOptions" :key="m.key" class="flex cursor-pointer items-center gap-2 text-sm text-slate-700">
                  <input
                    type="checkbox"
                    class="accent-indigo-600"
                    :checked="controls.displayMetrics.includes(m.key)"
                    @change="toggleDisplayMetric(m.key)"
                  >
                  {{ m.label }}
                </label>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- 操作ボタン -->
      <div class="mt-4 flex flex-wrap items-center gap-2">
        <button
          type="button"
          class="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-60"
          :disabled="loading"
          @click="emit('apply')"
        >
          <Search class="h-4 w-4" />
          フィルタを適用
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
        <span v-if="truncated" class="rounded bg-amber-50 px-2 py-1 text-xs text-amber-700">
          対象が多いため上限件数で集計を打ち切りました。フィルタで絞り込むと精度が上がります。
        </span>
      </div>
    </div>
  </section>
</template>
