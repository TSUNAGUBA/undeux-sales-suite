<script setup lang="ts">
/**
 * 商品詳細分析（/mart/item-detail）— アイテム分析タブ。
 *
 * 視点はバイヤー＝部門、商品軸は商品記号≒7桁（品番3桁＋単品4桁）。SKU × 週の明細を
 *   - 週別: 週を列展開し、SKU ごとに「売数／在庫数／在日」の3行で表示
 *   - 当週: 売上参照ファイルのレイアウトに倣い最新週スナップショット＋SKU情報を1行で表示
 * の2モードで切り替える。工夫要素:
 *   ① 在日の色分け（〜30：赤／31-60：青／61〜：既定）
 *   ② 売価変更（前週比で売価が下がった＝値下げ）週のセルを強調
 *   ④ 表の上に表示範囲のサマリーを表示
 *   ⑤ 表示中の表をクリップボードへコピー（Excel 貼り付け用）
 *   ⑥ 週別／当週の表示モード
 *   ・消化率（累計／プロパー／値下げ）の分解
 * ③ チラシは現状データに無いため未実装。
 *
 * フィルターは要件の4種（⓪業態・部門、①品名 部分一致、②商品記号 部分一致、③品番3桁）＋期間（年度）。
 * データ源は分析 mart（/api/mart/item-detail）。未構築時は MartNotBuiltNotice を表示する。
 * 順位・値下げ検出・消化率分解・サマリー・コピーはフロント側の表示射影（SoT は集計素材）。
 */
import { Check, ClipboardCopy, RotateCcw, Search } from 'lucide-vue-next'
import type { ItemDetailResponse } from '~/types/api'
import type { ItemDetailRowCategory, ItemDetailWeeklyBreakdown } from '~/utils/itemDetail'

useHead({ title: '商品詳細分析 | UndeuxSales' })

const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()
// 業態・部門・年度の選択肢だけを共有（/api/filters）から読む。共有フィルタ state は変更しない。
const { options, optionsError, loadOptions, years } = useFilters('mart-filter')

// ---------------------------------------------------------------
// フィルタ state（画面専用・ローカル）
// ---------------------------------------------------------------
const businessTypes = ref<string[]>([])
const departments = ref<string[]>([])
const productName = ref('')
const productSign = ref('')
const productCode = ref('')
const tanawari1 = ref('')
const tanawari2 = ref('')
const year = ref<number | null>(null)

// 品名 AND・棚割の適用値（画面表示のクライアント側フィルタ基準）。適用ボタンで確定する。
const appliedProductName = ref('')
const appliedTanawari1 = ref('')
const appliedTanawari2 = ref('')

const mode = ref<ItemDetailMode>('weekly')

const raw = ref<ItemDetailResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

/** 品名フィルタ入力をトークン分割（カンマ・スペース・読点区切り、空要素除去）。 */
function nameTokens(value: string): string[] {
  return value.split(/[\s,、，]+/).map((t) => t.trim()).filter(Boolean)
}

const fullView = computed(() => (raw.value ? buildItemDetailView(raw.value) : null))

/**
 * 表示ビュー: 品名（全トークンを含む AND 部分一致）・棚割1/棚割2（部分一致）で行を絞る。
 * 棚割はレスポンス未提供のためモック値（buildItemDetailView が付与）に対して絞り込む。
 */
const view = computed(() => {
  const v = fullView.value
  if (!v) return null
  const tokens = nameTokens(appliedProductName.value)
  const t1 = appliedTanawari1.value
  const t2 = appliedTanawari2.value
  if (tokens.length === 0 && !t1 && !t2) return v
  const rows = v.rows.filter(
    (r) =>
      tokens.every((t) => r.hinmei.includes(t))
      && (!t1 || r.tanawari1.includes(t1))
      && (!t2 || r.tanawari2.includes(t2)),
  )
  return { weeks: v.weeks, rows, latestWeek: v.latestWeek, truncated: v.truncated }
})
const summary = computed(() => computeItemDetailSummary(view.value?.rows ?? []))
const summaryFmt = computed(() => formatSummary(summary.value))

