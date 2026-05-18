<script setup lang="ts">
import type { Component } from 'vue'

defineProps<{
  label: string
  value: string
  icon: Component
  accentClass?: string
  sub?: string
}>()
</script>

<template>
  <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
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
