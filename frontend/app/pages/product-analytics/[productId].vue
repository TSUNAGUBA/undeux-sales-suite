<script setup lang="ts">
import {
  ArrowLeft,
  Package,
  ShoppingBag,
  TrendingUp,
  Store,
  Boxes,
  Percent,
  CalendarDays,
} from 'lucide-vue-next'
import type { ProductAnalyticsResponse } from '~/types/api'

useHead({ title: '商品分析 | UndeuxSales' })

const route = useRoute()
const router = useRouter()
// 商品分析ページ専用のフィルタスコープ（他ページの共通フィルタを汚染しない）。
const { toQuery, loadOptions } = useFilters('product-analytics-filter')
const { get } = useApi()

const productId = computed(() => String(route.params.productId ?? ''))

const data = ref<ProductAnalyticsResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)
const notFound = ref(false)

const heroImageIndex = ref(0)

const trendLabels = computed(() =>
  (data.value?.weeklyTrend ?? []).map((p) => p.date.slice(5)),
)
const trendSeries = computed(() => [
  {
    label: '売上金額',
    data: (data.value?.weeklyTrend ?? []).map((p) => p.amount),
    color: '#4f46e5',
  },
  {
    label: '粗利',
    data: (data.value?.weeklyTrend ?? []).map((p) => p.grossProfit),
    color: '#10b981',
  },
])

const customerLabels = computed(() =>
  (data.value?.byCustomer.slice(0, 10) ?? []).map((c) =>
    c.customerName ? `${c.customerCode} ${c.customerName}` : c.customerCode,
  ),
)
const customerData = computed(() =>
  (data.value?.byCustomer.slice(0, 10) ?? []).map((c) => c.amount),
)

const businessTypeLabels = computed(() =>
  (data.value?.byBusinessType ?? []).map((b) =>
    b.displayName
      ? `${b.businessCategoryCd} ${b.displayName}${b.shortName ? ` (${b.shortName})` : ''}`
      : b.businessCategoryCd,
  ),
)
const businessTypeData = computed(() =>
  (data.value?.byBusinessType ?? []).map((b) => b.amount),
)

const heroImage = computed(() => {
  const skus = data.value?.bySku ?? []
  if (skus.length === 0) return data.value?.product.primaryImageUrl ?? null
  const idx = Math.min(heroImageIndex.value, skus.length - 1)
  return skus[idx]?.primaryImageUrl ?? data.value?.product.primaryImageUrl ?? null
})

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  notFound.value = false
  try {
    data.value = await get<ProductAnalyticsResponse>(
      `/api/product-analytics/${productId.value}`,
      toQuery(),
    )
    // 新しいデータセットに合わせてサムネ選択を初期化する。
    // 件数が変わらないフィルタ変更でも SKU 順序が変わると元の indexed SKU と乖離するため、
    // load() 完了時に必ず先頭へ戻す。
    heroImageIndex.value = 0
  } catch (error) {
    const extracted = extractApiError(error)
    if (extracted?.errorCode === 'UNDX-DATA-002') {
      notFound.value = true
    } else {
      errorMessage.value = apiErrorMessage(error)
    }
  } finally {
    loading.value = false
  }
}

function goBack(): void {
  router.back()
}

// 商品 ID 変更時はデータを再取得（load() 内で heroImageIndex も初期化される）。
watch(productId, () => {
  load()
})

onMounted(async () => {
  await loadOptions()
  await load()
})
</script>

