<script setup lang="ts">
/**
 * ランキング分析（/ranking）ページ。
 *
 * クロス集計（2軸マトリクス）とは別物の「ランキング特化ワークベンチ」。世界水準の3レンズを統合する：
 *  1. 複合スコアリング: 複数指標を 0..1 正規化し重み付け合算した総合スコアで順位付け。
 *  2. 期間比較・順位変動: 前年同期等と比較し、順位変動（▲▼/NEW）・成長率をスロープで可視化。
 *  3. ABC / パレート: 累積構成比で A/B/C 自動分類＋パレート図。
 *
 * データフロー（SoT）: バックエンドは集計素材（行ごとの主期間/比較期間の指標）のみ返す。
 * 順位・複合スコア・構成比・累積・ABC・順位変動・成長率は本ページ（+ utils/ranking）で算出する
 * 表示射影であり、並び替え指標・重み・件数・ABC閾値の変更はサーバ往復なしで即時反映する。
 * 集計軸・期間比較・フィルタの変更時のみ再取得する。
 */
import { Award, ListOrdered, TrendingUp, Trophy } from 'lucide-vue-next'
import type { KpiCardItem, RankingMetricKey, RankingResponse, SalesFilterState } from '~/types/api'
import type {
  CompositeWeight,
  MoverItem,
  ParetoBar,
  PeriodRow,
  RankingControls,
  RankingViewRow,
} from '~/utils/ranking'

useHead({ title: 'ランキング分析 | UndeuxSales' })

const { filter, optionsError, loadOptions, years, toQuery, reset, addToFilter } = useFilters()
const { get } = useApi()

/** パレート図に出す最大棒数（読みやすさのため）。 */
const PARETO_MAX = 30

function defaultWeights(): CompositeWeight[] {
  return [
    { key: 'amount', weight: 50 },
    { key: 'grossProfit', weight: 30 },
    { key: 'sellThroughRate', weight: 20 },
  ]
}

function defaultControls(): RankingControls {
  return {
    dimensionKey: 'hinban',
    sortKey: 'amount',
    order: 'top',
    topN: 20,
    weights: defaultWeights(),
    comparisonMode: 'none',
    customYear: null,
    thresholdA: 70,
    thresholdB: 90,
    displayMetrics: ['amount', 'grossProfit', 'grossProfitRate', 'sellThroughRate'],
  }
}

const controls = ref<RankingControls>(defaultControls())

const data = ref<RankingResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

// 初期 load 完了まで自動 fetch watch を抑止し、明示 load との二重発火を防ぐ。
const initialized = ref(false)
// reset/明示 load と watch の衝突を 1 回だけ抑止する旗。
let suppressAutoLoad = false

// 常時利用可能なフロー系指標（最新週スナップショットが無くても算出できる）。
// 初回ロード前でも並び替え・表示指標の選択肢を破綻なく出すためのフォールバック。
const FALLBACK_METRICS: RankingMetricKey[] = ['amount', 'quantity', 'grossProfit', 'grossProfitRate']
const availableMetrics = computed<RankingMetricKey[]>(() => data.value?.availableMetrics ?? FALLBACK_METRICS)

// ---------------------------------------------------------------
// データ取得
// ---------------------------------------------------------------

