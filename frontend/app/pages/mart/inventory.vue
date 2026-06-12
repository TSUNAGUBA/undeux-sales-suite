<script setup lang="ts">
import {
  AlertOctagon,
  Boxes,
  CalendarClock,
  CircleDollarSign,
  ClipboardCopy,
  Gauge,
  LayoutDashboard,
  Scale,
  Search,
  Snowflake,
} from 'lucide-vue-next'
import type { Component } from 'vue'
import type {
  InventoryActionTargetTab,
  InventoryActionsResponse,
  InventoryItemRow,
  InventoryItemsPage,
  InventoryStatus,
  InventoryStatusCounts,
  KpiCardItem,
} from '~/types/api'

/**
 * 在庫マネジメント（アクション駆動）。「状態を表示する」だけでなく
 * 「滞留・不動を自動抽出し、発注抑制・値下げ等の判断に落とす」ことを目的とする。
 *
 * 構成: ページ内4タブ（ダッシュボード / 在庫一覧 / 滞留在庫 / 不動在庫）。
 * - タブは ?tab= クエリと同期する（全社サマリーのアクション導線から特定タブへ
 *   直リンクするため。ブラウザ戻る/進むにも追従する）。
 * - 滞留・不動の判定閾値の SoT はバックエンド InventoryHealthRules。画面の定義
 *   バナー・バケットはレスポンスの thresholds から描画する。
 * - 明細3タブは同一 API（/api/mart/inventory/items）を statuses 絞込だけ変えて共用する。
 */
useHead({ title: '在庫マネジメント | UndeuxSales' })

const MART_SCOPE = 'mart-filter'
const { toQuery, addToFilter, loadOptions } = useFilters(MART_SCOPE)
const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()
const route = useRoute()
const router = useRouter()

// ---- ページ内タブ（?tab= と同期） ----

const TABS: ReadonlyArray<{ key: TabKey; label: string; icon: Component }> = [
  { key: 'dashboard', label: 'ダッシュボード', icon: LayoutDashboard },
  { key: 'items', label: '在庫一覧', icon: Boxes },
  { key: 'stagnant', label: '滞留在庫', icon: AlertOctagon },
  { key: 'dormant', label: '不動在庫', icon: Snowflake },
]
type TabKey = 'dashboard' | 'items' | 'stagnant' | 'dormant'
type ListTabKey = Exclude<TabKey, 'dashboard'>

function parseTab(value: unknown): TabKey {
  return value === 'items' || value === 'stagnant' || value === 'dormant' ? value : 'dashboard'
}

/** URL がタブ状態の SoT（直リンク・戻る/進むに追従）。 */
const activeTab = computed<TabKey>(() => parseTab(route.query.tab))

function setTab(tab: TabKey): void {
  if (tab === activeTab.value) return
  // タブはビュー状態であり「戻る」の単位にしない（replace で履歴を汚さない）。
  // 既定タブはクエリを外して素の URL を保つ。
  const query = { ...route.query }
  if (tab === 'dashboard') {
    delete query.tab
  } else {
    query.tab = tab
  }
  void router.replace({ query })
}

// ---- mart 構築ガード・ページ初期化エラー ----

const martChecked = ref(false)
const pageError = ref<string | null>(null)

// ---- タブバッジ・最新週の単一参照元 ----
// 「最後に成功したロード」の値だけを参照する（複数レスポンスのフォールバック連鎖にすると、
// フィルタ適用後に旧フィルタの件数が混ざる陳腐化が起きるため。フィルタ適用時に破棄する）。

const lastStatusCounts = ref<InventoryStatusCounts | null>(null)
const lastLatestWeek = ref<string | null>(null)

// ---- ダッシュボード（/api/mart/inventory/actions） ----

const actions = ref<InventoryActionsResponse | null>(null)
const actionsLoading = ref(false)
const actionsError = ref<string | null>(null)
const actionsLoaded = ref(false)
// 連打・高速切替で古い応答が後着しても表示を上書きしないためのリクエスト世代
// （products/[productId].vue・introductions.vue で確立済みのパターン）。
let actionsRequestSeq = 0

