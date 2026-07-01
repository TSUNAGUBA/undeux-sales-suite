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
const year = ref<number | null>(null)

const mode = ref<ItemDetailMode>('weekly')

const raw = ref<ItemDetailResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const view = computed(() => (raw.value ? buildItemDetailView(raw.value) : null))
const summary = computed(() => computeItemDetailSummary(view.value?.rows ?? []))
const summaryFmt = computed(() => formatSummary(summary.value))

function buildQuery(): Record<string, unknown> {
  // 週別マトリクスの DOM を抑えるため既定 100 SKU（フィルタで絞り込む運用）。
  const query: Record<string, unknown> = { limit: 100 }
  if (businessTypes.value.length > 0) query.businessTypes = businessTypes.value
  if (departments.value.length > 0) query.departments = departments.value
  if (productName.value.trim()) query.productName = productName.value.trim()
  if (productSign.value.trim()) query.productSign = productSign.value.trim()
  if (productCode.value.trim()) query.productCode = productCode.value.trim()
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
  reload()
}

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
    mode.value === 'weekly' ? buildWeeklyClipboard(v.weeks, v.rows) : buildCurrentClipboard(v.rows)
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

      <!-- ①②③ 品名・商品記号（部分一致）・品番3桁（完全一致）＋ 期間 -->
      <div class="mt-3 grid grid-cols-1 gap-3 border-t border-dashed border-slate-200 pt-3 sm:grid-cols-2 lg:grid-cols-4">
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">品名（部分一致）</label>
          <input
            v-model="productName"
            type="search"
            placeholder="例: デニム"
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

        <p v-if="view?.truncated" class="rounded bg-amber-50 px-2 py-1 text-xs text-amber-700">
          対象 SKU が多いため上限件数で打ち切りました。フィルタで絞り込んでください。
        </p>

        <div
          v-if="(view?.rows.length ?? 0) === 0"
          class="rounded-xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-400"
        >
          該当する SKU がありません。フィルタを見直してください。
        </div>

        <!-- 週別マトリクス -->
        <div
          v-else-if="mode === 'weekly'"
          class="overflow-auto rounded-xl border border-slate-200 bg-white"
        >
          <table class="text-xs" style="border-collapse: separate; border-spacing: 0">
            <thead>
              <tr>
                <th class="sticky left-0 z-20 whitespace-nowrap border-b border-r border-slate-200 bg-slate-50 px-2 py-2 text-left font-semibold text-slate-600">品番/単品</th>
                <th class="whitespace-nowrap border-b border-r border-slate-200 bg-slate-50 px-2 py-2 text-left font-semibold text-slate-600">品名 / カラー / サイズ</th>
                <th class="whitespace-nowrap border-b border-r border-slate-200 bg-slate-50 px-2 py-2 text-right font-semibold text-slate-600">上代</th>
                <th class="whitespace-nowrap border-b border-r border-slate-200 bg-slate-50 px-2 py-2 text-center font-semibold text-slate-600">区分</th>
                <th
                  v-for="w in view!.weeks"
                  :key="w"
                  class="whitespace-nowrap border-b border-l border-slate-200 bg-slate-50 px-2 py-2 text-right font-medium text-slate-500"
                >{{ w.slice(5) }}</th>
              </tr>
            </thead>
            <tbody>
              <template v-for="row in view!.rows" :key="row.key">
                <!-- 売数 -->
                <tr class="border-t border-slate-200">
                  <th
                    rowspan="3"
                    class="sticky left-0 z-10 whitespace-nowrap border-r border-slate-200 bg-white px-2 py-1 text-left align-top font-semibold text-slate-700"
                  >
                    {{ row.hinbanCode }}-{{ row.tanpinCode }}
                  </th>
                  <td rowspan="3" class="border-r border-slate-200 px-2 py-1 align-top text-slate-600">
                    <div class="max-w-[160px] truncate font-medium text-slate-700" :title="row.hinmei">{{ row.hinmei || '—' }}</div>
                    <div class="text-[11px] text-slate-400">{{ row.colorName || '—' }} / {{ row.sizeName || '—' }}</div>
                  </td>
                  <td rowspan="3" class="border-r border-slate-200 px-2 py-1 text-right align-top tabular-nums text-slate-600">
                    {{ formatCurrency(row.listPrice) }}
                  </td>
                  <td class="border-r border-slate-200 px-2 py-1 text-center text-slate-500">売数</td>
                  <td
                    v-for="w in view!.weeks"
                    :key="`q-${w}`"
                    class="border-l border-slate-100 px-2 py-1 text-right tabular-nums"
                    :class="row.pointByWeek.get(w)?.isMarkdown ? 'bg-rose-100 font-semibold text-rose-700' : 'text-slate-700'"
                    :title="row.pointByWeek.get(w)?.isMarkdown ? `値下げ週（売価 ${formatCurrency(row.pointByWeek.get(w)!.salePrice)}）` : undefined"
                  >
                    {{ row.pointByWeek.has(w) ? formatNumber(row.pointByWeek.get(w)!.quantity) : '' }}
                  </td>
                </tr>
                <!-- 在庫数 -->
                <tr>
                  <td class="border-r border-t border-slate-100 px-2 py-1 text-center text-slate-500">在庫数</td>
                  <td
                    v-for="w in view!.weeks"
                    :key="`s-${w}`"
                    class="border-l border-t border-slate-100 px-2 py-1 text-right tabular-nums text-slate-600"
                  >
                    {{ row.pointByWeek.has(w) ? formatNumber(row.pointByWeek.get(w)!.stock) : '' }}
                  </td>
                </tr>
                <!-- 在日 -->
                <tr>
                  <td class="border-r border-t border-slate-100 px-2 py-1 text-center text-slate-500">在日</td>
                  <td
                    v-for="w in view!.weeks"
                    :key="`d-${w}`"
                    class="border-l border-t border-slate-100 px-2 py-1 text-right tabular-nums"
                    :class="stockDaysColorClass(row.pointByWeek.get(w)?.stockDays ?? null)"
                  >
                    {{ (row.pointByWeek.get(w)?.stockDays ?? 0) > 0 ? formatDecimal(row.pointByWeek.get(w)!.stockDays, 1) : '' }}
                  </td>
                </tr>
              </template>
            </tbody>
          </table>
        </div>

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
                <th class="whitespace-nowrap bg-slate-50 px-3 py-2 text-right font-medium">在庫数</th>
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
                <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-700">
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
          在日は最新週基準の平均。消化率は累計売上数÷累計納品数。プロパー／値下げ消化率は初回値下げ週の
          前後で売れた数量比により累計消化率を按分した近似値です。チラシ連動は現状データに無いため未実装です。
        </p>
      </div>
    </StatusBlock>
  </div>
</template>
