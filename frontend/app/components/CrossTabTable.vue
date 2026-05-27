<script setup lang="ts">
/**
 * クロス集計マトリクスの表コンポーネント。
 *
 * - スタックモード: セル内に複数メトリクスを縦積み表示
 * - インライン列モード: メトリクスを列として展開
 * - スティッキーヘッダ（上端 / 左端）
 * - 行小計列・列小計行・総計セル
 * - null 値は "—" 表示
 * - レスポンシブ: モバイル時は text-xs、横スクロールで表示
 *
 * tokutake-ai-platform の CrossTabTable.tsx を Vue 3 へポートしたコンポーネント。
 * undeux 既存の DataTable.vue とは仕様が大きく異なるため独立コンポーネントとして提供する。
 */

import type {
  CrosstabCell,
  CrosstabMatrixResponse,
  CrosstabMetricInfo,
  CrosstabMetricKey,
  MetricDisplayMode,
} from '~/types/api'

const props = defineProps<{
  data: CrosstabMatrixResponse
  selectedMetrics: CrosstabMetricKey[]
  metrics: CrosstabMetricInfo[]
  displayMode: MetricDisplayMode
}>()

const TOTAL_LABEL = '合計'
const HEADER_HEIGHT_PX = 38

// 選択中メトリクスをカタログ順に並べた info 配列。
const metricInfos = computed<CrosstabMetricInfo[]>(() => {
  const map = new Map(props.metrics.map((m) => [m.key, m]))
  return props.selectedMetrics
    .map((k) => map.get(k))
    .filter((m): m is CrosstabMetricInfo => m !== undefined)
})

// インライン列モードは2メトリクス以上のときだけ意味がある。
const isInline = computed(
  () => props.displayMode === 'inlineColumns' && metricInfos.value.length >= 2,
)

const colSpan = computed(() => (isInline.value ? metricInfos.value.length : 1))
const numColumnGroups = computed(() => props.data.columnLabels.length + 1) // +1 for row-total
const totalColumns = computed(() => 1 + colSpan.value * numColumnGroups.value)

/** セルの値（メトリクスごと）をフォーマットして返す。 */
function formatMetric(value: number | null | undefined, info: CrosstabMetricInfo): string {
  if (value === null || value === undefined) return '—'
  switch (info.format) {
    case 'currency':
      return formatCurrency(value)
    case 'percent':
      return formatPercent(value)
    case 'decimal':
      return formatDecimal(value, 1)
    case 'number':
    default:
      return formatNumber(value)
  }
}

function cellAt(row: string, col: string): CrosstabCell | undefined {
  return props.data.cells[row]?.[col]
}
</script>