// 当週ビューの日別売数（月〜日）・直近4週売数。行キーで一度だけ算出し各セルから参照する。
const weeklyBreakdowns = computed<Map<string, ItemDetailWeeklyBreakdown>>(
  () => new Map((view.value?.rows ?? []).map((r) => [r.key, itemDetailWeeklyBreakdown(r)])),
)
const EMPTY_BREAKDOWN: ItemDetailWeeklyBreakdown = { daily: [0, 0, 0, 0, 0, 0, 0], prior: [null, null, null, null] }
function breakdownOf(key: string): ItemDetailWeeklyBreakdown {
  return weeklyBreakdowns.value.get(key) ?? EMPTY_BREAKDOWN
}

function buildQuery(): Record<string, unknown> {
  // 週別マトリクスの DOM を抑えるため既定 100 SKU（フィルタで絞り込む運用）。
  const query: Record<string, unknown> = { limit: 100 }
  if (businessTypes.value.length > 0) query.businessTypes = businessTypes.value
  if (departments.value.length > 0) query.departments = departments.value
  // 品名 AND はクライアント側で行うため、サーバへは先頭トークンのみ渡して母集団を粗く絞る。
  const tokens = nameTokens(productName.value)
  if (tokens.length > 0) query.productName = tokens[0]
  if (productSign.value.trim()) query.productSign = productSign.value.trim()
  if (productCode.value.trim()) query.productCode = productCode.value.trim()
  // 棚割はレスポンス未提供（モック）のためクライアント側で絞る。サーバへは送らない。
  if (year.value !== null) {
    query.from = `${year.value}-01-01`
    query.to = `${year.value}-12-31`
  }
  return query
}

let loadSeq = 0

async function load(): Promise<void> {
  const seq = ++loadSeq
  loading.value = true
  errorMessage.value = null
  // クライアント側フィルタ（品名 AND・棚割）の適用値を確定する。
  appliedProductName.value = productName.value
  appliedTanawari1.value = tanawari1.value.trim()
  appliedTanawari2.value = tanawari2.value.trim()
  try {
    await refreshStatus()
    if (seq !== loadSeq) return
    if (!isBuilt.value) {
      raw.value = null
      return
    }
    const response = await get<ItemDetailResponse>('/api/mart/item-detail', buildQuery())
    if (seq !== loadSeq) return
    raw.value = response
  } catch (error) {
    if (seq !== loadSeq) return
    errorMessage.value = apiErrorMessage(error)
  } finally {
    if (seq === loadSeq) {
      loading.value = false
    }
  }
}

function reload(): void {
  void load()
}

function resetFilters(): void {
  businessTypes.value = []
  departments.value = []
  productName.value = ''
  productSign.value = ''
  productCode.value = ''
  tanawari1.value = ''
  tanawari2.value = ''
  reload()
}

// ---------------------------------------------------------------
// 行区分（売数・在庫数・在日・販売価格・気温[東京/札幌/沖縄]）の表示/非表示
// デフォルト全表示。最終表示条件は localStorage に保存し次回再現する（週別マトリクスに適用）。
// ---------------------------------------------------------------
const ROW_CATEGORY_STORAGE_KEY = 'undeux.itemDetail.rowCategories'
const ALL_CATEGORY_KEYS = ITEM_DETAIL_ROW_CATEGORIES.map((c) => c.key)

function loadCategoryVisibility(): Record<ItemDetailRowCategory, boolean> {
  const all = Object.fromEntries(ALL_CATEGORY_KEYS.map((k) => [k, true])) as Record<ItemDetailRowCategory, boolean>
  if (import.meta.server) return all
  try {
    const rawValue = window.localStorage.getItem(ROW_CATEGORY_STORAGE_KEY)
    if (!rawValue) return all
    const parsed = JSON.parse(rawValue) as Record<string, unknown>
    for (const k of ALL_CATEGORY_KEYS) {
      if (typeof parsed[k] === 'boolean') all[k] = parsed[k]
    }
  } catch {
    // 破損した保存値は無視して全表示にフォールバック（非ブロッキング）。
  }
  return all
}

