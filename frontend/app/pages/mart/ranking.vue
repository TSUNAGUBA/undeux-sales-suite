<script setup lang="ts">
/**
 * ランキング分析（スタースキーマ / mart）ページ。
 *
 * 既存の /ranking を分析 mart（スタースキーマ）へ向けたミラー。エンドポイントは
 * /api/mart/ranking を使い、データソースは分析 mart（fact_*・dim_*）。順位・複合スコア・
 * 構成比・累積・ABC・順位変動・成長率の算出（utils/ranking）と表示射影は既存ページと同等で、
 * 純粋な表示コンポーネント（RankingTable / RankingParetoChart / RankingMoversChart / KpiCard）を再利用する。
 *
 * mart 固有の差分:
 * - フィルタスコープを 'mart-filter' に分離（既存 sales 系 'sales-filter' とは別系統）。
 * - mart 未構築時は MartNotBuiltNotice を表示し、データ取得を行わない。
 * - mart が保持しない軸（帳票区分・棚割1・棚割2）は集計軸の候補から除外する
 *   （API は未対応軸に HTTP 400 を返すため、UI 側で選ばせない）。
 * - 共有の RankingConditionPanel は集計軸 select に RANKING_DIMENSIONS（全軸）を直接埋め込んでおり
 *   props で軸を制限できないため、本ページでは props で制限できる範囲のシンプルな自前コントロール
 *   （集計軸・並び替え・並び順・件数・期間比較の各 select／ボタン）を構築し、フィルタは共通の
 *   FilterBar（mart スコープ）を使う。複合スコアの重み調整 UI と ABC 閾値編集 UI は省略し、
 *   既定値（重み amount50/粗利30/消化20、ABC 70/90）で算出する（順位・ABC・パレート・順位変動は機能を維持）。
 *
 * データフロー（SoT）: mart は集計素材（行ごとの主期間/比較期間の指標）のみ返す。
 * 順位・複合スコア・構成比・累積・ABC・順位変動・成長率は本ページ（+ utils/ranking）で算出する表示射影。
 */
import { Award, ListOrdered, TrendingUp, Trophy } from 'lucide-vue-next'
import type {
  KpiCardItem,
  RankingDimensionKey,
  RankingMetricKey,
  RankingResponse,
} from '~/types/api'
import type {
  CompositeWeight,
  MoverItem,
  ParetoBar,
  PeriodRow,
  RankingComparisonMode,
  RankingOrder,
  RankingSortKey,
  RankingViewRow,
} from '~/utils/ranking'

useHead({ title: 'ランキング分析（スタースキーマ） | UndeuxSales' })

// mart 専用のフィルタスコープ。既存 sales 系（'sales-filter'）とは分離する。
const MART_SCOPE = 'mart-filter'
const { filter, loadOptions, years, toQuery, addToFilter } = useFilters(MART_SCOPE)
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()

/** パレート図に出す最大棒数（読みやすさのため）。 */
const PARETO_MAX = 30

// ---------------------------------------------------------------
// 集計軸カタログ（mart がサポートする軸の部分集合）。
//
// mart は帳票区分（chohyoKubun）・棚割1/2（tanawari1/tanawari2）を保持しないため
// 候補から除外する。RANKING_DIMENSIONS（utils/ranking が SoT）から該当3軸を外したもの。
// ---------------------------------------------------------------
const EXCLUDED_DIMENSIONS: ReadonlySet<RankingDimensionKey> = new Set<RankingDimensionKey>([
  'chohyoKubun',
  'tanawari1',
  'tanawari2',
])
const MART_DIMENSIONS = RANKING_DIMENSIONS.filter((d) => !EXCLUDED_DIMENSIONS.has(d.key))

// 複合スコアの既定重み（/ranking と同一。本ページは重み調整 UI を持たないため固定）。
function defaultWeights(): CompositeWeight[] {
  return [
    { key: 'amount', weight: 50 },
    { key: 'grossProfit', weight: 30 },
    { key: 'sellThroughRate', weight: 20 },
  ]
}