async function loadActions(): Promise<void> {
  const seq = ++actionsRequestSeq
  actionsLoading.value = true
  actionsError.value = null
  try {
    const response = await get<InventoryActionsResponse>('/api/mart/inventory/actions', toQuery())
    if (seq !== actionsRequestSeq) return
    actions.value = response
    actionsLoaded.value = true
    lastStatusCounts.value = response.statusCounts
    lastLatestWeek.value = response.latestWeek
  } catch (error) {
    if (seq !== actionsRequestSeq) return
    console.error('[inventory] 在庫アクションサマリーの取得に失敗しました:', error)
    actionsError.value = apiErrorMessage(error)
  } finally {
    if (seq === actionsRequestSeq) {
      actionsLoading.value = false
    }
  }
}

// ---- 明細3タブ（/api/mart/inventory/items 共用） ----

const PAGE_SIZE = 50

interface ListState {
  response: InventoryItemsPage | null
  page: number
  loading: boolean
  error: string | null
  loaded: boolean
}

function emptyListState(): ListState {
  return { response: null, page: 1, loading: false, error: null, loaded: false }
}

const listStates = reactive<Record<ListTabKey, ListState>>({
  items: emptyListState(),
  stagnant: emptyListState(),
  dormant: emptyListState(),
})

// タブ別のリクエスト世代（後着応答の破棄用。リアクティブ不要のため素のオブジェクト）。
const listRequestSeq: Record<ListTabKey, number> = { items: 0, stagnant: 0, dormant: 0 }

/** 在庫一覧タブの状態チップ絞込（null = すべて）。滞留・不動タブは固定絞込のため対象外。 */
const itemsStatusFilter = ref<InventoryStatus | null>(null)
/** 在庫一覧タブの検索（適用ボタン/Enter で確定）。 */
const searchInput = ref('')
const appliedSearch = ref('')

function listQuery(tab: ListTabKey): Record<string, unknown> {
  const query: Record<string, unknown> = {
    ...toQuery(),
    page: listStates[tab].page,
    pageSize: PAGE_SIZE,
  }
  if (tab === 'stagnant') {
    query.statuses = ['stagnant']
  } else if (tab === 'dormant') {
    query.statuses = ['dormant']
  } else {
    if (itemsStatusFilter.value) query.statuses = [itemsStatusFilter.value]
    if (appliedSearch.value) query.search = appliedSearch.value
  }
  return query
}

async function loadList(tab: ListTabKey): Promise<void> {
  const state = listStates[tab]
  const seq = ++listRequestSeq[tab]
  state.loading = true
  state.error = null
  try {
    const page = await get<InventoryItemsPage>('/api/mart/inventory/items', listQuery(tab))
    if (seq !== listRequestSeq[tab]) return
    state.response = page
    state.loaded = true
    // 検索適用中の在庫一覧タブの statusCounts は検索スコープの件数になるため、
    // タブバッジの単一参照元には反映しない（検索なしのキャッシュ済みタブと
    // バッジが矛盾したまま持続するのを防ぐ）。latestWeek は検索非依存のため常に更新する。
    if (tab !== 'items' || !appliedSearch.value) {
      lastStatusCounts.value = page.statusCounts
    }
    lastLatestWeek.value = page.latestWeek
  } catch (error) {
    if (seq !== listRequestSeq[tab]) return
    console.error(`[inventory] 在庫明細（${tab}）の取得に失敗しました:`, error)
    state.error = apiErrorMessage(error)
  } finally {
    if (seq === listRequestSeq[tab]) {
      state.loading = false
    }
  }
}

/** 表示中タブのデータを未取得なら取得する（タブ単位の遅延ロード）。 */
async function ensureTabLoaded(tab: TabKey): Promise<void> {
  if (!isBuilt.value) return
  if (tab === 'dashboard') {
    if (!actionsLoaded.value && !actionsLoading.value) await loadActions()
  } else if (!listStates[tab].loaded && !listStates[tab].loading) {
    await loadList(tab)
  }
}

