<script setup lang="ts">
/**
 * 部門分析（/mart/department）ページ。
 *
 * 「部門」を主軸に、分析 mart（fact_sales / dim_*）の売上・数量・粗利を集計して俯瞰する画面。
 * 全社サマリー（/mart）の内訳が複数軸の1つとして部門を扱うのに対し、本ページは部門軸に特化し、
 * 部門別ランキング（棒・表）と「部門 × 任意の二次軸」のクロス集計を1画面に集約する。
 *
 * - フィルタスコープは 'mart-filter'（他 mart ページと共有。部門/業態/年度等での絞り込みに対応）。
 * - 集計・クロス集計は共通コンポーネント（BarChartCard / DataTable / CrossTabTable）を再利用する。
 * - mart 未構築時は MartNotBuiltNotice を表示し、データ取得を行わない。
 */

import {
  Building2,
  CircleDollarSign,
  ExternalLink,
  PieChart,
  ShoppingCart,
  TrendingUp,
} from 'lucide-vue-next'
import type {
  BreakdownRow,
  CrosstabDimensionKey,
  CrosstabMatrixResponse,
  CrosstabMetricKey,
  KpiCardItem,
  MartBreakdownResponse,
  MartSummaryResponse,
} from '~/types/api'
import { CROSSTAB_DIMENSIONS, CROSSTAB_METRICS, crosstabDimension } from '~/utils/crosstabCatalog'

useHead({ title: '部門分析 | UndeuxSales' })

// 部門を固定の集計主軸（行）とする。二次軸（列）はユーザーが選択する。
const DEPARTMENT_DIMENSION: CrosstabDimensionKey = 'category:department'

const MART_SCOPE = 'mart-filter'
const { toQuery, loadOptions } = useFilters(MART_SCOPE)
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()

// 集計指標（棒・表・KPI強調で使う主指標）。
const metric = ref<'amount' | 'quantity' | 'grossProfit'>('amount')
const metricOptions = [
  { value: 'amount', label: '売上金額' },
  { value: 'quantity', label: '売上数量' },
  { value: 'grossProfit', label: '粗利' },
] as const
const metricLabel = computed(
  () => metricOptions.find((m) => m.value === metric.value)?.label ?? '売上金額',
)

// 部門 × 二次軸クロス集計の列（部門自身は除外）。既定は業態。
const columnChoices = computed(() =>
  CROSSTAB_DIMENSIONS.filter((d) => d.key !== DEPARTMENT_DIMENSION),
)
const columnDimensionKey = ref<CrosstabDimensionKey>('category:businessType')
const columnDimensionLabel = computed(
  () => crosstabDimension(columnDimensionKey.value)?.label ?? '業態',
)

const summary = ref<MartSummaryResponse | null>(null)
const breakdown = ref<MartBreakdownResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

// クロス集計（部門 × 二次軸）。本体表示をブロックしないよう独立に取得・エラー保持する（原則4）。
const crosstab = ref<CrosstabMatrixResponse | null>(null)
const crosstabLoading = ref(false)
const crosstabError = ref<string | null>(null)

// 連打・並行取得に対する世代ガード（/mart と同じ手法）。
let loadSeq = 0
let crosstabSeq = 0

function metricValue(row: BreakdownRow): number {
  if (metric.value === 'quantity') return row.quantity
  if (metric.value === 'grossProfit') return row.grossProfit
  return row.amount
}

// KPI（フィルタ適用後の全社集計＋対象部門数）。
const kpiItems = computed<KpiCardItem[]>(() => {
  const kpi = summary.value?.kpi
  if (!kpi) return []
  return [
    { label: '売上金額', value: formatCurrency(kpi.amount), icon: CircleDollarSign, accentClass: 'bg-indigo-50 text-indigo-600' },
    { label: '売上数量', value: `${formatNumber(kpi.quantity)} 点`, icon: ShoppingCart, accentClass: 'bg-sky-50 text-sky-600' },
    { label: '粗利', value: formatCurrency(kpi.grossProfit), icon: TrendingUp, accentClass: 'bg-emerald-50 text-emerald-600' },
    { label: '対象部門数', value: `${formatNumber(breakdown.value?.rows.length ?? 0)} 部門`, icon: Building2, accentClass: 'bg-amber-50 text-amber-600' },
  ]
})