const categoryVisible = ref<Record<ItemDetailRowCategory, boolean>>(loadCategoryVisibility())

/** 表示中の行区分（最低1つは残す＝全OFFで空表示にしない）。 */
const visibleCategories = computed<ItemDetailRowCategory[]>(() => {
  const list = ALL_CATEGORY_KEYS.filter((k) => categoryVisible.value[k])
  return list.length > 0 ? list : ['quantity']
})

function toggleCategory(key: ItemDetailRowCategory): void {
  categoryVisible.value = { ...categoryVisible.value, [key]: !categoryVisible.value[key] }
}

watch(
  categoryVisible,
  (val) => {
    if (import.meta.server) return
    try {
      window.localStorage.setItem(ROW_CATEGORY_STORAGE_KEY, JSON.stringify(val))
    } catch {
      // 保存不可（プライベートモード等）でも表示切替は機能させる（非ブロッキング）。
    }
  },
  { deep: true },
)

function onBusinessTypesChange(codes: string[]): void {
  businessTypes.value = codes
}
function onDepartmentsChange(codes: string[]): void {
  departments.value = codes
}

// ---------------------------------------------------------------
// クリップボードコピー（Excel 貼り付け用）
// ---------------------------------------------------------------
const copied = ref(false)
const copyFailed = ref(false)
let feedbackTimer: ReturnType<typeof setTimeout> | null = null

function showFeedback(success: boolean): void {
  if (feedbackTimer) clearTimeout(feedbackTimer)
  copied.value = success
  copyFailed.value = !success
  feedbackTimer = setTimeout(() => {
    copied.value = false
    copyFailed.value = false
  }, success ? 2000 : 3000)
}

async function copyTable(): Promise<void> {
  const v = view.value
  if (!v || v.rows.length === 0) return
  const { html, text } =
    mode.value === 'weekly'
      ? buildWeeklyClipboard(v.weeks, v.rows, visibleCategories.value)
      : buildCurrentClipboard(v.rows)
  const ok = await copyHtmlToClipboard(html, text)
  showFeedback(ok)
}

onBeforeUnmount(() => {
  if (feedbackTimer) clearTimeout(feedbackTimer)
})