<template>
  <div class="space-y-4">
    <div class="flex items-center gap-2">
      <button
        type="button"
        class="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 text-xs text-slate-600 hover:bg-slate-50"
        @click="goBack"
      >
        <ArrowLeft class="h-3.5 w-3.5" />
        戻る
      </button>
      <NuxtLink
        to="/product-master"
        class="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 text-xs text-slate-600 hover:bg-slate-50"
      >
        商品マスタへ
      </NuxtLink>
    </div>

    <FilterBar scope-key="product-analytics-filter" @apply="load" />

    <div
      v-if="notFound"
      class="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-700"
    >
      指定された商品マスタが見つかりません。URL の productId を確認してください。
    </div>

    <StatusBlock
      v-else
      :loading="loading"
      :error="errorMessage"
      :empty="!data"
      empty-message="表示する商品データがありません。"
    >
      <div v-if="data" class="space-y-4">
        <div class="grid grid-cols-1 gap-4 md:grid-cols-[280px_minmax(0,1fr)]">
          <div class="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
            <div class="relative aspect-square w-full overflow-hidden rounded-lg bg-slate-100">
              <img
                v-if="heroImage"
                :src="heroImage"
                :alt="data.product.productName"
                class="size-full object-cover"
              >
              <div
                v-else
                class="flex size-full items-center justify-center text-slate-300"
              >
                <Package class="h-12 w-12" :stroke-width="1.5" />
              </div>
            </div>

            <div
              v-if="(data.bySku ?? []).some((s) => s.primaryImageUrl)"
              class="mt-2 flex flex-wrap gap-1"
            >
              <button
                v-for="(sku, i) in data.bySku.filter((s) => s.primaryImageUrl)"
                :key="`${sku.unitCd}-${i}`"
                type="button"
                class="overflow-hidden rounded ring-2 transition-opacity"
                :class="
                  heroImageIndex === data.bySku.indexOf(sku)
                    ? 'ring-indigo-500 opacity-100'
                    : 'ring-transparent opacity-60 hover:opacity-100'
                "
                :title="`${sku.colorName} / ${sku.sizeName}`"
                @click="heroImageIndex = data.bySku.indexOf(sku)"
              >
                <img :src="sku.primaryImageUrl!" alt="" class="h-10 w-10 object-cover">
              </button>
            </div>
          </div>

          <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
            <div class="flex flex-col gap-2">
              <span
                v-if="data.product.brand"
                class="self-start rounded-full bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-700"
              >
                {{ data.product.brand }}
              </span>
              <h2 class="text-lg font-bold text-slate-800">
                {{ data.product.productName }}
              </h2>
              <p class="text-xs text-slate-500">
                {{ data.product.divisionName }} ・ {{ data.product.businessCategorySign }}
                <span v-if="data.product.manager"> ・ 担当: {{ data.product.manager }}</span>
              </p>
            </div>

            <dl class="mt-3 grid grid-cols-2 gap-x-3 gap-y-2 border-t border-slate-100 pt-3 text-xs sm:grid-cols-4">
              <div>
                <dt class="text-slate-400">業態</dt>
                <dd class="font-semibold text-slate-700">{{ data.product.businessCategoryCd }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">商品記号</dt>
                <dd class="font-mono font-semibold text-slate-700">{{ data.product.productSign }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">品番</dt>
                <dd class="font-mono font-semibold text-slate-700">{{ data.product.productTypeCrd }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">SKU 数</dt>
                <dd class="font-semibold text-slate-700">
                  {{ formatNumber(data.product.skuCount) }}
                  <span class="text-slate-400">
                    （色 {{ data.product.colorCount }} / サイズ {{ data.product.sizeCount }}）
                  </span>
                </dd>
              </div>
            </dl>
          </div>
        </div>

        <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <KpiCard
            label="売上金額"
            :value="formatCurrency(data.kpi.amount)"
            :icon="ShoppingBag"
            accent-class="bg-indigo-50 text-indigo-600"
          />
          <KpiCard
            label="売上数量"
            :value="formatNumber(data.kpi.quantity)"
            :icon="TrendingUp"
            accent-class="bg-blue-50 text-blue-600"
          />
          <KpiCard
            label="粗利"
            :value="formatCurrency(data.kpi.grossProfit)"
            :sub="`粗利率 ${formatRatioAsPercent(data.kpi.grossProfitRate)}`"
            :icon="Percent"
            accent-class="bg-emerald-50 text-emerald-600"
          />
          <KpiCard
            label="現在在庫"
            :value="formatNumber(data.kpi.currentStock)"
            :sub="`消化率 ${formatRatioAsPercent(data.kpi.sellThroughRate)}`"
            :icon="Boxes"
            accent-class="bg-amber-50 text-amber-600"
          />
          <KpiCard
            label="平均在日"
            :value="`${formatDecimal(data.kpi.averageStockDays, 1)} 日`"
            :icon="CalendarDays"
            accent-class="bg-slate-100 text-slate-600"
          />
          <KpiCard
            label="販売実績店舗数"
            :value="formatNumber(data.kpi.storeCount)"
            sub="期間内に1回でも売上があった店舗"
            :icon="Store"
            accent-class="bg-purple-50 text-purple-600"
          />
          <KpiCard
            label="最新週"
            :value="data.kpi.latestWeek ?? '—'"
            :icon="CalendarDays"
            accent-class="bg-slate-100 text-slate-600"
          />
        </div>

        <LineChartCard
          v-if="trendLabels.length > 0"
          title="週次トレンド（売上金額・粗利）"
          :labels="trendLabels"
          :series="trendSeries"
        />

        <div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <BarChartCard
            v-if="customerLabels.length > 0"
            title="取引先別売上 TOP10"
            :labels="customerLabels"
            :data="customerData"
            color="#4f46e5"
            series-label="売上金額"
            horizontal
          />
          <BarChartCard
            v-if="businessTypeLabels.length > 0"
            title="業態別売上（同一商品記号・品番）"
            :labels="businessTypeLabels"
            :data="businessTypeData"
            color="#10b981"
            series-label="売上金額"
            horizontal
          />
        </div>

        <div class="rounded-xl border border-slate-200 bg-white shadow-sm">
          <div class="border-b border-slate-100 px-4 py-3">
            <h3 class="text-sm font-semibold text-slate-700">SKU 別売上（色・サイズ）</h3>
          </div>

          <div class="hidden sm:block">
            <table class="w-full text-sm">
              <thead class="bg-slate-50 text-xs text-slate-500">
                <tr>
                  <th class="px-3 py-2 text-left">画像</th>
                  <th class="px-3 py-2 text-left">単品</th>
                  <th class="px-3 py-2 text-left">カラー</th>
                  <th class="px-3 py-2 text-left">サイズ</th>
                  <th class="px-3 py-2 text-right">売価</th>
                  <th class="px-3 py-2 text-right">数量</th>
                  <th class="px-3 py-2 text-right">売上金額</th>
                  <th class="px-3 py-2 text-right">粗利</th>
                  <th class="px-3 py-2 text-right">在庫</th>
                  <th class="px-3 py-2 text-right">構成比</th>
                </tr>
              </thead>
              <tbody>
                <tr
                  v-for="sku in data.bySku"
                  :key="sku.unitCd"
                  class="border-b border-slate-100 last:border-0"
                >
                  <td class="px-3 py-2">
                    <img
                      v-if="sku.primaryImageUrl"
                      :src="sku.primaryImageUrl"
                      alt=""
                      class="h-10 w-10 rounded object-cover"
                    >
                    <div
                      v-else
                      class="flex h-10 w-10 items-center justify-center rounded bg-slate-100 text-slate-300"
                    >
                      <Package class="h-4 w-4" />
                    </div>
                  </td>
                  <td class="px-3 py-2 font-mono text-xs text-slate-500">{{ sku.unitCd }}</td>
                  <td class="px-3 py-2 text-slate-700">{{ sku.colorName || '—' }}</td>
                  <td class="px-3 py-2 text-slate-700">{{ sku.sizeName || '—' }}</td>
                  <td class="px-3 py-2 text-right text-slate-700">
                    {{ sku.salesPrice > 0 ? formatCurrency(sku.salesPrice) : '—' }}
                  </td>
                  <td class="px-3 py-2 text-right text-slate-700">{{ formatNumber(sku.quantity) }}</td>
                  <td class="px-3 py-2 text-right font-semibold text-slate-800">
                    {{ formatCurrency(sku.amount) }}
                  </td>
                  <td class="px-3 py-2 text-right text-slate-700">{{ formatCurrency(sku.grossProfit) }}</td>
                  <td class="px-3 py-2 text-right text-slate-700">{{ formatNumber(sku.stock) }}</td>
                  <td class="px-3 py-2 text-right text-slate-700">{{ formatPercent(sku.sharePercent) }}</td>
                </tr>
              </tbody>
            </table>
          </div>

          <div class="flex flex-col gap-2 p-3 sm:hidden">
            <div
              v-for="sku in data.bySku"
              :key="sku.unitCd"
              class="rounded-lg border border-slate-200 p-3"
            >
              <div class="flex items-start gap-3">
                <img
                  v-if="sku.primaryImageUrl"
                  :src="sku.primaryImageUrl"
                  alt=""
                  class="h-14 w-14 shrink-0 rounded object-cover"
                >
                <div
                  v-else
                  class="flex h-14 w-14 shrink-0 items-center justify-center rounded bg-slate-100 text-slate-300"
                >
                  <Package class="h-5 w-5" />
                </div>
                <div class="min-w-0 flex-1">
                  <p class="font-mono text-xs text-slate-500">{{ sku.unitCd }}</p>
                  <p class="text-sm font-semibold text-slate-800">
                    {{ sku.colorName || '—' }} / {{ sku.sizeName || '—' }}
                  </p>
                </div>
              </div>
              <dl class="mt-2 grid grid-cols-2 gap-x-3 gap-y-1.5">
                <div>
                  <dt class="text-[11px] text-slate-400">売上金額</dt>
                  <dd class="text-sm font-semibold text-slate-900">{{ formatCurrency(sku.amount) }}</dd>
                </div>
                <div>
                  <dt class="text-[11px] text-slate-400">構成比</dt>
                  <dd class="text-sm text-slate-700">{{ formatPercent(sku.sharePercent) }}</dd>
                </div>
                <div>
                  <dt class="text-[11px] text-slate-400">数量</dt>
                  <dd class="text-sm text-slate-700">{{ formatNumber(sku.quantity) }}</dd>
                </div>
                <div>
                  <dt class="text-[11px] text-slate-400">在庫</dt>
                  <dd class="text-sm text-slate-700">{{ formatNumber(sku.stock) }}</dd>
                </div>
              </dl>
            </div>
          </div>
        </div>
      </div>
    </StatusBlock>
  </div>
</template>
