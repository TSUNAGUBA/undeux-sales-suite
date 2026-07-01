<script setup lang="ts">
/**
 * 写真帳（/mart/photobook）— アイテム分析タブ。
 *
 * 商品マスタ（m_product / m_product_sku）を画像主体のギャラリーで閲覧する画面。
 * 商品画像と商品/SKU 情報（品番・記号・品名・価格・色数・サイズ数・SKU数・売上数量・在庫等）を
 * カードで一覧する。カード押下で商品の詳細分析（/mart/products/{id}）へ遷移する。
 *
 * フィルターは要件の4種:
 *   ⓪ 業態・部門（ScopeFilterTags・複数選択）
 *   ① 品名（部分一致）
 *   ② 商品記号（部分一致）
 *   ③ 品番3桁（完全一致）
 * 業態・部門の選択肢は /api/filters（全社サマリー標準）から取得し、検索は /api/product-master に渡す。
 * データ源は商品マスタ（sales_weekly 直参照）で、分析 mart の構築有無に依存しない。
 */
import { RotateCcw, Search } from 'lucide-vue-next'
import type { MasterProductPage } from '~/types/api'

useHead({ title: '写真帳 | UndeuxSales' })

const { get } = useApi()
// 業態・部門の選択肢だけを共有（/api/filters）から読む。共有フィルタ state は変更しない。
const { options, optionsError, loadOptions } = useFilters('mart-filter')

// ---------------------------------------------------------------
// フィルタ state（画面専用・ローカル）
// ---------------------------------------------------------------
const businessTypes = ref<string[]>([])
const departments = ref<string[]>([])
const productName = ref('')
const productSign = ref('')
const productCode = ref('')

const page = ref(1)
const pageSize = 24

const result = ref<MasterProductPage | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const totalPages = computed(() => {
  const total = result.value?.totalCount ?? 0
  return total === 0 ? 1 : Math.ceil(total / pageSize)
})

function buildQuery(): Record<string, unknown> {
  const query: Record<string, unknown> = { page: page.value, pageSize }
  if (businessTypes.value.length > 0) query.businessTypes = businessTypes.value
  if (departments.value.length > 0) query.departments = departments.value
  if (productName.value.trim()) query.productName = productName.value.trim()
  if (productSign.value.trim()) query.productSign = productSign.value.trim()
  if (productCode.value.trim()) query.productCode = productCode.value.trim()
  return query
}

// 「適用」連打などで古い応答が後着しても表示を上書きしないためのリクエスト世代。
let loadSeq = 0

async function load(): Promise<void> {
  const seq = ++loadSeq
  loading.value = true
  errorMessage.value = null
  try {
    const response = await get<MasterProductPage>('/api/product-master', buildQuery())
    if (seq !== loadSeq) return
    result.value = response
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
  page.value = 1
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

function changePage(delta: number): void {
  const next = page.value + delta
  if (next >= 1 && next <= totalPages.value) {
    page.value = next
    void load()
  }
}

onMounted(async () => {
  await loadOptions()
  await load()
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">写真帳</h1>
      <p class="text-sm text-slate-500">
        商品画像を主体に、商品/SKU 情報（品番・記号・品名・価格・色数・サイズ数・SKU数・売上数量・在庫）を
        一覧します。カードを押すと商品の詳細分析へ遷移します。
      </p>
    </div>

    <CollapsiblePanel title="フィルター" :default-open="true">
      <p v-if="optionsError" class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700">
        フィルタ選択肢の取得に失敗しました: {{ optionsError }}
      </p>

      <!-- ⓪ 業態・部門（全社サマリー標準の ScopeFilterTags・複数選択） -->
      <ScopeFilterTags
        :business-types="options?.businessTypes ?? []"
        :departments="options?.departments ?? []"
        :selected-business-types="businessTypes"
        :selected-departments="departments"
        multiple
        @update:selected-business-types="onBusinessTypesChange"
        @update:selected-departments="onDepartmentsChange"
      />

      <!-- ①②③ 品名（部分一致）・商品記号（部分一致）・品番3桁 -->
      <div class="mt-3 grid grid-cols-1 gap-3 border-t border-dashed border-slate-200 pt-3 sm:grid-cols-3">
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

    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="(result?.items.length ?? 0) === 0"
      empty-message="該当する商品が見つかりません。フィルター条件を変更してください。"
    >
      <div class="space-y-3">
        <p class="text-sm text-slate-600">全 {{ formatNumber(result?.totalCount ?? 0) }} 件</p>

        <div class="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5">
          <ProductMasterCard
            v-for="product in result?.items ?? []"
            :key="product.productId"
            :product="product"
            :href="`/mart/products/${product.productId}`"
          />
        </div>

        <div class="flex items-center justify-between text-sm text-slate-600">
          <span>{{ page }} / {{ totalPages }} ページ</span>
          <div class="flex items-center gap-2">
            <button
              type="button"
              class="flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 disabled:opacity-40"
              :disabled="page <= 1"
              @click="changePage(-1)"
            >
              前へ
            </button>
            <button
              type="button"
              class="flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 disabled:opacity-40"
              :disabled="page >= totalPages"
              @click="changePage(1)"
            >
              次へ
            </button>
          </div>
        </div>
      </div>
    </StatusBlock>
  </div>
</template>