/**
 * フィルタ適用時: 全タブのキャッシュを無効化し、表示中タブだけ再取得する。
 * バッジ・最新週の単一参照元（lastStatusCounts 等）も破棄し、旧フィルタの件数が
 * 新フィルタの明細と同一画面に混在しないようにする。
 *
 * in-flight の旧フィルタ応答が後着で採用されないよう、リクエスト世代も必ず進める。
 * 世代を進めると旧リクエストの finally は loading を解除しなくなるため、loading の
 * 明示リセットを必ずセットで行う（行わないと ensureTabLoaded の loading ガードで
 * 再取得が永久スキップされ、スピナーが固着する）。
 */
async function handleFilterApply(): Promise<void> {
  actionsRequestSeq += 1
  actionsLoading.value = false
  actionsLoaded.value = false
  lastStatusCounts.value = null
  lastLatestWeek.value = null
  for (const key of ['items', 'stagnant', 'dormant'] as const) {
    listRequestSeq[key] += 1
    listStates[key].loading = false
    listStates[key].loaded = false
    listStates[key].page = 1
  }
  await ensureTabLoaded(activeTab.value)
}

watch(activeTab, (tab) => {
  void ensureTabLoaded(tab)
})

// ---- タブバッジ（状態別件数）。単一参照元 lastStatusCounts から描画する ----

function tabBadge(tab: TabKey): { text: string; hot: boolean } | null {
  const counts = lastStatusCounts.value
  if (!counts) return null
  if (tab === 'items') return { text: `${formatNumber(counts.total)} SKU`, hot: false }
  if (tab === 'stagnant') return { text: formatNumber(counts.stagnant), hot: counts.stagnant > 0 }
  if (tab === 'dormant') return { text: formatNumber(counts.dormant), hot: counts.dormant > 0 }
  return null
}

const latestWeek = computed(() => lastLatestWeek.value)

// ---- ダッシュボードの KPI（前週差はフロント射影） ----

/** 前週差の表示は toFixed(1)。丸めると 0.0 になる半単位未満は「±0.0」として扱う。 */
const DELTA_DISPLAY_EPSILON = 0.05

function deltaText(current: number, previous: number | undefined, unit: 'pt' | '日'): string | undefined {
  if (previous === undefined) return undefined
  const delta = unit === 'pt' ? (current - previous) * 100 : current - previous
  if (Math.abs(delta) < DELTA_DISPLAY_EPSILON) return `前週 ±0.0${unit}`
  return `前週 ${delta > 0 ? '+' : '−'}${Math.abs(delta).toFixed(1)}${unit}`
}

const kpiItems = computed<KpiCardItem[]>(() => {
  const data = actions.value
  if (!data) return []
  const kpi = data.kpi
  const prev = data.previousKpi ?? undefined
  return [
    {
      label: '在庫数',
      value: `${formatNumber(kpi.totalStock)} 点`,
      icon: Boxes,
      accentClass: 'bg-indigo-50 text-indigo-600',
      sub: `健全在庫 ${formatRatioAsPercent(kpi.healthyStockRatio)}`,
    },
    {
      label: '在庫金額（原価）',
      value: formatCurrency(kpi.stockValueCost),
      icon: CircleDollarSign,
      accentClass: 'bg-sky-50 text-sky-600',
      sub: '最新週スナップショット',
    },
    {
      label: '消化率',
      value: formatRatioAsPercent(kpi.sellThroughRate),
      icon: Gauge,
      accentClass: 'bg-teal-50 text-teal-600',
      sub: deltaText(kpi.sellThroughRate, prev?.sellThroughRate, 'pt'),
    },
    {
      label: '平均在庫日数',
      value: `${formatDecimal(kpi.averageStockDays, 1)} 日`,
      icon: CalendarClock,
      accentClass: 'bg-violet-50 text-violet-600',
      sub: deltaText(kpi.averageStockDays, prev?.averageStockDays, '日'),
    },
    {
      label: '滞留・不動の在庫比率',
      value: formatRatioAsPercent(kpi.stagnantDormantStockRatio),
      icon: Scale,
      accentClass: 'bg-rose-50 text-rose-600',
      sub: deltaText(kpi.stagnantDormantStockRatio, prev?.stagnantDormantStockRatio, 'pt'),
    },
  ]
})