// ---------------------------------------------------------------
// ローカル state（自前コントロール）
// ---------------------------------------------------------------
const dimensionKey = ref<RankingDimensionKey>('hinban')
const sortKey = ref<RankingSortKey>('amount')
const order = ref<RankingOrder>('top')
const topN = ref<number>(20)
const comparisonMode = ref<RankingComparisonMode>('none')
const customYear = ref<number | null>(null)
const weights = defaultWeights()
const thresholdA = 70
const thresholdB = 90
const displayMetrics = ref<RankingMetricKey[]>([
  'amount',
  'grossProfit',
  'grossProfitRate',
  'sellThroughRate',
])

const topNOptions = [10, 20, 30, 50, 100]

const data = ref<RankingResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

// 初期 load 完了まで自動 fetch watch を抑止し、明示 load との二重発火を防ぐ。
const initialized = ref(false)

// 常時利用可能なフロー系指標（最新週スナップショットが無くても算出できる）。
// 初回ロード前でも並び替え・表示指標の選択肢を破綻なく出すためのフォールバック。
const FALLBACK_METRICS: RankingMetricKey[] = ['amount', 'quantity', 'grossProfit', 'grossProfitRate']
const availableMetrics = computed<RankingMetricKey[]>(() => data.value?.availableMetrics ?? FALLBACK_METRICS)

// 並び替え・表示指標の選択肢（利用可能指標に律する）。
const sortMetricOptions = computed(() =>
  RANKING_METRICS.filter((m) => availableMetrics.value.includes(m.key)),
)

// ---------------------------------------------------------------
// データ取得
// ---------------------------------------------------------------

/** 比較期間の日付範囲（年度未選択・比較なし・任意年未選択は null）。 */
function comparisonRange(): { compareFrom: string; compareTo: string } | null {
  const year = filter.value.year
  if (year === null || comparisonMode.value === 'none') {
    return null
  }
  let comparisonYear: number | null = null
  if (comparisonMode.value === 'previousYear') {
    comparisonYear = year - 1
  } else if (comparisonMode.value === 'customYear') {
    comparisonYear = customYear.value
  }
  if (comparisonYear === null) {
    return null
  }
  return { compareFrom: `${comparisonYear}-01-01`, compareTo: `${comparisonYear}-12-31` }
}

/** 比較が有効か（期間比較レンズの表示可否）。 */
const comparisonActive = computed(() => comparisonRange() !== null)

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    await refreshStatus()
    if (!isBuilt.value) {
      data.value = null
      return
    }
    const query: Record<string, unknown> = {
      ...toQuery(),
      dimension: dimensionKey.value,
    }
    const range = comparisonRange()
    if (range) {
      query.compareFrom = range.compareFrom
      query.compareTo = range.compareTo
    }
    data.value = await get<RankingResponse>('/api/mart/ranking', query)
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

function applyAndLoad(): void {
  void load()
}

function onDimensionChange(event: Event): void {
  dimensionKey.value = (event.target as HTMLSelectElement).value as RankingDimensionKey
}
function onSortKeyChange(event: Event): void {
  sortKey.value = (event.target as HTMLSelectElement).value as RankingSortKey
}
function onTopNChange(event: Event): void {
  const v = Number.parseInt((event.target as HTMLSelectElement).value, 10)
  topN.value = Number.isFinite(v) && v > 0 ? v : 20
}
function onComparisonModeChange(event: Event): void {
  comparisonMode.value = (event.target as HTMLSelectElement).value as RankingComparisonMode
}
function onCustomYearChange(event: Event): void {
  const raw = (event.target as HTMLSelectElement).value
  customYear.value = raw === '' ? null : Number.parseInt(raw, 10)
}

// 期間比較は年度選択が前提（年度未選択時は無効化）。
const comparisonDisabled = computed(() => filter.value.year === null)
// 比較年候補（主期間の年を除く）。
const comparisonYearChoices = computed(() =>
  years.value.filter((y) => y !== filter.value.year),
)

// ---------------------------------------------------------------
// 算出パイプライン（順位・複合スコア・ABC・順位変動・成長率）
// /ranking と同一ロジック。utils/ranking の純粋関数を使う。
// ---------------------------------------------------------------