// 棒グラフは上位15部門。表は全件（部門数は多くないため limit 100 で全件想定）。
const topRows = computed(() => (breakdown.value?.rows ?? []).slice(0, 15))
const breakdownLabels = computed(() => topRows.value.map((r) => r.label))
const breakdownData = computed(() => topRows.value.map(metricValue))

const tableColumns = [
  { key: 'label', label: '部門', frozen: true },
  { key: 'quantity', label: '数量', align: 'right' as const, format: (row: BreakdownRow) => formatNumber(row.quantity) },
  { key: 'amount', label: '売上金額', align: 'right' as const, format: (row: BreakdownRow) => formatCurrency(row.amount) },
  { key: 'grossProfit', label: '粗利', align: 'right' as const, format: (row: BreakdownRow) => formatCurrency(row.grossProfit) },
  { key: 'sharePercent', label: '構成比', align: 'right' as const, format: (row: BreakdownRow) => formatPercent(row.sharePercent) },
]

// クロス集計側で表示するメトリクス（主指標のみ。カタログから該当定義を再利用）。
const crosstabMetricKeys = computed<CrosstabMetricKey[]>(() => [metric.value])

async function loadCrosstab(): Promise<void> {
  if (!isBuilt.value) {
    ++crosstabSeq
    crosstab.value = null
    return
  }
  const seq = ++crosstabSeq
  crosstabLoading.value = true
  crosstabError.value = null
  try {
    const result = await get<CrosstabMatrixResponse>('/api/mart/crosstab', {
      ...toQuery(),
      rowDimension: DEPARTMENT_DIMENSION,
      columnDimension: columnDimensionKey.value,
    })
    if (seq !== crosstabSeq) return
    crosstab.value = result
  } catch (error) {
    if (seq === crosstabSeq) {
      crosstabError.value = apiErrorMessage(error)
    }
  } finally {
    if (seq === crosstabSeq) {
      crosstabLoading.value = false
    }
  }
}

// 集計本体（KPI サマリー＋部門別内訳）を取得する。フィルタ・指標（ソート順）に依存する。
async function loadCore(): Promise<void> {
  const seq = ++loadSeq
  loading.value = true
  errorMessage.value = null
  try {
    await refreshStatus()
    if (!isBuilt.value) {
      if (seq !== loadSeq) return
      summary.value = null
      breakdown.value = null
      crosstab.value = null
      return
    }
    const query = toQuery()
    const [summaryResult, breakdownResult] = await Promise.all([
      get<MartSummaryResponse>('/api/mart/summary', query),
      get<MartBreakdownResponse>('/api/mart/breakdown', {
        ...query,
        dimension: 'department',
        metric: metric.value,
        limit: 100,
      }),
    ])
    if (seq !== loadSeq) return
    summary.value = summaryResult
    breakdown.value = breakdownResult
  } catch (error) {
    if (seq === loadSeq) {
      errorMessage.value = apiErrorMessage(error)
    }
  } finally {
    if (seq === loadSeq) {
      loading.value = false
    }
  }
}

// フィルタ適用・初期表示: 集計本体とクロス集計をまとめて再取得する（どちらもフィルタに依存）。
// クロス集計は補助表示のため非ブロッキングで取得する（原則4）。
async function load(): Promise<void> {
  await loadCore()
  if (isBuilt.value) void loadCrosstab()
}

// 指標変更: 棒・表・KPI は本体の再取得で反映（breakdown は metric でソート順が変わる）。
// クロス集計は全指標値をセルに持つため指標非依存 ＝ 再取得せず、表示メトリクスのみ射影を切替える。
watch(metric, () => {
  void loadCore()
})

// 二次軸の変更はクロス集計のみ再取得する。
watch(columnDimensionKey, () => {
  void loadCrosstab()
})

// クロス集計ページ（専用メニュー）へ現在の二次軸を引き継いで遷移する。
function openCrosstab(): void {
  void navigateTo({
    path: '/mart/crosstab',
    query: { rowDimension: DEPARTMENT_DIMENSION, columnDimension: columnDimensionKey.value },
  })
}