function handleFeedNavigate(tab: InventoryActionTargetTab): void {
  setTab(tab)
}

// ---- 明細テーブル ----

const productColumn = { key: 'product', label: '品番 / 単品' } as const
const statusColumn = { key: 'status', label: '状態' } as const
const recommendedColumn = { key: 'recommendedAction', label: '推奨アクション' } as const

function numberColumn(key: keyof InventoryItemRow, label: string, format: (row: InventoryItemRow) => string) {
  return { key, label, align: 'right' as const, format }
}

const stockColumn = numberColumn('stock', '在庫数', (row) => formatNumber(row.stock))
const stockValueColumn = numberColumn('stockValueCost', '在庫金額(原価)', (row) => formatCurrency(row.stockValueCost))
const orderColumn = numberColumn('orderQuantity', '発注残', (row) =>
  row.orderQuantity > 0 ? formatDecimal(row.orderQuantity, 1) : '—')
const sellThroughColumn = numberColumn('sellThroughRate', '消化率', (row) => formatRatioAsPercent(row.sellThroughRate))
const stockDaysColumn = numberColumn('stockDays', '在庫日数', (row) => `${formatDecimal(row.stockDays, 1)} 日`)
const zeroWeeksColumn = numberColumn('zeroSalesWeeks', '出荷ゼロ週数', (row) => `${row.zeroSalesWeeks} 週`)

const listColumns = computed(() => {
  if (activeTab.value === 'stagnant') {
    return [productColumn, stockColumn, stockDaysColumn, sellThroughColumn, orderColumn, recommendedColumn]
  }
  if (activeTab.value === 'dormant') {
    return [productColumn, stockColumn, zeroWeeksColumn, stockValueColumn, orderColumn, recommendedColumn]
  }
  return [productColumn, statusColumn, stockColumn, stockValueColumn, sellThroughColumn, stockDaysColumn, recommendedColumn]
})

function rowKey(row: InventoryItemRow): string {
  // 真のユニークキーは4組（同一 品番×単品 が複数業態で売られると行が分裂する）。
  return `${row.gyotaiCode}|${row.shohinKigou}|${row.hinbanCode}|${row.tanpinCode}`
}

/**
 * 行クリック: 商品マスタと紐付く行は商品詳細分析へ、未紐付け行は品番フィルタを
 * 適用してクロス集計へドリルダウンする（売上分析の行ドリルと同じパターン）。
 */
function handleRowClick(row: InventoryItemRow): void {
  if (row.masterProductId) {
    void navigateTo(`/mart/products/${row.masterProductId}`)
    return
  }
  addToFilter('hinbans', row.hinbanCode)
  void navigateTo({
    path: '/mart/crosstab',
    query: { rowDimension: 'category:product', columnDimension: 'time:year' },
  })
}

// ---- 在庫一覧タブの状態チップ・検索 ----

const statusChips = computed(() => {
  // チップ件数は在庫一覧タブ自身のレスポンス（検索適用後・状態絞込前）だけを参照する。
  // 他タブ由来の件数は検索条件が異なるため混ぜない（未取得時は件数非表示でチップのみ）。
  const counts = listStates.items.response?.statusCounts ?? null
  return [
    { status: null as InventoryStatus | null, label: 'すべて', count: counts?.total ?? null },
    ...INVENTORY_STATUS_ORDER.map((status) => ({
      status: status as InventoryStatus | null,
      label: INVENTORY_STATUSES[status].label,
      count: counts?.[status] ?? null,
    })),
  ]
})

