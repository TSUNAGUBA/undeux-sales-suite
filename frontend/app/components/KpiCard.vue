<script setup lang="ts">
import type { Component } from 'vue'

const props = defineProps<{
  label: string
  value: string
  icon: Component
  accentClass?: string
  sub?: string
  /** クリック可否。true のとき @click を発火し、ホバー時の見た目を変える。 */
  clickable?: boolean
}>()

const emit = defineEmits<{ click: [] }>()

function handleClick(): void {
  if (props.clickable) {
    emit('click')
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (props.clickable && (event.key === 'Enter' || event.key === ' ')) {
    event.preventDefault()
    emit('click')
  }
}
</script>

<template>
  <div
    class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm transition-shadow"
    :class="
      clickable
        ? 'cursor-pointer hover:shadow-md focus:outline-none focus:ring-2 focus:ring-indigo-400'
        : ''
    "
    :role="clickable ? 'button' : undefined"
    :tabindex="clickable ? 0 : undefined"
    @click="handleClick"
    @keydown="handleKeydown"
  >
    <div class="flex items-start justify-between gap-3">
      <div class="min-w-0">
        <p class="text-xs font-medium text-slate-500">{{ label }}</p>
        <p class="mt-1 truncate text-2xl font-bold text-slate-800">{{ value }}</p>
        <p v-if="sub" class="mt-0.5 truncate text-xs text-slate-400">{{ sub }}</p>
      </div>
      <div
        class="shrink-0 rounded-lg p-2"
        :class="accentClass ?? 'bg-indigo-50 text-indigo-600'"
      >
        <component :is="icon" class="h-5 w-5" />
      </div>
    </div>
  </div>
</template>
