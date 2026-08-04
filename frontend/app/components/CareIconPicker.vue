<script setup lang="ts">
import { ArrowLeft, ArrowRight, X } from 'lucide-vue-next'
import type { CareIcon } from '~/types/api'

/**
 * 洗濯表示アイコンの選択ピッカー（並び順つき）。
 * 選択済みは順序を保持し、左右移動・削除ができる。パレットから追加する。
 * アイコンの SVG は自社 API（CareIconCatalog）由来の信頼済みインライン SVG のため v-html で描画する。
 */
const props = defineProps<{
  modelValue: string[]
  icons: CareIcon[]
  disabled?: boolean
}>()

const emit = defineEmits<{ (e: 'update:modelValue', value: string[]): void }>()

/** カテゴリ表示名（未知カテゴリはコードのまま表示）。 */
const CATEGORY_LABELS: Record<string, string> = {
  wash: '洗濯',
  bleach: '漂白',
  tumble: 'タンブル乾燥',
  dry: '自然乾燥',
  iron: 'アイロン',
  professional: '商業クリーニング',
}

const iconByCode = computed(() => {
  const map = new Map<string, CareIcon>()
  for (const icon of props.icons) map.set(icon.code, icon)
  return map
})

/** カテゴリ順・定義順を保つグルーピング（パレット表示用）。 */
const groups = computed(() => {
  const order: string[] = []
  const byCategory = new Map<string, CareIcon[]>()
  for (const icon of props.icons) {
    if (!byCategory.has(icon.category)) {
      byCategory.set(icon.category, [])
      order.push(icon.category)
    }
    byCategory.get(icon.category)!.push(icon)
  }
  return order.map((category) => ({
    category,
    label: CATEGORY_LABELS[category] ?? category,
    icons: byCategory.get(category)!,
  }))
})

/** 選択済みコード → アイコン（未知コードも枠だけ残して欠落を可視化）。 */
const selected = computed(() =>
  props.modelValue.map((code) => ({ code, icon: iconByCode.value.get(code) ?? null })),
)

function commit(next: string[]): void {
  emit('update:modelValue', next)
}

function add(code: string): void {
  if (props.disabled) return
  commit([...props.modelValue, code])
}

function removeAt(index: number): void {
  if (props.disabled) return
  commit(props.modelValue.filter((_, i) => i !== index))
}

function move(index: number, delta: number): void {
  if (props.disabled) return
  const target = index + delta
  if (target < 0 || target >= props.modelValue.length) return
  const next = [...props.modelValue]
  const [item] = next.splice(index, 1)
  next.splice(target, 0, item as string)
  commit(next)
}
</script>

<template>
  <div class="space-y-3">
    <!-- 選択済み（並び順） -->
    <div>
      <p class="text-xs font-medium text-slate-500">選択中（左から記載順）</p>
      <p v-if="selected.length === 0" class="mt-1 text-xs text-slate-400">
        下のパレットからアイコンを選んでください（並び順も突合対象です）。
      </p>
      <ol v-else class="mt-2 flex flex-wrap gap-2">
        <li
          v-for="(entry, index) in selected"
          :key="`${entry.code}-${index}`"
          class="flex items-center gap-1 rounded-lg border border-indigo-200 bg-indigo-50 p-1.5"
        >
          <span class="grid h-8 w-8 place-items-center text-slate-700" aria-hidden="true">
            <span v-if="entry.icon" class="h-7 w-7" v-html="entry.icon.svg" />
            <span v-else class="text-[10px] text-rose-500">?</span>
          </span>
          <span class="max-w-[7rem] truncate text-[11px] text-slate-600" :title="entry.icon?.label ?? entry.code">
            {{ entry.icon?.label ?? entry.code }}
          </span>
          <span class="flex items-center">
            <button
              type="button"
              class="rounded p-0.5 text-slate-400 hover:text-indigo-700 disabled:opacity-30"
              :disabled="disabled || index === 0"
              :aria-label="`${entry.icon?.label ?? entry.code} を前へ`"
              @click="move(index, -1)"
            >
              <ArrowLeft class="h-3.5 w-3.5" />
            </button>
            <button
              type="button"
              class="rounded p-0.5 text-slate-400 hover:text-indigo-700 disabled:opacity-30"
              :disabled="disabled || index === selected.length - 1"
              :aria-label="`${entry.icon?.label ?? entry.code} を後ろへ`"
              @click="move(index, 1)"
            >
              <ArrowRight class="h-3.5 w-3.5" />
            </button>
            <button
              type="button"
              class="rounded p-0.5 text-slate-400 hover:text-rose-600 disabled:opacity-30"
              :disabled="disabled"
              :aria-label="`${entry.icon?.label ?? entry.code} を削除`"
              @click="removeAt(index)"
            >
              <X class="h-3.5 w-3.5" />
            </button>
          </span>
        </li>
      </ol>
    </div>

    <!-- パレット -->
    <div class="rounded-lg border border-slate-200 bg-slate-50 p-2">
      <div v-for="group in groups" :key="group.category" class="mb-2 last:mb-0">
        <p class="px-1 text-[11px] font-semibold text-slate-500">{{ group.label }}</p>
        <div class="mt-1 flex flex-wrap gap-1">
          <button
            v-for="icon in group.icons"
            :key="icon.code"
            type="button"
            class="grid h-9 w-9 place-items-center rounded-md border border-slate-200 bg-white text-slate-700 transition-colors hover:border-indigo-400 hover:bg-indigo-50 disabled:opacity-40"
            :disabled="disabled"
            :title="icon.label"
            :aria-label="`${icon.label} を追加`"
            @click="add(icon.code)"
          >
            <span class="h-6 w-6" v-html="icon.svg" />
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
