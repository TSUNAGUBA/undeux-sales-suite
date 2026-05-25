<script setup lang="ts">
import { ChevronLeft, ChevronRight, Package } from 'lucide-vue-next'
import type { MasterProductSummary, MasterProductSkuImage } from '~/types/api'

const props = defineProps<{
  product: MasterProductSummary
  /** 任意で複数画像を渡せる（詳細ロード後に切替表示）。未指定時は primaryImageUrl のみ。 */
  galleryImages?: MasterProductSkuImage[] | null
  /** カード全体のクリック先（指定時は <NuxtLink> ラップ）。 */
  href?: string | null
}>()

const idx = ref(0)

const images = computed<string[]>(() => {
  if (props.galleryImages && props.galleryImages.length > 0) {
    return props.galleryImages.map((g) => g.imageUrl)
  }
  return props.product.primaryImageUrl ? [props.product.primaryImageUrl] : []
})

const hasMultiple = computed(() => images.value.length > 1)
const currentIdx = computed(() =>
  Math.min(idx.value, Math.max(0, images.value.length - 1)),
)
const currentImage = computed(() => images.value[currentIdx.value] ?? null)

function prev(): void {
  if (images.value.length === 0) return
  idx.value = (currentIdx.value - 1 + images.value.length) % images.value.length
}
function next(): void {
  if (images.value.length === 0) return
  idx.value = (currentIdx.value + 1) % images.value.length
}

const priceLabel = computed(() => {
  const min = props.product.minSalesPrice
  const max = props.product.maxSalesPrice
  if (min === null && max === null) return '—'
  if (min !== null && max !== null && min !== max) {
    return `${formatCurrency(min)} 〜 ${formatCurrency(max)}`
  }
  return formatCurrency(max ?? min ?? 0)
})
</script>

<template>
  <component
    :is="href ? resolveComponent('NuxtLink') : 'div'"
    :to="href ?? undefined"
    class="flex flex-col overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm transition-shadow hover:shadow-md"
  >
    <div class="relative aspect-square w-full overflow-hidden bg-slate-100">
      <img
        v-if="currentImage"
        :src="currentImage"
        :alt="product.productName"
        loading="lazy"
        class="size-full object-cover"
      />
      <div
        v-else
        class="flex size-full items-center justify-center text-slate-300"
      >
        <Package class="h-12 w-12" :stroke-width="1.5" />
      </div>

      <template v-if="hasMultiple">
        <button
          type="button"
          aria-label="前の画像"
          class="absolute left-1.5 top-1/2 flex h-7 w-7 -translate-y-1/2 items-center justify-center rounded-full bg-black/40 text-white backdrop-blur-sm transition-colors hover:bg-black/60"
          @click.prevent="prev"
        >
          <ChevronLeft class="h-4 w-4" />
        </button>
        <button
          type="button"
          aria-label="次の画像"
          class="absolute right-1.5 top-1/2 flex h-7 w-7 -translate-y-1/2 items-center justify-center rounded-full bg-black/40 text-white backdrop-blur-sm transition-colors hover:bg-black/60"
          @click.prevent="next"
        >
          <ChevronRight class="h-4 w-4" />
        </button>
        <span
          aria-live="polite"
          class="absolute bottom-1.5 right-2 rounded-full bg-black/50 px-2 py-0.5 text-xs text-white backdrop-blur-sm"
        >
          {{ currentIdx + 1 }}/{{ images.length }}
        </span>
      </template>

      <span
        v-if="product.brand"
        class="absolute left-2 top-2 rounded-full bg-white/90 px-2 py-0.5 text-xs font-medium text-slate-700 shadow-sm"
      >
        {{ product.brand }}
      </span>
    </div>

    <div class="flex flex-col gap-2 p-3.5">
      <div class="flex items-center gap-2 text-xs text-slate-500">
        <code class="font-mono">{{ product.productSign }}</code>
        <span class="text-slate-300">/</span>
        <code class="font-mono">{{ product.productTypeCrd }}</code>
      </div>
      <p class="line-clamp-2 text-sm font-semibold leading-snug text-slate-900">
        {{ product.productName || '—' }}
      </p>
      <p class="line-clamp-1 text-xs text-slate-400">
        {{ product.divisionName }} ・ {{ product.businessCategorySign }}
      </p>

      <dl class="mt-1 grid grid-cols-2 gap-x-2 gap-y-1.5 border-t border-slate-100 pt-2.5 text-xs">
        <div class="flex flex-col gap-0.5">
          <dt class="text-slate-400">価格</dt>
          <dd class="font-semibold text-slate-900">{{ priceLabel }}</dd>
        </div>
        <div class="flex flex-col gap-0.5">
          <dt class="text-slate-400">SKU</dt>
          <dd class="font-semibold text-slate-900">{{ formatNumber(product.skuCount) }}</dd>
        </div>
        <div class="flex flex-col gap-0.5">
          <dt class="text-slate-400">色</dt>
          <dd class="font-semibold text-slate-900">{{ formatNumber(product.colorCount) }}</dd>
        </div>
        <div class="flex flex-col gap-0.5">
          <dt class="text-slate-400">サイズ</dt>
          <dd class="font-semibold text-slate-900">{{ formatNumber(product.sizeCount) }}</dd>
        </div>
      </dl>
    </div>
  </component>
</template>
