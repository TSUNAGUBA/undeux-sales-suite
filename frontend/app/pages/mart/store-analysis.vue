<script setup lang="ts">
/**
 * 店舗分析（/mart/store-analysis）— 小売（buyer）モードの店舗分析メニュー。
 *
 * 「発注タイプ（役割・権限）」「店舗属性（売上上位/下位・近隣/エリア・立地・形態）」
 * 「商品属性（部門・服種3桁CD）」を組み合わせて、条件内で売れている商品ランキングを表示する。
 *
 * 現状は店舗属性・発注区分の実データを持たないため、決定的なモックデータ（utils/storeAnalysisMock）で
 * 体験を先行表現する。フィルタ変更はクライアント側の集計（サーバ往復なし）で即時反映する。
 * 実データ接続時は本モックを店舗ディメンション＋発注区分つきの集計 API に置き換える。
 */
import { Info, RotateCcw, Store, Trophy } from 'lucide-vue-next'
import type {
  MockStoreProduct,
  OrderTypeKey,
  StoreAnalysisFilter,
} from '~/utils/storeAnalysisMock'

useHead({ title: '店舗分析 | UndeuxSales' })

// モック母集団は固定（決定的生成）。フィルタで絞り込み集計する。
const stores = buildMockStores()
const products = buildMockProducts()

const filter = ref<StoreAnalysisFilter>(defaultStoreAnalysisFilter())

/** 選択中の部門に属する服種CD（部門未選択なら全服種）。 */
const hinbanChoices = computed<{ code: string; name: string }[]>(() => {
  const seen = new Map<string, string>()
  for (const p of products) {
    if (filter.value.departmentCode !== 'all' && p.departmentCode !== filter.value.departmentCode) continue
    if (!seen.has(p.hinbanCode)) seen.set(p.hinbanCode, p.name)
  }
  return [...seen.keys()].sort().map((code) => ({ code, name: hinbanLabelOf(code) }))
})

/** 服種CD の代表名（部門内の最初の商品名の語幹）。 */
function hinbanLabelOf(code: string): string {
  const p = products.find((x) => x.hinbanCode === code)
  return p ? p.name.replace(/ [A-C]$/, '') : code
}

// 部門を切り替えたら、選択中の服種が対象外になる場合は「すべて」に戻す。
watch(
  () => filter.value.departmentCode,
  () => {
    if (
      filter.value.hinbanCode !== 'all'
      && !hinbanChoices.value.some((h) => h.code === filter.value.hinbanCode)
    ) {
      filter.value.hinbanCode = 'all'
    }
  },
)

const result = computed(() => buildStoreRanking(stores, products, filter.value, 20))
const rows = computed(() => result.value.rows)

function toggleOrderType(key: OrderTypeKey): void {
  const set = new Set(filter.value.orderTypes)
  if (set.has(key)) set.delete(key)
  else set.add(key)
  filter.value.orderTypes = [...set]
}

function resetFilters(): void {
  filter.value = defaultStoreAnalysisFilter()
}

function orderTypeLabel(key: OrderTypeKey): string {
  return STORE_ORDER_TYPES.find((o) => o.key === key)?.label ?? key
}

const activeChipClass = 'border-indigo-500 bg-indigo-50 text-indigo-700'
const inactiveChipClass = 'border-slate-200 bg-white text-slate-600 hover:border-indigo-300'

function rankBadgeClass(rank: number): string {
  if (rank === 1) return 'bg-amber-100 text-amber-700 ring-1 ring-amber-300'
  if (rank === 2) return 'bg-slate-200 text-slate-700 ring-1 ring-slate-300'
  if (rank === 3) return 'bg-orange-100 text-orange-700 ring-1 ring-orange-300'
  return 'bg-slate-50 text-slate-500'
}

