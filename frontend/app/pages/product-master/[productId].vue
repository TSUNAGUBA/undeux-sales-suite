<script setup lang="ts">
import { ArrowLeft } from 'lucide-vue-next'
import type { MasterProductDetail, MasterProductSku } from '~/types/api'

useHead({ title: '商品詳細 | UndeuxSales' })

const route = useRoute()
const router = useRouter()
const { get } = useApi()

const productId = computed(() => String(route.params.productId ?? ''))

const detail = ref<MasterProductDetail | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)
const notFound = ref(false)

// 表示中の SKU（クリックで切り替え）。null のときは商品マスタの代表画像 (Summary.primaryImageUrl)。
const selectedSku = ref<MasterProductSku | null>(null)
// 選択中 SKU 内での表示画像インデックス。
const selectedImageIdx = ref(0)

/**
 * SKU 一覧の表示順。
 * 第一優先: カラー名（ja ロケール）昇順
 * 第二優先: 同カラー内でサイズ昇順（数値は数値順、文字列は XS=SS<S<M<L<LL=2L<LLL=3L<LLLL=4L）
 * 第三優先: 単品コード昇順で決定論化
 */
const sortedSkus = computed(() => {
  const list = [...(detail.value?.skus ?? [])]
  list.sort((a, b) => {
    const byColor = a.colorName.localeCompare(b.colorName, 'ja')
    if (byColor !== 0) return byColor
    const bySize = compareSize(a.sizeName, b.sizeName)
    if (bySize !== 0) return bySize
    return a.unitCd.localeCompare(b.unitCd)
  })
  return list
})

const heroImageUrl = computed<string | null>(() => {
  if (selectedSku.value) {
    const images = selectedSku.value.images
    if (images.length === 0) return null
    const idx = Math.min(selectedImageIdx.value, images.length - 1)
    return images[idx]?.imageUrl ?? null
  }
  return detail.value?.summary.primaryImageUrl ?? null
})

