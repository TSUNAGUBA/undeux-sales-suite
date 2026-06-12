<script setup lang="ts">
/**
 * タブ切り替え式の単一選択コントロール。
 *
 * 「全て」タブ + 選択肢タブを横並びで表示する。業態の選択（クロス集計の条件パネル・
 * 商品導入管理）のように、少数の排他的な選択肢をワンタップで切り替える用途で使う。
 * modelValue は null（=全て）または選択中の値。
 */
const props = withDefaults(
  defineProps<{
    options: { value: string; label: string }[]
    /** 選択中の値。null は「全て」。 */
    modelValue: string | null
    /** 「全て」タブの表示名。 */
    allLabel?: string
    /** アクセシビリティ用のグループ名。 */
    ariaLabel?: string
  }>(),
  { allLabel: '全て', ariaLabel: undefined },
)

const emit = defineEmits<{ 'update:modelValue': [string | null] }>()

function select(value: string | null): void {
  if (value !== props.modelValue) {
    emit('update:modelValue', value)
  }
}
</script>

<!--
  ARIA は tablist/tab ではなく「トグルボタン群」（role=group + aria-pressed）とする。
  tabs パターンは矢印キー移動・tabpanel 関連付けが必須要件になるが、本コンポーネントは
  タブパネルを持たないフィルタ切替であり、Tab キー + aria-pressed の方が実態に即す。
-->
<template>
  <div
    class="flex flex-wrap gap-1 rounded-lg bg-slate-100 p-1"
    role="group"
    :aria-label="ariaLabel"
  >
    <button
      type="button"
      class="rounded-md px-3 py-1.5 text-sm transition-colors"
      :class="
        modelValue === null
          ? 'bg-white font-semibold text-indigo-700 shadow-sm'
          : 'text-slate-600 hover:bg-white/60'
      "
      :aria-pressed="modelValue === null"
      @click="select(null)"
    >
      {{ allLabel }}
    </button>
    <button
      v-for="opt in options"
      :key="opt.value"
      type="button"
      class="rounded-md px-3 py-1.5 text-sm transition-colors"
      :class="
        modelValue === opt.value
          ? 'bg-white font-semibold text-indigo-700 shadow-sm'
          : 'text-slate-600 hover:bg-white/60'
      "
      :aria-pressed="modelValue === opt.value"
      @click="select(opt.value)"
    >
      {{ opt.label }}
    </button>
  </div>
</template>