/** 並び替え方向（指標の良い向き ⊕ 上位/下位）。 */
const sortDirection = computed<'higher' | 'lower'>(() => {
  const key = sortKey.value
  const base = key === 'composite' ? 'higher' : rankingMetricInfo(key).direction
  if (order.value === 'bottom') {
    return base === 'higher' ? 'lower' : 'higher'
  }
  return base
})

const labelByKey = computed<Map<string, string>>(() => {
  const map = new Map<string, string>()
  for (const row of data.value?.rows ?? []) {
    map.set(row.key, row.label)
  }
  return map
})

const currentPeriodRows = computed<PeriodRow[]>(() =>
  (data.value?.rows ?? [])
    .filter((r) => r.current !== null)
    .map((r) => ({ key: r.key, values: r.current })),
)

const comparisonPeriodRows = computed<PeriodRow[]>(() =>
  (data.value?.rows ?? [])
    .filter((r) => r.comparison !== null)
    .map((r) => ({ key: r.key, values: r.comparison })),
)

const compositeCurrent = computed(() =>
  sortKey.value === 'composite' ? compositeScores(currentPeriodRows.value, weights) : null,
)
const compositeComparison = computed(() =>
  sortKey.value === 'composite' ? compositeScores(comparisonPeriodRows.value, weights) : null,
)

/** 並び替え値（複合スコア or 単一指標）を取り出す。 */
function sortValueFor(
  key: string,
  values: PeriodRow['values'],
  composite: Map<string, number | null> | null,
): number | null {
  if (sortKey.value === 'composite') {
    return composite?.get(key) ?? null
  }
  return metricRawValue(values, sortKey.value)
}

const currentRankMap = computed(() =>
  assignRanks(
    currentPeriodRows.value.map((r) => ({
      key: r.key,
      value: sortValueFor(r.key, r.values, compositeCurrent.value),
      tieBreak: r.values?.amount ?? 0,
    })),
    sortDirection.value,
  ),
)

const comparisonRankMap = computed(() =>
  assignRanks(
    comparisonPeriodRows.value.map((r) => ({
      key: r.key,
      value: sortValueFor(r.key, r.values, compositeComparison.value),
      tieBreak: r.values?.amount ?? 0,
    })),
    sortDirection.value,
  ),
)

/** 構成比・累積・ABC の基準指標（並び替えが合算可能指標ならそれ、率/複合は売上金額）。 */
const contributionMetric = computed<RankingMetricKey>(() => {
  const key = sortKey.value
  if (key !== 'composite' && rankingMetricInfo(key).additive) {
    return key
  }
  return 'amount'
})

// 成長率の基準指標は構成比・ABC と同じ「基準指標」に統一する。
const growthMetric = contributionMetric

const abc = computed(() =>
  computeAbc(currentPeriodRows.value, contributionMetric.value, thresholdA, thresholdB),
)

/** 当期に存在する全行を集約し、今期順位で昇順整列する。 */
const allViewRows = computed<RankingViewRow[]>(() => {
  const rankMap = currentRankMap.value
  const prevRankMap = comparisonRankMap.value
  const abcResult = abc.value
  const composite = compositeCurrent.value
  const hasComparison = comparisonActive.value
  const gm = growthMetric.value

  const rows: RankingViewRow[] = []
  for (const row of data.value?.rows ?? []) {
    if (row.current === null) continue
    const rank = rankMap.get(row.key)
    if (rank === undefined) continue
    const prevRank = prevRankMap.get(row.key) ?? null
    rows.push({
      key: row.key,
      label: row.label,
      rank,
      values: row.current,
      compositeScore: composite ? composite.get(row.key) ?? null : null,
      share: abcResult.share.get(row.key) ?? 0,
      cumulative: abcResult.cumulative.get(row.key) ?? 0,
      tier: abcResult.tier.get(row.key) ?? 'C',
      prevRank,
      rankDelta: prevRank !== null ? prevRank - rank : null,
      isNew: hasComparison && row.comparison === null,
      comparisonValues: row.comparison,
      growth: hasComparison
        ? growthPercent(metricRawValue(row.current, gm), metricRawValue(row.comparison, gm))
        : null,
    })
  }
  rows.sort((a, b) => a.rank - b.rank)
  return rows
})

