<script setup lang="ts">
/**
 * クロス集計マトリクスの表コンポーネント。
 *
 * - スタックモード: セル内に複数メトリクスを縦積み表示
 * - インライン列モード: メトリクスを列として展開（列を増やして横並び）
 * - メトリクス行モード: メトリクスを行として展開（行を増やして横並び。行ラベル × 集計値の2列が左端固定）
 * - スティッキーヘッダ（上端 / 左端）
 * - 行小計列・列小計行・総計セル
 * - null 値は "—" 表示
 * - レスポンシブ: モバイル時は text-xs、横スクロールで表示
 *
 * tokutake-ai-platform の CrossTabTable.tsx を Vue 3 へポートしたコンポーネント。
 * undeux 既存の DataTable.vue とは仕様が大きく異なるため独立コンポーネントとして提供する。
 */

import { Check, Copy } from 'lucide-vue-next'
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

// インライン列モード・メトリクス行モードは2メトリクス以上のときだけ意味がある。
const isInline = computed(
  () => props.displayMode === 'inlineColumns' && metricInfos.value.length >= 2,
)
const isMetricRows = computed(
  () => props.displayMode === 'metricRows' && metricInfos.value.length >= 2,
)

const colSpan = computed(() => (isInline.value ? metricInfos.value.length : 1))
const numColumnGroups = computed(() => props.data.columnLabels.length + 1) // +1 for row-total
const totalColumns = computed(
  () => (isMetricRows.value ? 2 : 1) + colSpan.value * numColumnGroups.value,
)

// メトリクス行モードの左端固定2列（行ラベル / 集計値名）の幅。sticky の left オフセットと一致させる。
const ROW_LABEL_WIDTH = '10rem'
const METRIC_LABEL_WIDTH = '7rem'

