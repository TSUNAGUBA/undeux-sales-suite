<script setup lang="ts">
import { ImageOff, Package } from 'lucide-vue-next'

/**
 * 商品画像の表示コンポーネント。
 * - src が null/undefined/空文字: 「No Image」プレースホルダ
 * - src あり・ロードエラー: 「画像エラー」プレースホルダ（壊れた画像アイコンの代替）
 * - src あり・ロード成功: <img> を表示
 *
 * 呼び出し側は外側の <div> でサイズ・shape（角丸等）・absolute オーバーレイ等を制御し、
 * 本コンポーネントは親の高さ・幅いっぱいに描画する（h-full w-full）。
 */
const props = defineProps<{
  src?: string | null
  alt?: string
  /** プレースホルダのアイコンクラス（既定: h-10 w-10）。 */
  iconClass?: string
  /** プレースホルダのテキストクラス（既定: text-xs）。 */
  labelClass?: string
  /** ラベルを表示するか（小さいセル用に false にできる、既定: true）。 */
  showLabel?: boolean
}>()

const failed = ref(false)

// src が切り替わったらエラー状態をリセットする（前回失敗を引き継がない）。
watch(
  () => props.src,
  () => {
    failed.value = false
  },
)

const hasImage = computed(() => Boolean(props.src) && !failed.value)
const showLabelComputed = computed(() => props.showLabel !== false)
const iconClassComputed = computed(() => props.iconClass ?? 'h-10 w-10')
const labelClassComputed = computed(() => props.labelClass ?? 'text-xs')
</script>

<template>
  <img
    v-if="hasImage"
    :src="src!"
    :alt="alt ?? ''"
    loading="lazy"
    decoding="async"
    class="h-full w-full object-cover"
    @error="failed = true"
  >
  <div
    v-else
    class="flex h-full w-full flex-col items-center justify-center gap-1 bg-slate-100 text-slate-400"
    role="img"
    :aria-label="failed ? '画像の読み込みに失敗しました' : '画像が登録されていません'"
  >
    <component
      :is="failed ? ImageOff : Package"
      :class="iconClassComputed"
      :stroke-width="1.5"
    />
    <span
      v-if="showLabelComputed"
      :class="['font-medium', labelClassComputed]"
    >
      {{ failed ? '画像エラー' : 'No Image' }}
    </span>
  </div>
</template>