<template>
  <div
    class="min-h-0 flex-1 w-full overflow-auto rounded-lg border border-slate-200 bg-white"
  >
    <table class="text-xs md:text-sm" style="border-collapse: separate; border-spacing: 0; min-width: 100%">
      <thead>
        <!-- メイン列ヘッダ -->
        <tr>
          <th
            scope="col"
            class="sticky left-0 top-0 z-30 whitespace-nowrap bg-slate-50 px-3 py-2 text-center font-semibold text-slate-700"
            :rowspan="isInline ? 2 : 1"
            style="min-width: 10rem; border-bottom: 1px solid #e2e8f0; border-right: 1px solid #e2e8f0"
          >
            {{ data.rowDimension.label }} ＼ {{ data.columnDimension.label }}
          </th>
          <th
            v-for="cl in data.columnLabels"
            :key="cl"
            scope="col"
            :colspan="colSpan"
            class="sticky top-0 z-20 whitespace-nowrap bg-slate-50 px-3 py-2 text-center font-semibold text-slate-700"
            style="border-bottom: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
          >
            {{ cl }}
          </th>
          <th
            scope="col"
            :colspan="colSpan"
            class="sticky right-0 top-0 z-30 whitespace-nowrap bg-indigo-50 px-3 py-2 text-center font-semibold text-indigo-700"
            style="border-bottom: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
          >
            {{ TOTAL_LABEL }}
          </th>
        </tr>

        <!-- 副ヘッダ（インライン列モード時のみ） -->
        <tr v-if="isInline">
          <template v-for="cl in data.columnLabels" :key="`metric-${cl}`">
            <th
              v-for="m in metricInfos"
              :key="`${cl}-${m.key}`"
              scope="col"
              class="sticky z-20 whitespace-nowrap bg-slate-50 px-2 py-1 text-right text-xs font-medium text-slate-500"
              :style="`top: ${HEADER_HEIGHT_PX}px; border-bottom: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0`"
            >
              {{ m.label }}
            </th>
          </template>
          <th
            v-for="m in metricInfos"
            :key="`total-${m.key}`"
            scope="col"
            class="sticky right-0 z-30 whitespace-nowrap bg-indigo-50 px-2 py-1 text-right text-xs font-medium text-indigo-600"
            :style="`top: ${HEADER_HEIGHT_PX}px; border-bottom: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0`"
          >
            {{ m.label }}
          </th>
        </tr>
      </thead>

      <tbody>
        <tr v-if="data.rowLabels.length === 0">
          <td
            :colspan="totalColumns"
            class="px-6 py-12 text-center text-slate-400"
          >
            該当データがありません。条件を見直してください。
          </td>
        </tr>

        <tr v-for="rl in data.rowLabels" :key="rl">
          <th
            scope="row"
            class="sticky left-0 z-10 whitespace-nowrap bg-white px-3 py-2 text-left font-semibold text-slate-700"
            style="border-top: 1px solid #e2e8f0; border-right: 1px solid #e2e8f0"
          >
            {{ rl }}
          </th>
          <template v-for="cl in data.columnLabels" :key="`${rl}-${cl}`">
            <template v-if="isInline">
              <td
                v-for="m in metricInfos"
                :key="`${rl}-${cl}-${m.key}`"
                class="whitespace-nowrap bg-white px-3 py-2 text-right text-slate-600"
                style="border-top: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
              >
                {{ formatMetric(cellAt(rl, cl)?.values[m.key], m) }}
              </td>
            </template>
            <td
              v-else
              class="whitespace-nowrap bg-white px-3 py-2 text-right text-slate-600"
              style="border-top: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
            >
              <template v-if="metricInfos.length === 1">
                {{ formatMetric(cellAt(rl, cl)?.values[metricInfos[0]!.key], metricInfos[0]!) }}
              </template>
              <div v-else class="flex flex-col gap-0.5">
                <div
                  v-for="m in metricInfos"
                  :key="`${rl}-${cl}-${m.key}-stacked`"
                  class="flex items-baseline justify-end gap-1"
                >
                  <span class="text-xs text-slate-400">{{ m.label }}</span>
                  <span>{{ formatMetric(cellAt(rl, cl)?.values[m.key], m) }}</span>
                </div>
              </div>
            </td>
          </template>

          <!-- 行合計 -->
          <template v-if="isInline">
            <td
              v-for="m in metricInfos"
              :key="`${rl}-total-${m.key}`"
              class="sticky right-0 z-10 whitespace-nowrap bg-indigo-50 px-3 py-2 text-right font-semibold text-indigo-700"
              style="border-top: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
            >
              {{ formatMetric(data.rowTotals[rl]?.values[m.key], m) }}
            </td>
          </template>
          <td
            v-else
            class="sticky right-0 z-10 whitespace-nowrap bg-indigo-50 px-3 py-2 text-right font-semibold text-indigo-700"
            style="border-top: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
          >
            <template v-if="metricInfos.length === 1">
              {{ formatMetric(data.rowTotals[rl]?.values[metricInfos[0]!.key], metricInfos[0]!) }}
            </template>
            <div v-else class="flex flex-col gap-0.5">
              <div
                v-for="m in metricInfos"
                :key="`${rl}-total-${m.key}-stacked`"
                class="flex items-baseline justify-end gap-1"
              >
                <span class="text-xs text-indigo-400">{{ m.label }}</span>
                <span>{{ formatMetric(data.rowTotals[rl]?.values[m.key], m) }}</span>
              </div>
            </div>
          </td>
        </tr>

        <!-- 列合計行（総計含む） -->
        <tr v-if="data.rowLabels.length > 0">
          <th
            scope="row"
            class="sticky left-0 z-10 whitespace-nowrap bg-indigo-50 px-3 py-2 text-left font-semibold text-indigo-700"
            style="border-top: 1px solid #e2e8f0; border-right: 1px solid #e2e8f0"
          >
            {{ TOTAL_LABEL }}
          </th>
          <template v-for="cl in data.columnLabels" :key="`total-${cl}`">
            <template v-if="isInline">
              <td
                v-for="m in metricInfos"
                :key="`total-${cl}-${m.key}`"
                class="whitespace-nowrap bg-indigo-50 px-3 py-2 text-right font-semibold text-indigo-700"
                style="border-top: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
              >
                {{ formatMetric(data.columnTotals[cl]?.values[m.key], m) }}
              </td>
            </template>
            <td
              v-else
              class="whitespace-nowrap bg-indigo-50 px-3 py-2 text-right font-semibold text-indigo-700"
              style="border-top: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
            >
              <template v-if="metricInfos.length === 1">
                {{ formatMetric(data.columnTotals[cl]?.values[metricInfos[0]!.key], metricInfos[0]!) }}
              </template>
              <div v-else class="flex flex-col gap-0.5">
                <div
                  v-for="m in metricInfos"
                  :key="`total-${cl}-${m.key}-stacked`"
                  class="flex items-baseline justify-end gap-1"
                >
                  <span class="text-xs text-indigo-400">{{ m.label }}</span>
                  <span>{{ formatMetric(data.columnTotals[cl]?.values[m.key], m) }}</span>
                </div>
              </div>
            </td>
          </template>

          <!-- 総計セル -->
          <template v-if="isInline">
            <td
              v-for="m in metricInfos"
              :key="`grand-${m.key}`"
              class="sticky right-0 z-20 whitespace-nowrap bg-indigo-100 px-3 py-2 text-right font-bold text-indigo-800"
              style="border-top: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
            >
              {{ formatMetric(data.grandTotal.values[m.key], m) }}
            </td>
          </template>
          <td
            v-else
            class="sticky right-0 z-20 whitespace-nowrap bg-indigo-100 px-3 py-2 text-right font-bold text-indigo-800"
            style="border-top: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
          >
            <template v-if="metricInfos.length === 1">
              {{ formatMetric(data.grandTotal.values[metricInfos[0]!.key], metricInfos[0]!) }}
            </template>
            <div v-else class="flex flex-col gap-0.5">
              <div
                v-for="m in metricInfos"
                :key="`grand-${m.key}-stacked`"
                class="flex items-baseline justify-end gap-1"
              >
                <span class="text-xs text-indigo-500">{{ m.label }}</span>
                <span>{{ formatMetric(data.grandTotal.values[m.key], m) }}</span>
              </div>
            </div>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