const priceLabel = computed(() => {
  const s = detail.value?.summary
  if (!s) return '—'
  const min = s.minSalesPrice
  const max = s.maxSalesPrice
  if (min === null && max === null) return '—'
  if (min !== null && max !== null && min !== max) {
    return `${formatCurrency(min)} 〜 ${formatCurrency(max)}`
  }
  return formatCurrency(max ?? min ?? 0)
})

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  notFound.value = false
  try {
    detail.value = await get<MasterProductDetail>(
      `/api/product-master/${productId.value}`,
    )
    // 初期表示は商品マスタの代表画像（Summary）。SKU は未選択。
    selectedSku.value = null
    selectedImageIdx.value = 0
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

function selectSku(sku: MasterProductSku): void {
  selectedSku.value = sku
  selectedImageIdx.value = 0
}

function clearSelection(): void {
  selectedSku.value = null
  selectedImageIdx.value = 0
}

function goBack(): void {
  router.back()
}

watch(productId, load)

onMounted(load)
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
        商品マスタ一覧へ
      </NuxtLink>
    </div>

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
      :empty="!detail"
      empty-message="表示する商品データがありません。"
    >
      <div v-if="detail" class="space-y-4">
        <!-- ヘッダー: 画像 + 基本情報 -->
        <div class="grid grid-cols-1 gap-4 md:grid-cols-[280px_minmax(0,1fr)]">
          <div class="rounded-xl border border-slate-200 bg-white p-3 shadow-sm">
            <div class="relative aspect-square w-full overflow-hidden rounded-lg bg-slate-100">
              <ProductImage
                :src="heroImageUrl"
                :alt="detail.summary.productName"
                icon-class="h-12 w-12"
                label-class="text-xs"
              />
              <span
                v-if="selectedSku"
                class="absolute left-2 top-2 rounded-full bg-indigo-600 px-2 py-0.5 text-xs font-medium text-white shadow-sm"
              >
                {{ selectedSku.colorName }} / {{ selectedSku.sizeName }}
              </span>
            </div>

            <!-- 選択中 SKU が複数画像を持つ場合のサムネ -->
            <div
              v-if="selectedSku && selectedSku.images.length > 1"
              class="mt-2 flex flex-wrap gap-1"
            >
              <button
                v-for="(img, i) in selectedSku.images"
                :key="img.imageId"
                type="button"
                class="h-10 w-10 overflow-hidden rounded ring-2 transition-opacity"
                :class="
                  selectedImageIdx === i
                    ? 'ring-indigo-500 opacity-100'
                    : 'ring-transparent opacity-60 hover:opacity-100'
                "
                :title="`画像 ${i + 1}`"
                @click="selectedImageIdx = i"
              >
                <ProductImage
                  :src="img.imageUrl"
                  alt=""
                  icon-class="h-4 w-4"
                  :show-label="false"
                />
              </button>
            </div>

            <button
              v-if="selectedSku"
              type="button"
              class="mt-2 w-full rounded-lg border border-slate-300 px-2 py-1 text-xs text-slate-600 hover:bg-slate-50"
              @click="clearSelection"
            >
              代表画像に戻す
            </button>
          </div>

          <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
            <div class="flex flex-col gap-2">
              <span
                v-if="detail.summary.brand"
                class="self-start rounded-full bg-indigo-50 px-2 py-0.5 text-xs font-medium text-indigo-700"
              >
                {{ detail.summary.brand }}
              </span>
              <h1 class="text-lg font-bold text-slate-800">
                {{ detail.summary.productName }}
              </h1>
              <p class="text-xs text-slate-500">
                {{ detail.summary.divisionName }} ・ {{ detail.summary.businessCategorySign }}
                <span v-if="detail.summary.manager"> ・ 担当: {{ detail.summary.manager }}</span>
              </p>
            </div>

            <dl class="mt-3 grid grid-cols-2 gap-x-3 gap-y-2 border-t border-slate-100 pt-3 text-xs sm:grid-cols-4">
              <div>
                <dt class="text-slate-400">業態</dt>
                <dd class="font-semibold text-slate-700">{{ detail.summary.businessCategoryCd }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">商品記号</dt>
                <dd class="font-mono font-semibold text-slate-700">{{ detail.summary.productSign }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">品番</dt>
                <dd class="font-mono font-semibold text-slate-700">{{ detail.summary.productTypeCrd }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">価格</dt>
                <dd class="font-semibold text-slate-700">{{ priceLabel }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">SKU 数</dt>
                <dd class="font-semibold text-slate-700">{{ formatNumber(detail.summary.skuCount) }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">カラー</dt>
                <dd class="font-semibold text-slate-700">{{ formatNumber(detail.summary.colorCount) }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">サイズ</dt>
                <dd class="font-semibold text-slate-700">{{ formatNumber(detail.summary.sizeCount) }}</dd>
              </div>
              <div>
                <dt class="text-slate-400">画像枚数（全 SKU 合計）</dt>
                <dd class="font-semibold text-slate-700">
                  {{ formatNumber(detail.skus.reduce((n, s) => n + s.images.length, 0)) }}
                </dd>
              </div>
            </dl>
          </div>
        </div>

        <!-- SKU 一覧 -->
        <div class="rounded-xl border border-slate-200 bg-white shadow-sm">
          <div class="border-b border-slate-100 px-4 py-3">
            <h2 class="text-sm font-semibold text-slate-700">
              SKU 一覧（{{ formatNumber(detail.skus.length) }} 件）
            </h2>
            <p class="mt-0.5 text-xs text-slate-400">行をクリックすると上部の画像が切り替わります。</p>
          </div>

          <!-- デスクトップ: テーブル -->
          <div class="hidden sm:block">
            <table class="w-full text-sm">
              <thead class="bg-slate-50 text-xs text-slate-500">
                <tr>
                  <th class="px-3 py-2 text-left">画像</th>
                  <th class="px-3 py-2 text-left">単品コード</th>
                  <th class="px-3 py-2 text-left">カラー</th>
                  <th class="px-3 py-2 text-left">サイズ</th>
                  <th class="px-3 py-2 text-right">売価</th>
                  <th class="px-3 py-2 text-right">原価</th>
                  <th class="px-3 py-2 text-right">画像数</th>
                </tr>
              </thead>
              <tbody>
                <tr
                  v-for="sku in sortedSkus"
                  :key="sku.skuItemId"
                  class="cursor-pointer border-b border-slate-100 hover:bg-slate-50 last:border-0"
                  :class="selectedSku?.skuItemId === sku.skuItemId ? 'bg-indigo-50' : ''"
                  @click="selectSku(sku)"
                >
                  <td class="px-3 py-2">
                    <div class="h-10 w-10 overflow-hidden rounded">
                      <ProductImage
                        :src="sku.images[0]?.imageUrl ?? null"
                        :alt="`${sku.colorName} / ${sku.sizeName}`"
                        icon-class="h-4 w-4"
                        :show-label="false"
                      />
                    </div>
                  </td>
                  <td class="px-3 py-2 font-mono text-xs text-slate-500">{{ sku.unitCd }}</td>
                  <td class="px-3 py-2 text-slate-700">{{ sku.colorName || '—' }}</td>
                  <td class="px-3 py-2 text-slate-700">{{ sku.sizeName || '—' }}</td>
                  <td class="px-3 py-2 text-right text-slate-700">
                    {{ sku.salesPrice > 0 ? formatCurrency(sku.salesPrice) : '—' }}
                  </td>
                  <td class="px-3 py-2 text-right text-slate-700">
                    {{ sku.costPrice > 0 ? formatCurrency(sku.costPrice) : '—' }}
                  </td>
                  <td class="px-3 py-2 text-right text-slate-700">{{ formatNumber(sku.images.length) }}</td>
                </tr>
              </tbody>
            </table>
          </div>

          <!-- モバイル: カード -->
          <div class="flex flex-col gap-2 p-3 sm:hidden">
            <button
              v-for="sku in sortedSkus"
              :key="sku.skuItemId"
              type="button"
              class="flex items-start gap-3 rounded-lg border border-slate-200 p-3 text-left"
              :class="selectedSku?.skuItemId === sku.skuItemId ? 'border-indigo-400 bg-indigo-50' : ''"
              @click="selectSku(sku)"
            >
              <div class="h-14 w-14 shrink-0 overflow-hidden rounded">
                <ProductImage
                  :src="sku.images[0]?.imageUrl ?? null"
                  :alt="`${sku.colorName} / ${sku.sizeName}`"
                  icon-class="h-5 w-5"
                  :show-label="false"
                />
              </div>
              <div class="min-w-0 flex-1">
                <p class="font-mono text-xs text-slate-500">{{ sku.unitCd }}</p>
                <p class="text-sm font-semibold text-slate-800">
                  {{ sku.colorName || '—' }} / {{ sku.sizeName || '—' }}
                </p>
                <dl class="mt-1 grid grid-cols-2 gap-x-3 gap-y-1">
                  <div>
                    <dt class="text-[11px] text-slate-400">売価</dt>
                    <dd class="text-sm text-slate-700">
                      {{ sku.salesPrice > 0 ? formatCurrency(sku.salesPrice) : '—' }}
                    </dd>
                  </div>
                  <div>
                    <dt class="text-[11px] text-slate-400">画像</dt>
                    <dd class="text-sm text-slate-700">{{ formatNumber(sku.images.length) }} 枚</dd>
                  </div>
                </dl>
              </div>
            </button>
          </div>
        </div>
      </div>
    </StatusBlock>
  </div>
</template>