/** 比較期間の日付範囲（年度未選択・比較なし・任意年未選択は null）。 */
function comparisonRange(): { compareFrom: string; compareTo: string } | null {
  const year = filter.value.year
  if (year === null || controls.value.comparisonMode === 'none') {
    return null
  }
  let comparisonYear: number | null = null
  if (controls.value.comparisonMode === 'previousYear') {
    comparisonYear = year - 1
  } else if (controls.value.comparisonMode === 'customYear') {
    comparisonYear = controls.value.customYear
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
    const query: Record<string, unknown> = {
      ...toQuery(),
      dimension: controls.value.dimensionKey,
    }
    const range = comparisonRange()
    if (range) {
      query.compareFrom = range.compareFrom
      query.compareTo = range.compareTo
    }
    data.value = await get<RankingResponse>('/api/ranking', query)
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

function applyAndLoad(): void {
  void load()
}

function resetAndLoad(): void {
  suppressAutoLoad = true
  reset()
  controls.value = defaultControls()
  void load()
  // watch のフラッシュ後にフラグを戻す。
  void nextTick(() => {
    suppressAutoLoad = false
  })
}

function assignFilter(next: SalesFilterState): void {
  filter.value = next
}

function removeHinban(value: string): void {
  filter.value.hinbans = filter.value.hinbans.filter((h) => h !== value)
}

function updateControls(next: RankingControls): void {
  controls.value = next
}

// ---------------------------------------------------------------
// 算出パイプライン（順位・複合スコア・ABC・順位変動・成長率）
// ---------------------------------------------------------------

/** 並び替え方向（指標の良い向き ⊕ 上位/下位）。 */
const sortDirection = computed<'higher' | 'lower'>(() => {
  const sortKey = controls.value.sortKey
  const base = sortKey === 'composite' ? 'higher' : rankingMetricInfo(sortKey).direction
  if (controls.value.order === 'bottom') {
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
  controls.value.sortKey === 'composite'
    ? compositeScores(currentPeriodRows.value, controls.value.weights)
    : null,
)
const compositeComparison = computed(() =>
  controls.value.sortKey === 'composite'
    ? compositeScores(comparisonPeriodRows.value, controls.value.weights)
    : null,
)

/** 並び替え値（複合スコア or 単一指標）を取り出す。 */
function sortValueFor(
  key: string,
  values: PeriodRow['values'],
  composite: Map<string, number | null> | null,
): number | null {
  const sortKey = controls.value.sortKey
  if (sortKey === 'composite') {
    return composite?.get(key) ?? null
  }
  return metricRawValue(values, sortKey)
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
  const sortKey = controls.value.sortKey
  if (sortKey !== 'composite' && rankingMetricInfo(sortKey).additive) {
    return sortKey
  }
  return 'amount'
})

// 成長率の基準指標は構成比・ABC と同じ「基準指標」に統一する。
// 率系（粗利率・消化率・在日）や複合スコアで並べた場合も売上金額などの合算可能指標を基準にし、
// 「率の変化率」という不自然な値を避ける（構成比/累積/ABC と同じ土台に揃える）。
const growthMetric = contributionMetric

const abc = computed(() =>
  computeAbc(
    currentPeriodRows.value,
    contributionMetric.value,
    controls.value.thresholdA,
    controls.value.thresholdB,
  ),
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
const displayRows = computed<RankingViewRow[]>(() => allViewRows.value.slice(0, controls.value.topN))

/** テーブルの指標列（並び替えが単一指標ならそれも含めることを保証する）。 */
const metricColumns = computed<RankingMetricKey[]>(() => {
  const set = new Set<RankingMetricKey>(controls.value.displayMetrics)
  const sortKey = controls.value.sortKey
  if (sortKey !== 'composite') {
    set.add(sortKey)
  }
  return RANKING_METRICS.filter((m) => set.has(m.key)).map((m) => m.key)
})

const showComposite = computed(() => controls.value.sortKey === 'composite')

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
 * これにより KPI「最大上昇」（全件母集団）とスロープの母集団を一致させる。
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
// ドリルダウン（クロス集計へ：同一軸 × 年）
// ---------------------------------------------------------------

function handleRowClick(row: RankingViewRow): void {
  const dim = controls.value.dimensionKey
  // フィルタ可能な軸はクリック値で絞り込み、クロス集計の時系列へ遷移する（sales.vue と同方針）。
  if (dim === 'department') {
    addToFilter('departments', row.key)
  } else if (dim === 'businessType') {
    addToFilter('businessTypes', row.key)
  } else if (dim === 'season') {
    addToFilter('seasons', row.key)
  } else if (dim === 'hinban') {
    addToFilter('hinbans', row.key)
  } else if (dim === 'product') {
    // product のキーは "業態|記号|品番|単品"。構造化キーから品番3桁を取り出す
    // （表示ラベル "品番-単品" の分割より堅牢）。
    const hinban = row.key.split('|')[2]
    if (hinban) {
      addToFilter('hinbans', hinban)
    }
  }
  void navigateTo({
    path: '/crosstab',
    query: { rowDimension: `category:${dim}`, columnDimension: 'time:year' },
  })
}

// ---------------------------------------------------------------
// 自動取得 watch（集計軸・期間比較の変更時のみ再取得）
// ---------------------------------------------------------------

watch(
  () => controls.value.dimensionKey,
  () => {
    if (initialized.value && !suppressAutoLoad) void load()
  },
)
watch(
  () => controls.value.comparisonMode,
  () => {
    if (initialized.value && !suppressAutoLoad) void load()
  },
)
watch(
  () => controls.value.customYear,
  () => {
    if (initialized.value && !suppressAutoLoad && controls.value.comparisonMode === 'customYear') {
      void load()
    }
  },
)

// API が返す利用可能指標に合わせて、並び替え・表示指標を健全化する。
watch(availableMetrics, (avail) => {
  if (avail.length === 0) return
  let next = controls.value
  let changed = false
  if (next.sortKey !== 'composite' && !avail.includes(next.sortKey)) {
    next = { ...next, sortKey: 'amount' }
    changed = true
  }
  const filtered = next.displayMetrics.filter((k) => avail.includes(k))
  const display = filtered.length > 0 ? filtered : ['amount' as RankingMetricKey]
  if (display.length !== next.displayMetrics.length) {
    next = { ...next, displayMetrics: display }
    changed = true
  }
  if (changed) {
    controls.value = next
  }
})

onMounted(async () => {
  await loadOptions()
  await load()
  initialized.value = true
})
</script>

<template>
  <div class="flex h-full flex-col gap-3">
    <RankingConditionPanel
      :controls="controls"
      :available-metrics="availableMetrics"
      :filter-state="filter"
      :options-error="optionsError"
      :available-years="years"
      :loading="loading"
      :truncated="data?.truncated ?? false"
      @update:controls="updateControls"
      @update:filter-state="assignFilter"
      @apply="applyAndLoad"
      @reset="resetAndLoad"
      @remove-hinban="removeHinban"
    />

    <div class="flex min-h-0 flex-1 flex-col gap-3">
      <StatusBlock
        :loading="loading"
        :error="errorMessage"
        :empty="!data || allViewRows.length === 0"
        empty-message="該当するデータがありません。フィルタや集計軸を見直してください。"
      >
        <div class="space-y-3">
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
            :dimension-label="rankingDimensionLabel(controls.dimensionKey)"
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