/** 表示する上位 N 件。 */
const displayRows = computed<RankingViewRow[]>(() => allViewRows.value.slice(0, topN.value))

/** テーブルの指標列（並び替えが単一指標ならそれも含めることを保証する）。 */
const metricColumns = computed<RankingMetricKey[]>(() => {
  const set = new Set<RankingMetricKey>(displayMetrics.value)
  if (sortKey.value !== 'composite') {
    set.add(sortKey.value)
  }
  return RANKING_METRICS.filter((m) => set.has(m.key)).map((m) => m.key)
})

const showComposite = computed(() => sortKey.value === 'composite')

const contributionLabel = computed(() => rankingMetricInfo(contributionMetric.value).label)
const growthLabel = computed(() => rankingMetricInfo(growthMetric.value).label)

/** パレート図の棒（基準指標降順、上位 PARETO_MAX 件）。 */
const paretoBars = computed<ParetoBar[]>(() => {
  const abcResult = abc.value
  const metric = contributionMetric.value
  return currentPeriodRows.value
    .map((r) => ({ key: r.key, value: metricRawValue(r.values, metric) ?? 0 }))
    .filter((e) => e.value > 0)
    .sort((a, b) => b.value - a.value)
    .slice(0, PARETO_MAX)
    .map((e) => ({
      label: labelByKey.value.get(e.key) ?? e.key,
      value: e.value,
      cumulative: abcResult.cumulative.get(e.key) ?? 0,
      tier: abcResult.tier.get(e.key) ?? 'C',
    }))
})

/**
 * 順位変動スロープのアイテム（比較有効時のみ）。
 * テーブルの表示件数（topN）ではなく全件（順位昇順）から渡し、チャート側が上位を抽出する。
 */
const moverItems = computed<MoverItem[]>(() =>
  comparisonActive.value
    ? allViewRows.value.map((r) => ({
      key: r.key,
      label: r.label,
      rank: r.rank,
      prevRank: r.prevRank,
      isNew: r.isNew,
      delta: r.rankDelta,
    }))
    : [],
)

// ---------------------------------------------------------------
// KPI ストリップ
// ---------------------------------------------------------------

const aTierRows = computed(() => allViewRows.value.filter((r) => r.tier === 'A'))
const aTierShare = computed(() => aTierRows.value.reduce((sum, r) => sum + r.share, 0))

const topRiser = computed<RankingViewRow | null>(() => {
  let best: RankingViewRow | null = null
  for (const row of allViewRows.value) {
    if (row.rankDelta !== null && row.rankDelta > 0 && (best === null || row.rankDelta > best.rankDelta!)) {
      best = row
    }
  }
  return best
})

const topRow = computed<RankingViewRow | null>(() => allViewRows.value[0] ?? null)

const kpiItems = computed<KpiCardItem[]>(() => {
  if (allViewRows.value.length === 0) {
    return []
  }
  const items: KpiCardItem[] = [
    {
      label: '対象件数',
      value: `${formatNumber(allViewRows.value.length)} 件`,
      icon: ListOrdered,
      accentClass: 'bg-slate-50 text-slate-600',
    },
    {
      label: '首位',
      value: topRow.value ? topRow.value.label : '—',
      icon: Trophy,
      accentClass: 'bg-amber-50 text-amber-600',
    },
    {
      label: 'A ランク',
      value: `${formatNumber(aTierRows.value.length)} 件`,
      icon: Award,
      accentClass: 'bg-indigo-50 text-indigo-600',
    },
  ]
  if (comparisonActive.value) {
    items.push({
      label: '最大上昇',
      value: topRiser.value ? `${topRiser.value.label}` : '—',
      icon: TrendingUp,
      accentClass: 'bg-emerald-50 text-emerald-600',
    })
  }
  return items
})

