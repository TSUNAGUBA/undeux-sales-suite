<script setup lang="ts">
/**
 * クロス集計（スタースキーマ / mart）ページ。
 *
 * エンドポイントは /api/mart/crosstab、データソースは分析 mart（fact_*・dim_*）。
 * 共有コンポーネント（CrossTabConditionPanel / CrossTabTable）で行×列マトリクスを表示する。
 *
 * - フィルタスコープは 'mart-filter'。
 * - mart 未構築時は MartNotBuiltNotice を表示し、データ取得を行わない。
 * - mart が集計軸として保持しない軸（帳票区分・棚割1・棚割2）はディメンション候補から除外する
 *   （API は未対応軸に HTTP 400 を返すため、UI 側で選ばせない。棚割1のフィルタは対応済み）。
 *
 * 在庫系メトリクス（在日・消化率・店頭在庫）は最新週スナップショット基準のため、
 * 時間軸（年/四半期/月）を含む組合せでは UI 上で無効化され、自動的に選択解除される。
 */

import type {
  CrosstabDimensionKey,
  CrosstabMatrixResponse,
  CrosstabMetricKey,
  MetricDisplayMode,
  SalesFilterState,
  TemperatureArea,
} from '~/types/api'
import {
  CROSSTAB_DEFAULT_METRICS,
  CROSSTAB_DIMENSIONS,
  CROSSTAB_LEGACY_DIMENSION_MAP,
  CROSSTAB_METRICS,
  CROSSTAB_STOCK_METRICS,
  CROSSTAB_TEMP_METRICS,
  crosstabDimension,
  pickCrosstabDimensionKey,
} from '~/utils/crosstabCatalog'

useHead({ title: 'クロス集計 | UndeuxSales' })

// mart 専用のフィルタスコープ。既存 sales 系（'sales-filter'）とは分離する。
const MART_SCOPE = 'mart-filter'
const { filter, optionsError, loadOptions, years, toQuery, reset } = useFilters(MART_SCOPE)
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()
const route = useRoute()

// 集計軸・メトリクスのカタログは utils/crosstabCatalog.ts（SoT）へ集約済み。
// 本ページはそのカタログを参照し、ページ固有の既定値のみをローカルに持つ。

const DEFAULT_ROW: CrosstabDimensionKey = 'category:businessType'
const DEFAULT_COL: CrosstabDimensionKey = 'time:year'
// 既定メトリクスはカタログ（SoT）を参照する。
const DEFAULT_METRICS = CROSSTAB_DEFAULT_METRICS

// ---------------------------------------------------------------
// ローカル state
// ---------------------------------------------------------------

const rowDimensionKey = ref<CrosstabDimensionKey>(DEFAULT_ROW)
const columnDimensionKey = ref<CrosstabDimensionKey>(DEFAULT_COL)
const selectedMetrics = ref<CrosstabMetricKey[]>([...DEFAULT_METRICS])
const metricDisplayMode = ref<MetricDisplayMode>('stacked')
// 気温メトリクスのエリア種別（null=気温なし）。時間軸との組合せで気温系が利用可能になる。
const temperatureArea = ref<TemperatureArea | null>(null)

const data = ref<CrosstabMatrixResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

// 初期化完了フラグ。onMounted 内の初期 load() 完了まで watch ベースの
// 自動 fetch を抑止し、ルートクエリ解釈による初期値変更と初期 load() の
// 二重発火（race）を防ぐ。
const initialized = ref(false)
// resetAndLoad で row/col を既定値に戻す際、watch による自動 fetch を
// 1 回だけスキップする旗。明示的 load() と watch の二重発火を防ぐ。
let skipNextDimensionWatch = false

// ---------------------------------------------------------------
// メトリクスの利用可否（時間軸 ⇒ 在庫系を除外）
//
// SoT: バックエンドの CrosstabDimensionInfo.isTimeAxis と AvailableMetrics。
// data 取得後は data.value 由来で判定し、未取得時のみ CROSSTAB_DIMENSIONS カタログから
// 算出する（API レスポンス到達前でも UI を破綻なく表示するためのフォールバック）。
// ---------------------------------------------------------------

/**
 * 行・列のいずれかが時間軸なら true。
 * 取得済みデータの dimensionInfo.isTimeAxis を優先し、未取得時は CROSSTAB_DIMENSIONS カタログから算出する。
 */
const hasTimeAxis = computed(() => {
  const d = data.value
  if (d) {
    return d.rowDimension.isTimeAxis || d.columnDimension.isTimeAxis
  }
  const rowInfo = crosstabDimension(rowDimensionKey.value)
  const colInfo = crosstabDimension(columnDimensionKey.value)
  return Boolean(rowInfo?.isTimeAxis || colInfo?.isTimeAxis)
})