async function applyStatusChip(status: InventoryStatus | null): Promise<void> {
  if (itemsStatusFilter.value === status) return
  itemsStatusFilter.value = status
  listStates.items.page = 1
  await loadList('items')
}

async function applySearch(): Promise<void> {
  if (appliedSearch.value === searchInput.value.trim()) return
  appliedSearch.value = searchInput.value.trim()
  listStates.items.page = 1
  await loadList('items')
}

// ---- ページング ----

function pageInfo(tab: ListTabKey): string {
  const state = listStates[tab]
  const total = state.response?.totalCount ?? 0
  if (total === 0) return '0 件'
  const from = (state.page - 1) * PAGE_SIZE + 1
  const to = Math.min(state.page * PAGE_SIZE, total)
  return `${formatNumber(from)}〜${formatNumber(to)} / ${formatNumber(total)} 件`
}

function hasNextPage(tab: ListTabKey): boolean {
  const state = listStates[tab]
  return state.page * PAGE_SIZE < (state.response?.totalCount ?? 0)
}

async function movePage(tab: ListTabKey, delta: number): Promise<void> {
  const state = listStates[tab]
  const next = state.page + delta
  if (next < 1 || (delta > 0 && !hasNextPage(tab))) return
  state.page = next
  await loadList(tab)
}

// ---- TSV コピー（共有手段。クロス集計と同じ utils/clipboard.ts を使用） ----

const copyState = reactive<Record<ListTabKey, 'idle' | 'copied' | 'failed'>>({
  items: 'idle',
  stagnant: 'idle',
  dormant: 'idle',
})
// フィードバック解除タイマーはタブ別に持つ（共有すると別タブのコピーで clearTimeout され、
// 元タブの「コピーしました」表示が解除されず残留するため）。
const copyTimers: Partial<Record<ListTabKey, ReturnType<typeof setTimeout>>> = {}

async function copyList(tab: ListTabKey): Promise<void> {
  const rows = listStates[tab].response?.items ?? []
  if (rows.length === 0) return

  const headers = [
    '業態', '商品記号', '品番', '単品', '商品名', '状態', '在庫数', '在庫金額(原価)',
    '発注残', '先付数', '消化率(%)', '在庫日数', '出荷ゼロ週数', '推奨アクション',
  ]
  const cells = rows.map((row) => [
    row.gyotaiCode, row.shohinKigou, row.hinbanCode, row.tanpinCode,
    row.productName ?? row.hinmei, INVENTORY_STATUSES[row.status].label,
    String(row.stock), String(row.stockValueCost),
    String(row.orderQuantity), String(row.advanceQuantity),
    (row.sellThroughRate * 100).toFixed(1), row.stockDays.toFixed(1),
    String(row.zeroSalesWeeks),
    row.recommendedAction ? RECOMMENDED_ACTIONS[row.recommendedAction].label : '',
  ])
  const text = [headers, ...cells].map((line) => line.join('\t')).join('\n')
  const escapeHtml = (value: string) =>
    value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')
  const html = `<table><tr>${headers.map((h) => `<th>${escapeHtml(h)}</th>`).join('')}</tr>${cells
    .map((line) => `<tr>${line.map((cell) => `<td>${escapeHtml(cell)}</td>`).join('')}</tr>`)
    .join('')}</table>`

  const ok = await copyHtmlToClipboard(html, text)
  copyState[tab] = ok ? 'copied' : 'failed'
  const previous = copyTimers[tab]
  if (previous) clearTimeout(previous)
  copyTimers[tab] = setTimeout(() => {
    copyState[tab] = 'idle'
  }, ok ? 2000 : 3000)
}

onBeforeUnmount(() => {
  for (const timer of Object.values(copyTimers)) {
    if (timer) clearTimeout(timer)
  }
})

// ---- 初期化 ----