onMounted(async () => {
  await loadOptions()
  await load()
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="flex items-center gap-1.5 text-xl font-bold text-slate-800">
        <PieChart class="h-5 w-5 text-indigo-500" />
        部門分析
      </h1>
      <p class="text-sm text-slate-500">
        分析 mart（fact_sales / dim_*）の売上・数量・粗利を「部門」を主軸に集計します。
        フィルターで部門・業態・年度等を絞り込み、部門別ランキングと「部門 × 任意軸」のクロス集計を確認できます。
      </p>
    </div>

    <FilterBar :scope-key="MART_SCOPE" @apply="load" />

    <!-- 集計指標の切替（棒・表・KPI 強調に反映）。 -->
    <div class="flex flex-wrap items-center gap-2">
      <span class="text-xs font-medium text-slate-500">集計指標</span>
      <div class="inline-flex overflow-hidden rounded-lg border border-slate-300">
        <button
          v-for="opt in metricOptions"
          :key="opt.value"
          type="button"
          class="px-3 py-1.5 text-sm transition-colors"
          :class="metric === opt.value ? 'bg-indigo-600 text-white' : 'bg-white text-slate-600 hover:bg-slate-50'"
          :aria-pressed="metric === opt.value"
          @click="metric = opt.value"
        >
          {{ opt.label }}
        </button>
      </div>
    </div>

    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="isBuilt && (breakdown !== null && breakdown.rows.length === 0)"
      empty-message="選択した条件に該当する部門データがありません。フィルターを見直してください。"
    >
      <MartNotBuiltNotice v-if="!isBuilt" />
      <div v-else class="space-y-4">
        <!-- KPI -->
        <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
          <KpiCard
            v-for="item in kpiItems"
            :key="item.label"
            :label="item.label"
            :value="item.value"
            :icon="item.icon"
            :accent-class="item.accentClass"
            :sub="item.sub"
          />
        </div>

        <!-- 部門別ランキング（棒） -->
        <BarChartCard
          v-if="breakdownLabels.length > 0"
          :title="`部門別 ${metricLabel}（上位15）`"
          :labels="breakdownLabels"
          :data="breakdownData"
          :series-label="metricLabel"
          color="#4f46e5"
          horizontal
        />

        <!-- 部門別 明細表 -->
        <DataTable
          :columns="tableColumns"
          :rows="breakdown?.rows ?? []"
          :row-key="(row: BreakdownRow) => row.key"
        />

        <!-- 部門 × 二次軸 クロス集計 -->
        <section class="space-y-2">
          <div class="flex flex-wrap items-end justify-between gap-3">
            <div class="flex flex-wrap items-end gap-3">
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">二次軸（列）</label>
                <select
                  v-model="columnDimensionKey"
                  class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
                  :disabled="crosstabLoading"
                >
                  <option v-for="d in columnChoices" :key="d.key" :value="d.key">{{ d.label }}</option>
                </select>
              </div>
              <p class="pb-2 text-xs text-slate-500">
                部門 × {{ columnDimensionLabel }} の{{ metricLabel }}
              </p>
            </div>
            <button
              type="button"
              class="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 px-3 py-2 text-sm font-medium text-slate-600 transition-colors hover:bg-slate-50 focus:outline-none focus:ring-2 focus:ring-indigo-400"
              @click="openCrosstab"
            >
              <ExternalLink class="h-4 w-4" />
              クロス集計で詳しく分析
            </button>
          </div>

          <StatusBlock
            :loading="crosstabLoading"
            :error="crosstabError"
            :empty="crosstab !== null && crosstab.rowLabels.length === 0"
            empty-message="該当するクロス集計データがありません。"
          >
            <CrossTabTable
              v-if="crosstab"
              :data="crosstab"
              :selected-metrics="crosstabMetricKeys"
              :metrics="CROSSTAB_METRICS"
              display-mode="stacked"
            />
          </StatusBlock>
        </section>
      </div>
    </StatusBlock>
  </div>
</template>