/**
 * UI上で選択可能なメトリクス（在庫系は時間軸絡みなら除外）。
 * data 取得済みなら API レスポンスの availableMetrics を SoT として参照する。
 * 未取得時は DIMENSIONS カタログから時間軸判定 → 在庫系を除外する。
 */
const availableMetrics = computed<CrosstabMetricKey[]>(() => {
  const d = data.value
  if (d) {
    return d.availableMetrics
  }
  if (hasTimeAxis.value) {
    const base = CROSSTAB_METRICS
      .filter((m) => !CROSSTAB_STOCK_METRICS.includes(m.key) && !CROSSTAB_TEMP_METRICS.includes(m.key))
      .map((m) => m.key)
    return temperatureArea.value ? [...base, ...CROSSTAB_TEMP_METRICS] : base
  }
  return CROSSTAB_METRICS.filter((m) => !CROSSTAB_TEMP_METRICS.includes(m.key)).map((m) => m.key)
})

// 選択中メトリクスを利用可能集合へ健全化する（時間軸で在庫系、エリア未選択/時間軸なしで気温系を自動除外）。
watch(availableMetrics, (avail) => {
  const before = selectedMetrics.value
  const after = before.filter((m) => avail.includes(m))
  if (after.length === before.length) return
  selectedMetrics.value = after.length > 0 ? after : ['amount']
})

// ---------------------------------------------------------------
// データ取得
// ---------------------------------------------------------------

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    await refreshStatus()
    if (!isBuilt.value) {
      data.value = null
      return
    }
    data.value = await get<CrosstabMatrixResponse>('/api/mart/crosstab', {
      ...toQuery(),
      rowDimension: rowDimensionKey.value,
      columnDimension: columnDimensionKey.value,
      ...(temperatureArea.value ? { temperatureArea: temperatureArea.value } : {}),
    })
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

async function applyAndLoad(): Promise<void> {
  await load()
}

function resetAndLoad(): void {
  reset()
  // row/col/気温エリア が既定値と異なる場合のみ watch が発火する。そのときだけ
  // 自動 fetch を 1 回スキップし、明示的 load() に集約する。
  if (
    rowDimensionKey.value !== DEFAULT_ROW
    || columnDimensionKey.value !== DEFAULT_COL
    || temperatureArea.value !== null
  ) {
    skipNextDimensionWatch = true
  }
  rowDimensionKey.value = DEFAULT_ROW
  columnDimensionKey.value = DEFAULT_COL
  selectedMetrics.value = [...DEFAULT_METRICS]
  metricDisplayMode.value = 'stacked'
  temperatureArea.value = null
  void load()
}

function swapDimensions(): void {
  const tmp = rowDimensionKey.value
  rowDimensionKey.value = columnDimensionKey.value
  columnDimensionKey.value = tmp
}

function removeHinban(value: string): void {
  filter.value.hinbans = filter.value.hinbans.filter((h) => h !== value)
}

/** 子コンポーネントから受け取った新しいフィルタ state を ref に代入する。 */
function assignFilter(next: SalesFilterState): void {
  filter.value = next
}

// 行・列ディメンションが入れ替わって同一になった場合のガード。
// （実際は CrossTabConditionPanel の rowChoices/colChoices で除外しているため発生しない）
watch([rowDimensionKey, columnDimensionKey], ([r, c]) => {
  if (r === c) {
    const fallback = CROSSTAB_DIMENSIONS.find((d) => d.key !== r)
    if (fallback) {
      columnDimensionKey.value = fallback.key
    }
  }
})

// メトリクス更新ハンドラ。1つだけのときは表示モードを stacked に強制リセット。
function onUpdateSelectedMetrics(next: CrosstabMetricKey[]): void {
  selectedMetrics.value = next
  if (next.length < 2) {
    metricDisplayMode.value = 'stacked'
  }
}

// セル数の警告判定。SoT はバックエンドの rowTruncated/columnTruncated。
const cellCountWarning = computed(() => {
  const d = data.value
  if (!d) return null
  if (d.rowTruncated || d.columnTruncated) {
    const rows = d.rowLabels.length
    const cols = d.columnLabels.length
    return `表示は最大 ${rows} × ${cols} で打ち切られています。フィルタで絞り込んでください。`
  }
  return null
})

/**
 * 行・列ディメンション変更時の自動 fetch。
 * フィルタは「適用」ボタンで明示取得。メトリクス・表示モード変更はクライアント側の射影のため fetch 不要。
 */
watch([rowDimensionKey, columnDimensionKey, temperatureArea], (next, prev) => {
  if (!initialized.value) return
  if (next[0] === prev[0] && next[1] === prev[1] && next[2] === prev[2]) return
  if (skipNextDimensionWatch) {
    skipNextDimensionWatch = false
    return
  }
  void load()
})