onMounted(async () => {
  try {
    await loadOptions()
    await refreshStatus()
    // 各タブのロードは内部で catch するため、ここに到達する例外は初期化（ステータス確認等）のみ。
    await ensureTabLoaded(activeTab.value)
  } catch (error) {
    console.error('[inventory] ページ初期化に失敗しました:', error)
    pageError.value = apiErrorMessage(error)
  } finally {
    // 失敗時も StatusBlock の error 表示へ切り替える（無言の永久ローディングにしない）。
    martChecked.value = true
  }
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">在庫マネジメント</h1>
      <p class="text-sm text-slate-500">
        滞留・不動在庫を自動抽出し、発注抑制・値下げ・売場再点検の判断につなげます
        （分析 mart の最新取込週スナップショット基準<template v-if="latestWeek">: 最新取込週 {{ latestWeek }}</template>）。
      </p>
    </div>

    <FilterBar :scope-key="MART_SCOPE" @apply="handleFilterApply" />

    <StatusBlock :loading="!martChecked" :error="pageError">
      <MartNotBuiltNotice v-if="!isBuilt" />
      <div v-else class="space-y-4">
        <!-- ページ内タブ（?tab= 同期） -->
        <nav class="border-b border-slate-200" aria-label="在庫マネジメントのビュー切替">
          <ul class="scrollbar-hide -mb-px flex gap-1 overflow-x-auto">
            <li v-for="tab in TABS" :key="tab.key" class="shrink-0">
              <button
                type="button"
                class="flex items-center gap-1.5 whitespace-nowrap border-b-2 px-3 py-2.5 text-sm font-medium transition-colors"
                :class="
                  activeTab === tab.key
                    ? 'border-indigo-600 text-indigo-700'
                    : 'border-transparent text-slate-500 hover:border-slate-300 hover:text-slate-800'
                "
                :aria-current="activeTab === tab.key ? 'page' : undefined"
                @click="setTab(tab.key)"
              >
                <component :is="tab.icon" class="h-4 w-4 shrink-0" />
                {{ tab.label }}
                <span
                  v-if="tabBadge(tab.key)"
                  class="rounded-full px-1.5 py-0.5 text-[10px] font-bold"
                  :class="tabBadge(tab.key)!.hot ? 'bg-rose-100 text-rose-700' : 'bg-slate-100 text-slate-500'"
                >
                  {{ tabBadge(tab.key)!.text }}
                </span>
              </button>
            </li>
          </ul>
        </nav>

        <!-- ============ ダッシュボード ============ -->
        <section v-if="activeTab === 'dashboard'" aria-label="ダッシュボード">
          <StatusBlock
            :loading="actionsLoading"
            :error="actionsError"
            :empty="!actions || !actions.latestWeek"
            empty-message="該当する在庫データがありません。"
          >
            <div v-if="actions" class="space-y-4">
              <div class="grid grid-cols-2 gap-3 lg:grid-cols-3 xl:grid-cols-5">
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

              <div class="grid grid-cols-1 gap-4 xl:grid-cols-2">
                <div class="rounded-xl border border-slate-200 bg-white p-4">
                  <div class="mb-1 flex items-baseline justify-between gap-2">
                    <h2 class="text-sm font-bold text-slate-800">今週のアクション</h2>
                    <span class="text-xs text-slate-400">取込データから自動抽出</span>
                  </div>
                  <InventoryActionFeed :actions="actions.actions" @navigate="handleFeedNavigate" />
                </div>

                <div class="rounded-xl border border-slate-200 bg-white p-4">
                  <InventoryQuadrantChart
                    :departments="actions.byDepartment"
                    :thresholds="actions.thresholds"
                  />
                </div>
              </div>

              <div class="rounded-xl border border-slate-200 bg-white p-4">
                <div class="mb-2 flex items-baseline justify-between gap-2">
                  <h2 class="text-sm font-bold text-slate-800">部門別 在庫の鮮度</h2>
                  <button
                    type="button"
                    class="text-xs font-medium text-indigo-600 hover:text-indigo-800"
                    @click="setTab('items')"
                  >
                    在庫一覧で詳しく見る →
                  </button>
                </div>
                <InventoryFreshnessStrips :departments="actions.byDepartment" />
              </div>
            </div>
          </StatusBlock>
        </section>

        <!-- ============ 明細タブ（在庫一覧 / 滞留 / 不動） ============ -->
        <section v-else aria-label="在庫明細">
          <div class="space-y-3">
            <!-- 定義バナー（滞留・不動のみ） -->
            <InventoryDefinitionBanner
              v-if="activeTab !== 'items' && listStates[activeTab].response"
              :variant="activeTab"
              :thresholds="listStates[activeTab].response!.thresholds"
            />

            <!-- 経過バケット（滞留・不動のみ） -->
            <InventoryAgingBuckets
              v-if="activeTab === 'stagnant' && listStates.stagnant.response"
              :boundaries="listStates.stagnant.response!.thresholds.stagnantAgingBoundaries"
              :counts="listStates.stagnant.response!.stagnantAgingCounts"
              unit="日"
              :bar-classes="['bg-amber-400', 'bg-rose-500', 'bg-rose-700']"
            />
            <InventoryAgingBuckets
              v-if="activeTab === 'dormant' && listStates.dormant.response"
              :boundaries="listStates.dormant.response!.thresholds.dormantAgingBoundaries"
              :counts="listStates.dormant.response!.dormantAgingCounts"
              unit="週"
              :bar-classes="['bg-slate-400', 'bg-slate-500', 'bg-slate-700']"
            />

            <!-- 在庫一覧タブ: 状態チップ + 検索 -->
            <div v-if="activeTab === 'items'" class="flex flex-wrap items-center gap-2">
              <button
                v-for="chip in statusChips"
                :key="chip.status ?? 'all'"
                type="button"
                class="inline-flex items-center gap-1.5 rounded-full border px-3 py-1.5 text-xs font-medium transition-colors"
                :class="
                  itemsStatusFilter === chip.status
                    ? 'border-indigo-500 bg-indigo-50 text-indigo-700'
                    : 'border-slate-200 bg-white text-slate-600 hover:border-indigo-300'
                "
                @click="applyStatusChip(chip.status)"
              >
                {{ chip.label }}
                <span v-if="chip.count !== null" class="font-bold tabular-nums">
                  {{ formatNumber(chip.count) }}
                </span>
              </button>
              <form class="ml-auto flex items-center gap-2" @submit.prevent="applySearch">
                <label class="relative block">
                  <Search
                    class="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-slate-400"
                  />
                  <input
                    v-model="searchInput"
                    type="search"
                    placeholder="品番・商品名・単品・ブランド"
                    class="w-56 rounded-lg border border-slate-200 bg-white py-1.5 pl-8 pr-2.5 text-xs text-slate-700 placeholder:text-slate-400 focus:border-indigo-400 focus:outline-none"
                  />
                </label>
                <button
                  type="submit"
                  class="rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-xs font-medium text-slate-600 hover:border-indigo-300 hover:text-indigo-700"
                >
                  検索
                </button>
              </form>
            </div>

            <StatusBlock
              :loading="listStates[activeTab].loading"
              :error="listStates[activeTab].error"
              :empty="(listStates[activeTab].response?.items.length ?? 0) === 0"
              :empty-message="
                activeTab === 'stagnant'
                  ? '滞留在庫はありません。'
                  : activeTab === 'dormant'
                    ? '不動在庫はありません。'
                    : '該当する在庫データがありません。'
              "
            >
              <div class="space-y-3">
                <div class="flex items-center justify-between gap-2">
                  <p class="text-xs text-slate-400">{{ pageInfo(activeTab) }}</p>
                  <button
                    type="button"
                    class="inline-flex items-center gap-1.5 rounded-lg border px-2.5 py-1 text-xs transition-colors"
                    :class="
                      copyState[activeTab] === 'copied'
                        ? 'border-emerald-300 bg-emerald-50 text-emerald-700'
                        : copyState[activeTab] === 'failed'
                          ? 'border-rose-300 bg-rose-50 text-rose-700'
                          : 'border-slate-300 bg-white text-slate-600 hover:bg-slate-50'
                    "
                    @click="copyList(activeTab)"
                  >
                    <ClipboardCopy class="h-3.5 w-3.5" />
                    {{
                      copyState[activeTab] === 'copied'
                        ? 'コピーしました'
                        : copyState[activeTab] === 'failed'
                          ? 'コピーに失敗しました'
                          : '表示中のページをコピー'
                    }}
                  </button>
                </div>

                <DataTable
                  :columns="listColumns"
                  :rows="listStates[activeTab].response?.items ?? []"
                  :row-key="rowKey"
                  clickable
                  @row-click="handleRowClick"
                >
                  <template #product="{ row }">
                    <span class="font-bold text-slate-800">
                      {{ (row as InventoryItemRow).hinbanCode }}-{{ (row as InventoryItemRow).tanpinCode }}
                    </span>
                    <span class="mt-0.5 block text-xs font-normal text-slate-400">
                      {{ (row as InventoryItemRow).productName ?? (row as InventoryItemRow).hinmei }}
                    </span>
                  </template>
                  <template #status="{ row }">
                    <SkuStatusBadge :status="(row as InventoryItemRow).status" />
                  </template>
                  <template #recommendedAction="{ row }">
                    <template v-if="(row as InventoryItemRow).recommendedAction">
                      <span
                        class="text-xs font-medium text-slate-700"
                        :title="RECOMMENDED_ACTIONS[(row as InventoryItemRow).recommendedAction!].description"
                      >
                        {{ RECOMMENDED_ACTIONS[(row as InventoryItemRow).recommendedAction!].label }}
                      </span>
                      <NuxtLink
                        v-if="RECOMMENDED_ACTIONS[(row as InventoryItemRow).recommendedAction!].to"
                        :to="RECOMMENDED_ACTIONS[(row as InventoryItemRow).recommendedAction!].to!"
                        class="ml-1.5 text-xs font-medium text-indigo-600 hover:text-indigo-800"
                        @click.stop
                      >
                        確認 →
                      </NuxtLink>
                    </template>
                    <span v-else class="text-xs text-slate-300">—</span>
                  </template>
                </DataTable>

                <div
                  v-if="(listStates[activeTab].response?.totalCount ?? 0) > PAGE_SIZE"
                  class="flex items-center justify-center gap-3"
                >
                  <button
                    type="button"
                    class="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:border-indigo-300 disabled:cursor-not-allowed disabled:opacity-40"
                    :disabled="listStates[activeTab].page <= 1"
                    @click="movePage(activeTab, -1)"
                  >
                    ← 前へ
                  </button>
                  <span class="text-xs text-slate-500">{{ pageInfo(activeTab) }}</span>
                  <button
                    type="button"
                    class="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:border-indigo-300 disabled:cursor-not-allowed disabled:opacity-40"
                    :disabled="!hasNextPage(activeTab)"
                    @click="movePage(activeTab, 1)"
                  >
                    次へ →
                  </button>
                </div>

                <p v-if="activeTab === 'stagnant'" class="text-xs text-slate-400">
                  「発注残があるのに滞留している」行が最優先です（仕入れを止めるだけで改善します）。
                  推奨アクションはルールベースの自動判定で、最終判断は担当者が行ってください。
                </p>
                <p v-else-if="activeTab === 'dormant'" class="text-xs text-slate-400">
                  在庫金額は原価ベースの概算です（原価未設定の SKU は 0 円として加算）。
                  長期不動の SKU は処分・値引き販売の判断対象として週次レビューでの確認を推奨します。
                </p>
              </div>
            </StatusBlock>
          </div>
        </section>
      </div>
    </StatusBlock>
  </div>
</template>