/** セルの値（メトリクスごと）をフォーマットして返す。 */
function formatMetric(value: number | null | undefined, info: CrosstabMetricInfo): string {
  if (value === null || value === undefined) return '—'
  switch (info.format) {
    case 'currency':
      return formatCurrency(value)
    case 'percent':
      return formatPercent(value)
    case 'temperature':
      return `${formatDecimal(value, 1)}℃`
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

// ============================================================
// クリップボードへのコピー（Excel への貼り付け用）
//
// Excel はクリップボードの text/html（インライン CSS 付き <table>）を解釈し、フォント・文字色・
// セル背景色・罫線・配置を再現する。そこで、描画済みテーブルの「計算済みスタイル」をインライン化した
// HTML を生成して text/html で書き込み、併せて TSV を text/plain で書き込む（他アプリ・素のペースト用）。
// Clipboard API（要セキュアコンテキスト）が使えない場合は execCommand へフォールバックする。
// ============================================================

const tableRef = ref<HTMLTableElement | null>(null)
const copied = ref(false)
const copyFailed = ref(false)
let feedbackTimer: ReturnType<typeof setTimeout> | null = null

const canCopy = computed(() => props.data.rowLabels.length > 0)

/** Excel 再現に必要な計算済みスタイルを clone 要素へインライン化し、class 等は除去する。 */
function inlineComputedStyle(source: Element, target: HTMLElement): void {
  const cs = getComputedStyle(source)
  const parts: string[] = []
  if (cs.color) parts.push(`color:${cs.color}`)
  const bg = cs.backgroundColor
  if (bg && bg !== 'rgba(0, 0, 0, 0)' && bg !== 'transparent') {
    parts.push(`background-color:${bg}`)
  }
  parts.push(`font-family:${cs.fontFamily}`)
  parts.push(`font-size:${cs.fontSize}`)
  parts.push(`font-weight:${cs.fontWeight}`)
  if (cs.fontStyle && cs.fontStyle !== 'normal') parts.push(`font-style:${cs.fontStyle}`)
  if (cs.textAlign && cs.textAlign !== 'start' && cs.textAlign !== 'normal') {
    parts.push(`text-align:${cs.textAlign}`)
  }
  if (cs.verticalAlign && cs.verticalAlign !== 'baseline') {
    parts.push(`vertical-align:${cs.verticalAlign}`)
  }
  // 罫線・余白はセル（td/th）にのみ付与する。画面は「片側のみ罫線 ＋ border-spacing:0」の
  // スキームだが、Excel 向けクリップボードは border-collapse:collapse で結合するため、片側だけだと
  // 外周や接合部の罫線が欠ける。そこで全辺を均一の 1px 罫線にし、画面と同じ完全な格子を再現する。
  const tag = source.tagName.toLowerCase()
  if (tag === 'td' || tag === 'th') {
    let gridColor = ''
    for (const side of ['bottom', 'right', 'top', 'left']) {
      if (cs.getPropertyValue(`border-${side}-width`) !== '0px') {
        gridColor = cs.getPropertyValue(`border-${side}-color`)
        break
      }
    }
    parts.push(`border:1px solid ${gridColor || 'rgb(226, 232, 240)'}`)
    parts.push(`padding:${cs.paddingTop} ${cs.paddingRight} ${cs.paddingBottom} ${cs.paddingLeft}`)
  }
  target.setAttribute('style', parts.join(';'))
  target.removeAttribute('class')
}

/** 描画済みテーブルから、計算済みスタイルをインライン化した Excel 貼り付け用 HTML を生成する。 */
function buildStyledHtml(source: HTMLTableElement): string {
  const clone = source.cloneNode(true) as HTMLTableElement
  inlineComputedStyle(source, clone)
  // Excel では罫線をセル間で結合させたいので collapse にする（描画は separate のまま）。
  clone.style.borderCollapse = 'collapse'
  clone.style.borderSpacing = '0'

  const srcEls = source.querySelectorAll<HTMLElement>('*')
  const dstEls = clone.querySelectorAll<HTMLElement>('*')
  for (let i = 0; i < srcEls.length; i++) {
    const s = srcEls[i]
    const d = dstEls[i]
    if (s && d) inlineComputedStyle(s, d)
  }
  return `<meta charset="utf-8">${clone.outerHTML}`
}

/**
 * セル内テキストを抽出する。縦積み（stacked）メトリクスのセルは
 * "ラベル 値 / ラベル 値" の形に整形し、TSV で読めるようにする（素の textContent は
 * ラベルと値が連結して潰れるため）。インライン列・単一メトリクスは textContent をそのまま使う。
 */
function extractCellText(cell: HTMLTableCellElement): string {
  const stackedRows = cell.querySelectorAll(':scope > div > div')
  if (stackedRows.length > 0) {
    return Array.from(stackedRows)
      .map((row) =>
        Array.from(row.children)
          .map((child) => (child.textContent ?? '').trim())
          .filter(Boolean)
          .join(' '),
      )
      .filter(Boolean)
      .join(' / ')
  }
  return (cell.textContent ?? '').replace(/\s+/g, ' ').trim()
}

/**
 * 素のペースト用に TSV（タブ区切り）を生成する。
 * rowspan / colspan（インライン列モードのヘッダ・メトリクス行モードの行ラベル）を
 * 矩形グリッドへ展開し、後続行の列ずれを防ぐ（span の先頭セルのみ値、残りは空欄）。
 */
function buildPlainText(source: HTMLTableElement): string {
  const grid: string[][] = []
  const rows = Array.from(source.rows)
  for (let r = 0; r < rows.length; r++) {
    grid[r] ??= []
    const gridRow = grid[r]!
    let c = 0
    for (const cell of Array.from(rows[r]!.cells)) {
      // 先行セルの rowspan で占有済みの列を読み飛ばす。
      while (gridRow[c] !== undefined) c++
      const text = extractCellText(cell)
      const colSpanCount = cell.colSpan || 1
      const rowSpanCount = cell.rowSpan || 1
      for (let dr = 0; dr < rowSpanCount; dr++) {
        grid[r + dr] ??= []
        for (let dc = 0; dc < colSpanCount; dc++) {
          grid[r + dr]![c + dc] = dr === 0 && dc === 0 ? text : ''
        }
      }
      c += colSpanCount
    }
  }
  return grid.map((row) => Array.from(row, (v) => v ?? '').join('\t')).join('\n')
}

/** execCommand によるフォールバックコピー（Clipboard API 非対応・非セキュアコンテキスト用）。 */
function fallbackCopy(html: string): boolean {
  const container = document.createElement('div')
  container.setAttribute('contenteditable', 'true')
  container.style.position = 'fixed'
  container.style.left = '-9999px'
  container.style.top = '0'
  container.innerHTML = html
  document.body.appendChild(container)

  const selection = window.getSelection()
  // ユーザーが既に持っている選択範囲を退避し、コピー後に復元する。
  const savedRanges: Range[] = selection
    ? Array.from({ length: selection.rangeCount }, (_, i) => selection.getRangeAt(i))
    : []
  const range = document.createRange()
  range.selectNodeContents(container)
  selection?.removeAllRanges()
  selection?.addRange(range)

  let ok = false
  try {
    ok = document.execCommand('copy')
  } catch {
    ok = false
  }
  selection?.removeAllRanges()
  for (const saved of savedRanges) selection?.addRange(saved)
  document.body.removeChild(container)
  return ok
}

function showFeedback(success: boolean): void {
  if (feedbackTimer) clearTimeout(feedbackTimer)
  copied.value = success
  copyFailed.value = !success
  feedbackTimer = setTimeout(() => {
    copied.value = false
    copyFailed.value = false
  }, success ? 2000 : 3000)
}

/** テーブルをクリップボードへコピーする（text/html ＋ text/plain）。 */
async function copyForExcel(): Promise<void> {
  const table = tableRef.value
  if (!table || !canCopy.value) return

  const html = buildStyledHtml(table)
  const text = buildPlainText(table)

  let ok = false
  try {
    if (navigator.clipboard?.write && typeof ClipboardItem !== 'undefined') {
      await navigator.clipboard.write([
        new ClipboardItem({
          'text/html': new Blob([html], { type: 'text/html' }),
          'text/plain': new Blob([text], { type: 'text/plain' }),
        }),
      ])
      ok = true
    }
  } catch {
    ok = false
  }
  // 主要パスが失敗（権限・非対応・非セキュア）したら execCommand へフォールバック。
  if (!ok) ok = fallbackCopy(html)
  showFeedback(ok)
}

onBeforeUnmount(() => {
  if (feedbackTimer) clearTimeout(feedbackTimer)
})

</script>

<template>
  <div class="flex min-h-0 flex-1 flex-col gap-1.5">
    <!-- コピーツールバー（テーブル直上・右寄せ。邪魔にならない位置に配置） -->
    <div class="flex shrink-0 items-center justify-end">
      <button
        type="button"
        class="inline-flex items-center gap-1.5 rounded-lg border px-2.5 py-1 text-xs transition-colors disabled:cursor-not-allowed disabled:opacity-40"
        :class="
          copied
            ? 'border-emerald-300 bg-emerald-50 text-emerald-700'
            : copyFailed
              ? 'border-rose-300 bg-rose-50 text-rose-700'
              : 'border-slate-300 bg-white text-slate-600 hover:bg-slate-50'
        "
        :disabled="!canCopy"
        aria-label="テーブルをExcel貼り付け用にクリップボードへコピー"
        title="スタイル付きでクリップボードへコピー（Excel に貼り付け可）"
        @click="copyForExcel"
      >
        <Check v-if="copied" class="h-3.5 w-3.5" aria-hidden="true" />
        <Copy v-else class="h-3.5 w-3.5" aria-hidden="true" />
        {{ copied ? 'コピーしました' : copyFailed ? 'コピーに失敗しました' : 'コピー' }}
      </button>
    </div>

    <div class="min-h-0 flex-1 w-full overflow-auto rounded-lg border border-slate-200 bg-white">
      <table ref="tableRef" class="text-xs md:text-sm" style="border-collapse: separate; border-spacing: 0; min-width: 100%">
      <thead>
        <!-- メイン列ヘッダ（メトリクス行モードは左端固定が「行ラベル + 集計値」の2列になる） -->
        <tr>
          <th
            scope="col"
            class="sticky left-0 top-0 z-30 whitespace-nowrap bg-slate-50 px-3 py-2 text-center font-semibold text-slate-700"
            :rowspan="isInline ? 2 : 1"
            :colspan="isMetricRows ? 2 : 1"
            :style="`min-width: ${isMetricRows ? '17rem' : ROW_LABEL_WIDTH}; border-bottom: 1px solid #e2e8f0; border-right: 1px solid #e2e8f0`"
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

        <!-- メトリクス行モード: 行ラベルごとに選択メトリクスをサブ行として展開（行を増やして横並び） -->
        <template v-if="isMetricRows">
          <template v-for="rl in data.rowLabels" :key="`mr-${rl}`">
            <tr v-for="(m, mi) in metricInfos" :key="`mr-${rl}-${m.key}`">
              <th
                v-if="mi === 0"
                scope="row"
                :rowspan="metricInfos.length"
                class="sticky left-0 z-10 whitespace-nowrap bg-white px-3 py-2 text-left align-top font-semibold text-slate-700"
                :style="`min-width: ${ROW_LABEL_WIDTH}; border-top: 1px solid #e2e8f0; border-right: 1px solid #e2e8f0`"
              >
                {{ rl }}
              </th>
              <th
                scope="row"
                class="sticky z-10 whitespace-nowrap bg-white px-2 py-2 text-left text-xs font-medium text-slate-500"
                :style="`left: ${ROW_LABEL_WIDTH}; min-width: ${METRIC_LABEL_WIDTH}; border-top: 1px solid #e2e8f0; border-right: 1px solid #e2e8f0`"
              >
                {{ m.label }}
              </th>
              <td
                v-for="cl in data.columnLabels"
                :key="`mr-${rl}-${m.key}-${cl}`"
                class="whitespace-nowrap bg-white px-3 py-2 text-right text-slate-600"
                style="border-top: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
              >
                {{ formatMetric(cellAt(rl, cl)?.values[m.key], m) }}
              </td>
              <td
                class="sticky right-0 z-10 whitespace-nowrap bg-indigo-50 px-3 py-2 text-right font-semibold text-indigo-700"
                style="border-top: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
              >
                {{ formatMetric(data.rowTotals[rl]?.values[m.key], m) }}
              </td>
            </tr>
          </template>

          <!-- 列合計（メトリクスごとのサブ行 + 総計） -->
          <template v-if="data.rowLabels.length > 0">
            <tr v-for="(m, mi) in metricInfos" :key="`mr-total-${m.key}`">
              <th
                v-if="mi === 0"
                scope="row"
                :rowspan="metricInfos.length"
                class="sticky left-0 z-10 whitespace-nowrap bg-indigo-50 px-3 py-2 text-left align-top font-semibold text-indigo-700"
                :style="`min-width: ${ROW_LABEL_WIDTH}; border-top: 1px solid #e2e8f0; border-right: 1px solid #e2e8f0`"
              >
                {{ TOTAL_LABEL }}
              </th>
              <th
                scope="row"
                class="sticky z-10 whitespace-nowrap bg-indigo-50 px-2 py-2 text-left text-xs font-medium text-indigo-600"
                :style="`left: ${ROW_LABEL_WIDTH}; min-width: ${METRIC_LABEL_WIDTH}; border-top: 1px solid #e2e8f0; border-right: 1px solid #e2e8f0`"
              >
                {{ m.label }}
              </th>
              <td
                v-for="cl in data.columnLabels"
                :key="`mr-total-${m.key}-${cl}`"
                class="whitespace-nowrap bg-indigo-50 px-3 py-2 text-right font-semibold text-indigo-700"
                style="border-top: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
              >
                {{ formatMetric(data.columnTotals[cl]?.values[m.key], m) }}
              </td>
              <td
                class="sticky right-0 z-20 whitespace-nowrap bg-indigo-100 px-3 py-2 text-right font-bold text-indigo-800"
                style="border-top: 1px solid #e2e8f0; border-left: 1px solid #e2e8f0"
              >
                {{ formatMetric(data.grandTotal.values[m.key], m) }}
              </td>
            </tr>
          </template>
        </template>

        <!-- 通常モード（スタック / インライン列） -->
        <template v-if="!isMetricRows">
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

        <!-- 列合計行（総計含む）。メトリクス行モードは上の専用ブロックで描画済み。 -->
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
        </template>
      </tbody>
      </table>
    </div>
  </div>
</template>