onMounted(async () => {
  // 旧形式 `?dimension=xxx` 互換および新形式 `?rowDimension=&columnDimension=` を解釈する。
  const queryRow = pickCrosstabDimensionKey(route.query.rowDimension)
  const queryCol = pickCrosstabDimensionKey(route.query.columnDimension)
  const legacyDim = typeof route.query.dimension === 'string'
    ? CROSSTAB_LEGACY_DIMENSION_MAP[route.query.dimension]
    : undefined

  if (queryRow || queryCol) {
    rowDimensionKey.value = queryRow ?? DEFAULT_ROW
    columnDimensionKey.value = queryCol ?? DEFAULT_COL
  } else if (legacyDim) {
    rowDimensionKey.value = legacyDim
    columnDimensionKey.value = 'time:year'
  }

  await loadOptions()
  await load()
  // 初期 load 完了後に自動 fetch watch を有効化する。
  initialized.value = true
})
</script>

<template>
  <div class="flex h-full flex-col gap-3">
    <!-- mart 由来であることを示すページ見出し（共有パネルの h1 は固定文言のためページ側で表示）。 -->
    <div>
      <h1 class="text-xl font-bold text-slate-800">クロス集計</h1>
      <p class="text-sm text-slate-500">
        分析 mart（fact_sales / dim_*）から行 × 列マトリクスを集計。既存の売上参照（sales_weekly）とは
        別系統で、汎用ディメンショナルモデルを基盤とする。
      </p>
    </div>

    <!--
      左:条件パネル（デスクトップは固定幅サイドバー・自前スクロール）× 右:マトリクス（自前スクロール）。
      フィルタを操作しながらマトリクスの変化を常時目視できるようにする（要件 #8）。狭幅では縦積み
      （パネルは折り畳み可）。パネルをサイドバーの独立スクロール領域に置くことで、従来の単一カラム
      flex で発生していた「パネルが圧縮され overflow-hidden で見切れる」問題も解消する。
    -->
    <div class="flex min-h-0 flex-1 flex-col gap-3 lg:flex-row lg:gap-4">
      <div class="lg:w-80 lg:shrink-0 lg:overflow-y-auto xl:w-96">
        <CrossTabConditionPanel
          :dimensions="CROSSTAB_DIMENSIONS"
          :metrics="CROSSTAB_METRICS"
          :row-dimension-key="rowDimensionKey"
          :column-dimension-key="columnDimensionKey"
          :selected-metrics="selectedMetrics"
          :metric-display-mode="metricDisplayMode"
          :available-metrics="availableMetrics"
          :filter-state="filter"
          :temperature-area="temperatureArea"
          :has-time-axis="hasTimeAxis"
          :options-error="optionsError"
          :available-years="years"
          :loading="loading"
          @update:row-dimension-key="(v) => (rowDimensionKey = v as CrosstabDimensionKey)"
          @update:column-dimension-key="(v) => (columnDimensionKey = v as CrosstabDimensionKey)"
          @update:selected-metrics="onUpdateSelectedMetrics"
          @update:metric-display-mode="(v) => (metricDisplayMode = v)"
          @update:filter-state="(v) => assignFilter(v)"
          @update:temperature-area="(v) => (temperatureArea = v)"
          @swap-dimensions="swapDimensions"
          @apply="applyAndLoad"
          @reset="resetAndLoad"
          @remove-hinban="removeHinban"
        />
      </div>

      <div class="flex min-h-0 flex-1 flex-col gap-2">
        <StatusBlock
          :loading="loading"
          :error="errorMessage"
          :empty="isBuilt && (!data || data.rowLabels.length === 0)"
          empty-message="該当するデータがありません。フィルタを見直してください。"
        >
          <MartNotBuiltNotice v-if="!isBuilt" />
          <div v-else class="flex h-full flex-col gap-2">
            <div class="shrink-0 flex flex-wrap items-center justify-between gap-2 text-xs text-slate-500">
              <p>
                行 {{ data?.rowLabels.length ?? 0 }} ／ 列 {{ data?.columnLabels.length ?? 0 }}
                <span v-if="data?.latestWeek"> ／ 最新取込週: {{ data.latestWeek }}（在庫スナップショット基準）</span>
                <span v-if="hasTimeAxis"> ／ 在日・消化率・店頭在庫は時間軸との組合せでは表示されません</span>
              </p>
              <p v-if="cellCountWarning" class="rounded bg-amber-50 px-2 py-1 text-xs text-amber-700">
                {{ cellCountWarning }}
              </p>
            </div>

            <CrossTabTable
              v-if="data"
              :data="data"
              :selected-metrics="selectedMetrics"
              :metrics="CROSSTAB_METRICS"
              :display-mode="metricDisplayMode"
            />
          </div>
        </StatusBlock>
      </div>
    </div>
  </div>
</template>
