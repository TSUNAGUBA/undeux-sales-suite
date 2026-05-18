<script setup lang="ts">
import { ChevronDown, Check } from 'lucide-vue-next'

interface SelectOption {
  value: string
  text: string
}

const props = defineProps<{
  label: string
  options: SelectOption[]
  modelValue: string[]
}>()

const emit = defineEmits<{ 'update:modelValue': [string[]] }>()

const open = ref(false)
const search = ref('')
const root = ref<HTMLElement | null>(null)

const filteredOptions = computed(() => {
  const keyword = search.value.trim().toLowerCase()
  if (!keyword) {
    return props.options
  }
  return props.options.filter(
    (option) =>
      option.text.toLowerCase().includes(keyword) ||
      option.value.toLowerCase().includes(keyword),
  )
})

function isSelected(value: string): boolean {
  return props.modelValue.includes(value)
}

function toggle(value: string): void {
  const next = new Set(props.modelValue)
  if (next.has(value)) {
    next.delete(value)
  } else {
    next.add(value)
  }
  emit('update:modelValue', [...next])
}

function clearSelection(): void {
  emit('update:modelValue', [])
}

function handleDocumentClick(event: MouseEvent): void {
  if (root.value && !root.value.contains(event.target as Node)) {
    open.value = false
  }
}

onMounted(() => document.addEventListener('click', handleDocumentClick))
onBeforeUnmount(() => document.removeEventListener('click', handleDocumentClick))
</script>

<template>
  <div ref="root" class="relative">
    <label class="mb-1 block text-xs font-medium text-slate-500">{{ label }}</label>
    <button
      type="button"
      class="flex w-full items-center justify-between rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700 hover:border-slate-400"
      @click="open = !open"
    >
      <span>
        {{ modelValue.length > 0 ? `${modelValue.length}件選択中` : '指定なし' }}
      </span>
      <ChevronDown class="h-4 w-4 text-slate-400" />
    </button>

    <div
      v-if="open"
      class="absolute z-30 mt-1 w-full rounded-lg border border-slate-200 bg-white shadow-lg"
    >
      <div class="border-b border-slate-100 p-2">
        <input
          v-model="search"
          type="text"
          placeholder="検索..."
          class="w-full rounded-md border border-slate-200 px-2 py-1 text-sm focus:border-indigo-400 focus:outline-none"
        >
      </div>
      <div class="max-h-56 overflow-y-auto py-1">
        <button
          v-for="option in filteredOptions"
          :key="option.value"
          type="button"
          class="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm hover:bg-slate-50"
          @click="toggle(option.value)"
        >
          <span
            class="flex h-4 w-4 shrink-0 items-center justify-center rounded border"
            :class="
              isSelected(option.value)
                ? 'border-indigo-600 bg-indigo-600'
                : 'border-slate-300'
            "
          >
            <Check v-if="isSelected(option.value)" class="h-3 w-3 text-white" />
          </span>
          <span class="truncate text-slate-700">{{ option.text }}</span>
        </button>
        <p
          v-if="filteredOptions.length === 0"
          class="px-3 py-2 text-xs text-slate-400"
        >
          該当する選択肢がありません。
        </p>
      </div>
      <button
        v-if="modelValue.length > 0"
        type="button"
        class="w-full border-t border-slate-100 px-3 py-1.5 text-xs text-indigo-600 hover:bg-slate-50"
        @click="clearSelection"
      >
        選択をクリア
      </button>
    </div>
  </div>
</template>