const aTierSub = computed(() => `構成比 ${formatPercent(aTierShare.value)}`)
const riserSub = computed(() =>
  topRiser.value && topRiser.value.rankDelta !== null ? `▲${topRiser.value.rankDelta}` : '—',
)

// 集計基準週・比較週の注記。
const periodNote = computed(() => {
  const parts: string[] = []
  if (data.value?.latestWeek) {
    parts.push(`在庫スナップショット基準: ${data.value.latestWeek}`)
  }
  if (comparisonActive.value && data.value?.comparisonLatestWeek) {
    parts.push(`比較期間基準: ${data.value.comparisonLatestWeek}`)
  }
  return parts.join(' ／ ')
})

// ---------------------------------------------------------------
// ドリルダウン（mart クロス集計へ：同一軸 × 年）
// ---------------------------------------------------------------

function handleRowClick(row: RankingViewRow): void {
  const dim = dimensionKey.value
  // フィルタ可能な軸はクリック値で絞り込み、mart クロス集計の時系列へ遷移する。
  if (dim === 'department') {
    addToFilter('departments', row.key)
  } else if (dim === 'businessType') {
    addToFilter('businessTypes', row.key)
  } else if (dim === 'season') {
    addToFilter('seasons', row.key)
  } else if (dim === 'hinban') {
    addToFilter('hinbans', row.key)
  } else if (dim === 'product') {
    // product のキーは "業態|記号|品番|単品"。構造化キーから品番3桁を取り出す。
    const hinban = row.key.split('|')[2]
    if (hinban) {
      addToFilter('hinbans', hinban)
    }
  }
  void navigateTo({
    path: '/mart/crosstab',
    query: { rowDimension: `category:${dim}`, columnDimension: 'time:year' },
  })
}

// ---------------------------------------------------------------
// 自動取得 watch（集計軸・期間比較の変更時のみ再取得）
// ---------------------------------------------------------------

watch(dimensionKey, () => {
  if (initialized.value) void load()
})
watch(comparisonMode, () => {
  if (initialized.value) void load()
})
watch(customYear, () => {
  if (initialized.value && comparisonMode.value === 'customYear') {
    void load()
  }
})

// API が返す利用可能指標に合わせて、並び替え・表示指標を健全化する。
watch(availableMetrics, (avail) => {
  if (avail.length === 0) return
  if (sortKey.value !== 'composite' && !avail.includes(sortKey.value)) {
    sortKey.value = 'amount'
  }
  const filtered = displayMetrics.value.filter((k) => avail.includes(k))
  const next = filtered.length > 0 ? filtered : (['amount'] as RankingMetricKey[])
  if (next.length !== displayMetrics.value.length) {
    displayMetrics.value = next
  }
})