onMounted(async () => {
  await loadOptions()
  // 週数を抑えるため既定は最新の利用可能年度。
  year.value = years.value.length > 0 ? years.value[years.value.length - 1]! : null
  await load()
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">商品詳細分析</h1>
      <p class="text-sm text-slate-500">
        SKU（品番3桁×単品4桁）単位で「売数／在庫数／在日」を週別・当週で分析します。
        在日の色分け、値下げ（売価変更）週の強調、消化率（累計／プロパー／値下げ）の分解を行い、
        表示中の表は Excel 貼り付け用にコピーできます。
      </p>
    </div>

    <CollapsiblePanel title="フィルター" :default-open="true">
      <p v-if="optionsError" class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700">
        フィルタ選択肢の取得に失敗しました: {{ optionsError }}
      </p>

      <!-- ⓪ 業態・部門（複数選択） -->
      <ScopeFilterTags
        :business-types="options?.businessTypes ?? []"
        :departments="options?.departments ?? []"
        :selected-business-types="businessTypes"
        :selected-departments="departments"
        multiple
        @update:selected-business-types="onBusinessTypesChange"
        @update:selected-departments="onDepartmentsChange"
      />

      <!-- ①②③ 品名（AND部分一致）・商品記号（部分一致）・品番3桁（完全一致）・棚割1/2・期間 -->
      <div class="mt-3 grid grid-cols-1 gap-3 border-t border-dashed border-slate-200 pt-3 sm:grid-cols-2 lg:grid-cols-3">
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">品名（部分一致・AND）</label>
          <input
            v-model="productName"
            type="search"
            placeholder="例: デニム 黒（スペース/カンマ区切りで全て含む）"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
            @keydown.enter.prevent="reload"
          >
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">商品記号（部分一致）</label>
          <input
            v-model="productSign"
            type="search"
            placeholder="例: LG3362"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
            @keydown.enter.prevent="reload"
          >
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">品番3桁（完全一致）</label>
          <input
            v-model="productCode"
            type="search"
            placeholder="例: 558"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
            @keydown.enter.prevent="reload"
          >
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">棚割1（部分一致）</label>
          <input
            v-model="tanawari1"
            type="search"
            placeholder="例: A01"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
            @keydown.enter.prevent="reload"
          >
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">棚割2（部分一致）</label>
          <input
            v-model="tanawari2"
            type="search"
            placeholder="例: L1"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
            @keydown.enter.prevent="reload"
          >
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">期間（年度）</label>
          <select
            v-model="year"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
          >
            <option :value="null">全期間</option>
            <option v-for="y in years" :key="y" :value="y">{{ y }}年</option>
          </select>
        </div>
      </div>

      <div class="mt-4 flex flex-wrap gap-2">
        <button
          type="button"
          class="flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-60"
          :disabled="loading"
          @click="reload"
        >
          <Search class="h-4 w-4" />
          適用
        </button>
        <button
          type="button"
          class="flex items-center gap-1.5 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 disabled:opacity-60"
          :disabled="loading"
          @click="resetFilters"
        >
          <RotateCcw class="h-4 w-4" />
          リセット
        </button>
      </div>
    </CollapsiblePanel>

    <StatusBlock :loading="loading" :error="errorMessage">
      <MartNotBuiltNotice v-if="!isBuilt" />
      <div v-else class="space-y-3">
        <!-- ④ サマリー（表示範囲） -->
        <div class="grid grid-cols-2 gap-3 md:grid-cols-3 lg:grid-cols-6">
          <div class="rounded-xl border border-slate-200 bg-white p-3">
            <p class="text-xs text-slate-500">対象SKU数</p>
            <p class="mt-0.5 text-lg font-bold text-slate-800">{{ formatNumber(summary.skuCount) }}</p>
          </div>
          <div class="rounded-xl border border-slate-200 bg-white p-3">
            <p class="text-xs text-slate-500">合計売数（期間）</p>
            <p class="mt-0.5 text-lg font-bold text-slate-800">{{ summaryFmt.totalQuantity }}</p>
          </div>
          <div class="rounded-xl border border-slate-200 bg-white p-3">
            <p class="text-xs text-slate-500">合計在庫数（最新週）</p>
            <p class="mt-0.5 text-lg font-bold text-slate-800">{{ summaryFmt.totalStock }}</p>
          </div>
          <div class="rounded-xl border border-slate-200 bg-white p-3">
            <p class="text-xs text-slate-500">平均在日</p>
            <p class="mt-0.5 text-lg font-bold text-slate-800">{{ summaryFmt.averageStockDays }}</p>
          </div>
          <div class="rounded-xl border border-slate-200 bg-white p-3">
            <p class="text-xs text-slate-500">平均消化率</p>
            <p class="mt-0.5 text-lg font-bold text-slate-800">{{ summaryFmt.averageSellThrough }}</p>
          </div>
          <div class="rounded-xl border border-slate-200 bg-white p-3">
            <p class="text-xs text-slate-500">値下げSKU数</p>
            <p class="mt-0.5 text-lg font-bold text-slate-800">{{ formatNumber(summary.markdownSkuCount) }}</p>
          </div>
        </div>

        <!-- ⑥ 表示モード ＋ ⑤ コピー ＋ 凡例 -->
        <div class="flex flex-wrap items-center justify-between gap-2">
          <div class="inline-flex overflow-hidden rounded-lg border border-slate-300">
            <button
              type="button"
              class="px-3 py-1.5 text-sm"
              :class="mode === 'weekly' ? 'bg-indigo-600 text-white' : 'bg-white text-slate-600'"
              @click="mode = 'weekly'"
            >
              週別
            </button>
            <button
              type="button"
              class="px-3 py-1.5 text-sm"
              :class="mode === 'current' ? 'bg-indigo-600 text-white' : 'bg-white text-slate-600'"
              @click="mode = 'current'"
            >
              当週
            </button>
          </div>

          <div class="flex flex-wrap items-center gap-3">
            <div class="flex items-center gap-2 text-xs text-slate-400">
              <span class="text-rose-600">在日〜30</span>
              <span class="text-sky-600">31-60</span>
              <span class="text-slate-600">61〜</span>
              <span class="inline-flex items-center gap-1">
                <span class="inline-block h-3 w-3 rounded-sm bg-rose-100" />値下げ週
              </span>
            </div>
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
              :disabled="(view?.rows.length ?? 0) === 0"
              @click="copyTable"
            >
              <Check v-if="copied" class="h-3.5 w-3.5" />
              <ClipboardCopy v-else class="h-3.5 w-3.5" />
              {{ copied ? 'コピーしました' : copyFailed ? 'コピーに失敗' : 'コピー' }}
            </button>
          </div>
        </div>

        <!-- 行区分の表示/非表示（週別のみ。最終状態は localStorage に保存） -->
        <div
          v-if="mode === 'weekly'"
          class="flex flex-wrap items-center gap-x-3 gap-y-1.5 rounded-lg border border-slate-200 bg-slate-50/60 px-3 py-2"
        >
          <span class="text-xs font-medium text-slate-500">行区分:</span>
          <label
            v-for="c in ITEM_DETAIL_ROW_CATEGORIES"
            :key="c.key"
            class="inline-flex cursor-pointer items-center gap-1 text-xs text-slate-600"
          >
            <input
              type="checkbox"
              class="accent-indigo-600"
              :checked="categoryVisible[c.key]"
              @change="toggleCategory(c.key)"
            >
            {{ c.label }}
          </label>
        </div>

        <p v-if="view?.truncated" class="rounded bg-amber-50 px-2 py-1 text-xs text-amber-700">
          対象 SKU が多いため上限件数で打ち切りました。フィルタで絞り込んでください。
        </p>

        <div
          v-if="(view?.rows.length ?? 0) === 0"
          class="rounded-xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-400"
        >
          該当する SKU がありません。フィルタを見直してください。
        </div>

        <!-- 週別マトリクス（メタ列＋行区分＋週。表示中の行区分のみ描画） -->
        <ItemDetailWeeklyTable
          v-else-if="mode === 'weekly'"
          :weeks="view!.weeks"
          :rows="view!.rows"
          :visible-categories="visibleCategories"
        />

        <!-- 当週テーブル（売上参照ファイル準拠・1 SKU 1 行） -->
        <div v-else class="overflow-auto rounded-xl border border-slate-200 bg-white">
          <table class="w-full text-sm">
            <thead class="text-slate-500">
              <tr>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-left font-medium">品番CD</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-left font-medium">単品CD</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-left font-medium">記号</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-left font-medium">品名</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-left font-medium">カラー</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-left font-medium">サイズ</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-right font-medium">上代</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-left font-medium">導入日</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-right font-medium">売数</th>
                <th
                  v-for="d in ['月', '火', '水', '木', '金', '土', '日']"
                  :key="`dh-${d}`"
                  class="whitespace-nowrap border-l border-slate-100 bg-emerald-50 px-2 py-2 text-right font-medium text-emerald-700"
                >{{ d }}</th>
                <th class="whitespace-nowrap border-l border-slate-200 bg-slate-50 px-3 py-2 text-right font-medium">前週</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-right font-medium">2週前</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-right font-medium">3週前</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-right font-medium">4週前</th>
                <th class="whitespace-nowrap border-l border-slate-200 bg-slate-50 px-3 py-2 text-right font-medium">在庫数</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-right font-medium">在日</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-right font-medium">消化率</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-right font-medium">プロパー消化率</th>
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-right font-medium">値下げ消化率</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-slate-100">
              <tr v-for="row in view!.rows" :key="row.key" class="hover:bg-slate-50">
                <td class="whitespace-nowrap px-3 py-2 font-mono text-slate-700">{{ row.hinbanCode }}</td>
                <td class="whitespace-nowrap px-3 py-2 font-mono text-slate-700">{{ row.tanpinCode }}</td>
                <td class="whitespace-nowrap px-3 py-2 font-mono text-slate-500">{{ row.shohinKigou }}</td>
                <td class="max-w-[200px] truncate px-3 py-2 text-slate-700" :title="row.hinmei">{{ row.hinmei || '—' }}</td>
                <td class="whitespace-nowrap px-3 py-2 text-slate-600">{{ row.colorName || '—' }}</td>
                <td class="whitespace-nowrap px-3 py-2 text-slate-600">{{ row.sizeName || '—' }}</td>
                <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-600">{{ formatCurrency(row.listPrice) }}</td>
                <td class="whitespace-nowrap px-3 py-2 text-slate-600">{{ formatDate(row.donyuDate) }}</td>
                <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-700">
                  {{ row.latest ? formatNumber(row.latest.quantity) : '—' }}
                </td>
                <td
                  v-for="(d, di) in breakdownOf(row.key).daily"
                  :key="`d-${di}`"
                  class="whitespace-nowrap border-l border-slate-100 bg-emerald-50/40 px-2 py-2 text-right tabular-nums text-slate-600"
                >{{ formatNumber(d) }}</td>
                <td class="whitespace-nowrap border-l border-slate-200 px-3 py-2 text-right tabular-nums text-slate-600">
                  {{ breakdownOf(row.key).prior[0] === null ? '—' : formatNumber(breakdownOf(row.key).prior[0]!) }}
                </td>
                <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-600">
                  {{ breakdownOf(row.key).prior[1] === null ? '—' : formatNumber(breakdownOf(row.key).prior[1]!) }}
                </td>
                <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-600">
                  {{ breakdownOf(row.key).prior[2] === null ? '—' : formatNumber(breakdownOf(row.key).prior[2]!) }}
                </td>
                <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-600">
                  {{ breakdownOf(row.key).prior[3] === null ? '—' : formatNumber(breakdownOf(row.key).prior[3]!) }}
                </td>
                <td class="whitespace-nowrap border-l border-slate-200 px-3 py-2 text-right tabular-nums text-slate-700">
                  {{ row.latest ? formatNumber(row.latest.stock) : '—' }}
                </td>
                <td
                  class="whitespace-nowrap px-3 py-2 text-right tabular-nums"
                  :class="stockDaysColorClass(row.latest?.stockDays ?? null)"
                >
                  {{ (row.latest?.stockDays ?? 0) > 0 ? formatDecimal(row.latest!.stockDays, 1) : '—' }}
                </td>
                <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-700">
                  {{ row.overallSellThrough !== null ? formatRatioAsPercent(row.overallSellThrough) : '—' }}
                </td>
                <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-emerald-700">
                  {{ row.properSellThrough !== null ? formatRatioAsPercent(row.properSellThrough) : '—' }}
                </td>
                <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-rose-700">
                  {{ row.markdownSellThrough !== null ? formatRatioAsPercent(row.markdownSellThrough) : '—' }}
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <p class="text-xs text-slate-400">
          月〜日は当週売数の日別内訳（売上参照ファイルの当週日別が item-detail レスポンス未提供のため、
          当週売数を決定的に按分したモック。合計は売数に一致）。前週〜4週前は週次履歴の実売数です。
          在日は最新週基準の平均。消化率は累計売上数÷累計納品数。プロパー／値下げ消化率は初回値下げ週の
          前後で売れた数量比により累計消化率を按分した近似値です。チラシ連動は現状データに無いため未実装です。
        </p>
      </div>
    </StatusBlock>
  </div>
</template>