function productLabel(p: MockStoreProduct): string {
  return `${p.departmentCode}-${p.hinbanCode}`
}
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="flex items-center gap-2 text-xl font-bold text-slate-800">
        <Store class="h-5 w-5 text-indigo-500" />店舗分析
      </h1>
      <p class="text-sm text-slate-500">
        発注タイプ・店舗属性・商品属性の条件を組み合わせて、条件内で売れている商品ランキングを表示します。
      </p>
      <p class="mt-1 inline-flex items-center gap-1.5 rounded-lg bg-amber-50 px-2.5 py-1 text-xs text-amber-700">
        <Info class="h-3.5 w-3.5 shrink-0" />
        店舗粒度の属性・発注区分は現状データが不足するため、モックデータで表現しています。
      </p>
    </div>

    <CollapsiblePanel title="フィルター" :default-open="true">
      <div class="space-y-4">
        <!-- 発注タイプ（役割・権限） -->
        <div>
          <p class="mb-1.5 text-xs font-semibold text-slate-500">発注タイプ（役割・権限）</p>
          <div class="flex flex-wrap gap-2">
            <button
              v-for="ot in STORE_ORDER_TYPES"
              :key="ot.key"
              type="button"
              class="inline-flex flex-col items-start rounded-lg border px-3 py-1.5 text-xs font-medium transition-colors"
              :class="filter.orderTypes.includes(ot.key) ? activeChipClass : inactiveChipClass"
              :aria-pressed="filter.orderTypes.includes(ot.key)"
              @click="toggleOrderType(ot.key)"
            >
              <span>{{ ot.label }}</span>
              <span class="text-[10px] font-normal text-slate-400">{{ ot.authority }}</span>
            </button>
          </div>
        </div>

        <!-- 店舗属性 -->
        <div class="border-t border-dashed border-slate-200 pt-3">
          <p class="mb-1.5 text-xs font-semibold text-slate-500">店舗属性</p>
          <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <label class="block">
              <span class="mb-1 block text-xs font-medium text-indigo-600">◎ 売上</span>
              <select v-model="filter.salesTier" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
                <option value="all">すべて</option>
                <option v-for="t in STORE_TIERS" :key="t.key" :value="t.key">{{ t.label }}</option>
              </select>
            </label>
            <label class="block">
              <span class="mb-1 block text-xs font-medium text-indigo-600">◎ 店舗／エリア</span>
              <select v-model="filter.area" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
                <option value="all">近隣（すべて）</option>
                <option v-for="a in STORE_AREAS" :key="a.key" :value="a.key">{{ a.label }}</option>
              </select>
            </label>
            <label class="block">
              <span class="mb-1 block text-xs font-medium text-slate-500">店舗タイプ1</span>
              <select v-model="filter.type1" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
                <option value="all">すべて</option>
                <option v-for="t in STORE_TYPE1" :key="t.key" :value="t.key">{{ t.label }}</option>
              </select>
            </label>
            <label class="block">
              <span class="mb-1 block text-xs font-medium text-slate-500">店舗タイプ2</span>
              <select v-model="filter.type2" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
                <option value="all">すべて</option>
                <option v-for="t in STORE_TYPE2" :key="t.key" :value="t.key">{{ t.label }}</option>
              </select>
            </label>
          </div>
        </div>

        <!-- 商品属性 -->
        <div class="border-t border-dashed border-slate-200 pt-3">
          <p class="mb-1.5 text-xs font-semibold text-slate-500">商品属性</p>
          <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <label class="block">
              <span class="mb-1 block text-xs font-medium text-slate-500">商品カテゴリ大（部門）</span>
              <select v-model="filter.departmentCode" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
                <option value="all">すべて</option>
                <option v-for="d in STORE_DEPARTMENTS" :key="d.code" :value="d.code">{{ d.code }}: {{ d.name }}</option>
              </select>
            </label>
            <label class="block">
              <span class="mb-1 block text-xs font-medium text-slate-500">商品カテゴリ中（服種3桁CD）</span>
              <select v-model="filter.hinbanCode" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
                <option value="all">すべて</option>
                <option v-for="h in hinbanChoices" :key="h.code" :value="h.code">{{ h.code }}: {{ h.name }}</option>
              </select>
            </label>
          </div>
        </div>

        <div class="flex flex-wrap gap-2 border-t border-dashed border-slate-200 pt-3">
          <button
            type="button"
            class="flex items-center gap-1.5 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
            @click="resetFilters"
          >
            <RotateCcw class="h-4 w-4" />
            リセット
          </button>
        </div>
      </div>
    </CollapsiblePanel>

    <!-- サマリー -->
    <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
      <div class="rounded-xl border border-slate-200 bg-white p-3">
        <p class="text-xs text-slate-500">対象店舗数</p>
        <p class="mt-0.5 text-lg font-bold text-slate-800">{{ formatNumber(result.storeCount) }} 店</p>
      </div>
      <div class="rounded-xl border border-slate-200 bg-white p-3">
        <p class="text-xs text-slate-500">ランクイン商品数</p>
        <p class="mt-0.5 text-lg font-bold text-slate-800">{{ formatNumber(rows.length) }} 品</p>
      </div>
      <div class="rounded-xl border border-slate-200 bg-white p-3">
        <p class="text-xs text-slate-500">首位商品</p>
        <p class="mt-0.5 truncate text-lg font-bold text-slate-800" :title="rows[0]?.product.name">
          {{ rows[0]?.product.name ?? '—' }}
        </p>
      </div>
      <div class="rounded-xl border border-slate-200 bg-white p-3">
        <p class="text-xs text-slate-500">合計売上数量</p>
        <p class="mt-0.5 text-lg font-bold text-slate-800">
          {{ formatNumber(rows.reduce((sum, r) => sum + r.quantity, 0)) }} 点
        </p>
      </div>
    </div>

    <div
      v-if="rows.length === 0"
      class="rounded-xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-400"
    >
      条件に合致する店舗または商品がありません。フィルターを見直してください。
    </div>

    <!-- ランキング（PC: テーブル） -->
    <div v-else class="hidden overflow-x-auto rounded-xl border border-slate-200 bg-white md:block">
      <table class="w-full text-sm">
        <thead class="text-slate-500">
          <tr>
            <!-- 横スクロール時に 順位・商品名 を左固定（z: 固定ヘッダ z-30 ＞ 固定ボディ z-10 ＞ 通常 0）。 -->
            <th class="sticky z-30 whitespace-nowrap bg-slate-50 px-3 py-2.5 text-center font-medium" :style="{ left: '0px', width: '56px', minWidth: '56px', maxWidth: '56px' }">順位</th>
            <th class="sticky z-30 whitespace-nowrap border-r border-slate-200 bg-slate-50 px-3 py-2.5 text-left font-medium" :style="{ left: '56px', width: '180px', minWidth: '180px', maxWidth: '180px' }">商品名</th>
            <th class="whitespace-nowrap bg-slate-50 px-3 py-2.5 text-left font-medium">部門 / 服種</th>
            <th class="whitespace-nowrap bg-slate-50 px-3 py-2.5 text-left font-medium">発注タイプ</th>
            <th class="whitespace-nowrap bg-slate-50 px-3 py-2.5 text-right font-medium">売上数量</th>
            <th class="whitespace-nowrap bg-slate-50 px-3 py-2.5 text-right font-medium">売上金額</th>
            <th class="whitespace-nowrap bg-slate-50 px-3 py-2.5 text-right font-medium">実績店舗数</th>
            <th class="whitespace-nowrap bg-slate-50 px-3 py-2.5 text-right font-medium">消化率</th>
            <th class="whitespace-nowrap bg-slate-50 px-3 py-2.5 text-right font-medium">在庫数</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-slate-100">
          <tr v-for="row in rows" :key="row.product.key" class="hover:bg-slate-50">
            <td class="sticky z-10 whitespace-nowrap bg-white px-3 py-2 text-center" :style="{ left: '0px', width: '56px', minWidth: '56px', maxWidth: '56px' }">
              <span
                class="inline-flex h-6 min-w-6 items-center justify-center rounded-full px-1.5 text-xs font-bold tabular-nums"
                :class="rankBadgeClass(row.rank)"
              >{{ row.rank }}</span>
            </td>
            <td class="sticky z-10 truncate border-r border-slate-200 bg-white px-3 py-2 font-medium text-slate-700" :style="{ left: '56px', width: '180px', minWidth: '180px', maxWidth: '180px' }" :title="row.product.name">{{ row.product.name }}</td>
            <td class="whitespace-nowrap px-3 py-2 text-slate-500">
              {{ row.product.departmentName }}
              <span class="text-slate-300">/</span>
              <span class="font-mono">{{ row.product.hinbanCode }}</span>
            </td>
            <td class="whitespace-nowrap px-3 py-2">
              <span class="rounded bg-slate-100 px-1.5 py-0.5 text-xs text-slate-600">
                {{ orderTypeLabel(row.product.orderType) }}
              </span>
            </td>
            <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums font-semibold text-slate-700">{{ formatNumber(row.quantity) }}</td>
            <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-600">{{ formatCurrency(row.amount) }}</td>
            <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-500">{{ formatNumber(row.storeCount) }}</td>
            <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-600">{{ formatRatioAsPercent(row.sellThroughRate) }}</td>
            <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-600">{{ formatNumber(row.stock) }}</td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- ランキング（モバイル: カード） -->
    <div v-if="rows.length > 0" class="space-y-2 md:hidden">
      <div
        v-for="row in rows"
        :key="row.product.key"
        class="rounded-xl border border-slate-200 bg-white p-3 shadow-sm"
      >
        <div class="mb-2 flex items-center gap-2">
          <span
            class="inline-flex h-6 min-w-6 items-center justify-center rounded-full px-1.5 text-xs font-bold tabular-nums"
            :class="rankBadgeClass(row.rank)"
          >{{ row.rank }}</span>
          <Trophy v-if="row.rank === 1" class="h-4 w-4 text-amber-500" />
          <span class="min-w-0 flex-1 truncate font-semibold text-slate-700">{{ row.product.name }}</span>
          <span class="rounded bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-600">
            {{ orderTypeLabel(row.product.orderType) }}
          </span>
        </div>
        <div class="grid grid-cols-2 gap-x-3 gap-y-1 text-sm">
          <div class="flex justify-between gap-2">
            <span class="text-slate-500">部門 / 服種</span>
            <span class="text-slate-700">{{ productLabel(row.product) }}</span>
          </div>
          <div class="flex justify-between gap-2">
            <span class="text-slate-500">売上数量</span>
            <span class="tabular-nums font-semibold text-slate-700">{{ formatNumber(row.quantity) }}</span>
          </div>
          <div class="flex justify-between gap-2">
            <span class="text-slate-500">売上金額</span>
            <span class="tabular-nums text-slate-700">{{ formatCurrency(row.amount) }}</span>
          </div>
          <div class="flex justify-between gap-2">
            <span class="text-slate-500">実績店舗数</span>
            <span class="tabular-nums text-slate-700">{{ formatNumber(row.storeCount) }}</span>
          </div>
          <div class="flex justify-between gap-2">
            <span class="text-slate-500">消化率</span>
            <span class="tabular-nums text-slate-700">{{ formatRatioAsPercent(row.sellThroughRate) }}</span>
          </div>
          <div class="flex justify-between gap-2">
            <span class="text-slate-500">在庫数</span>
            <span class="tabular-nums text-slate-700">{{ formatNumber(row.stock) }}</span>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