onMounted(async () => {
  await loadOptions()
  await load()
  initialized.value = true
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">ランキング分析（スタースキーマ）</h1>
      <p class="text-sm text-slate-500">
        分析 mart（fact_sales / dim_*）の集計素材から、順位・複合スコア・ABC/パレート・順位変動を算出。
        既存の売上参照（sales_weekly）とは別系統で、汎用ディメンショナルモデルを基盤とする。
      </p>
    </div>

    <!-- 集計・並び替えコントロール（自前。共有 RankingConditionPanel は集計軸を制限できないため） -->
    <div class="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
      <div class="flex flex-wrap items-end gap-3">
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">集計軸</label>
          <select
            :value="dimensionKey"
            class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
            :disabled="loading"
            @change="onDimensionChange"
          >
            <option v-for="d in MART_DIMENSIONS" :key="d.key" :value="d.key">{{ d.label }}</option>
          </select>
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">並び替え</label>
          <select
            :value="sortKey"
            class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
            @change="onSortKeyChange"
          >
            <option value="composite">複合スコア</option>
            <option v-for="m in sortMetricOptions" :key="m.key" :value="m.key">{{ m.label }}</option>
          </select>
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">並び順</label>
          <div class="inline-flex overflow-hidden rounded-lg border border-slate-300">
            <button
              type="button"
              class="px-3 py-2 text-xs"
              :class="order === 'top' ? 'bg-indigo-600 text-white' : 'bg-white text-slate-600'"
              @click="order = 'top'"
            >
              上位
            </button>
            <button
              type="button"
              class="px-3 py-2 text-xs"
              :class="order === 'bottom' ? 'bg-indigo-600 text-white' : 'bg-white text-slate-600'"
              @click="order = 'bottom'"
            >
              下位
            </button>
          </div>
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">表示件数</label>
          <select
            :value="String(topN)"
            class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
            @change="onTopNChange"
          >
            <option v-for="n in topNOptions" :key="n" :value="String(n)">上位{{ n }}</option>
          </select>
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">期間比較</label>
          <select
            :value="comparisonMode"
            class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm disabled:bg-slate-100 disabled:text-slate-400"
            :disabled="comparisonDisabled || loading"
            @change="onComparisonModeChange"
          >
            <option value="none">比較なし</option>
            <option value="previousYear">前年同期</option>
            <option value="customYear">任意の年と比較</option>
          </select>
        </div>
        <div v-if="comparisonMode === 'customYear' && !comparisonDisabled">
          <label class="mb-1 block text-xs font-medium text-slate-500">比較年</label>
          <select
            :value="customYear === null ? '' : String(customYear)"
            class="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
            :disabled="loading"
            @change="onCustomYearChange"
          >
            <option value="">比較年を選択</option>
            <option v-for="y in comparisonYearChoices" :key="y" :value="String(y)">{{ y }}年</option>
          </select>
        </div>
      </div>
      <p v-if="comparisonDisabled" class="mt-2 text-xs text-slate-400">
        ※ 期間比較を使うには、下のフィルターで年度を選択してください。
      </p>
      <p class="mt-1 text-xs text-slate-400">
        集計軸・期間比較は変更で即再集計されます。並び替え・並び順・件数はクライアント側で即時反映します。
        複合スコアの重み（売上50/粗利30/消化20）と ABC 閾値（70/90）は既定値で算出します。
      </p>
    </div>

    <FilterBar :scope-key="MART_SCOPE" @apply="applyAndLoad" />

    <div class="flex min-h-0 flex-1 flex-col gap-3">
      <StatusBlock
        :loading="loading"
        :error="errorMessage"
        :empty="isBuilt && (!data || allViewRows.length === 0)"
        empty-message="該当するデータがありません。フィルタや集計軸を見直してください。"
      >
        <MartNotBuiltNotice v-if="!isBuilt" />
        <div v-else class="space-y-3">
          <!-- KPI ストリップ -->
          <div class="grid grid-cols-2 gap-3 lg:grid-cols-4">
            <KpiCard
              v-for="(item, index) in kpiItems"
              :key="item.label"
              :label="item.label"
              :value="item.value"
              :icon="item.icon"
              :accent-class="item.accentClass"
              :sub="index === 2 ? aTierSub : (item.label === '最大上昇' ? riserSub : undefined)"
            />
          </div>

          <p v-if="periodNote" class="text-xs text-slate-400">{{ periodNote }}</p>

          <p v-if="data?.truncated" class="rounded bg-amber-50 px-2 py-1 text-xs text-amber-700">
            対象が多いため上限件数で集計を打ち切りました。フィルタで絞り込むと精度が上がります。
          </p>

          <!-- 可視化: パレート図 / 順位変動スロープ -->
          <div class="grid grid-cols-1 gap-3" :class="comparisonActive ? 'xl:grid-cols-2' : ''">
            <RankingParetoChart
              :bars="paretoBars"
              :metric-label="contributionLabel"
              :total-count="currentPeriodRows.length"
            />
            <RankingMoversChart v-if="comparisonActive" :items="moverItems" />
          </div>

          <!-- ランキング表 -->
          <RankingTable
            :rows="displayRows"
            :dimension-label="rankingDimensionLabel(dimensionKey)"
            :metric-columns="metricColumns"
            :show-composite="showComposite"
            :show-comparison="comparisonActive"
            :growth-label="growthLabel"
            clickable
            @row-click="handleRowClick"
          />
        </div>
      </StatusBlock>
    </div>
  </div>
</template>
