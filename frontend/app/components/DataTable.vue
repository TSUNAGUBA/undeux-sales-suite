<script setup lang="ts" generic="T extends object">
interface TableColumn<Row> {
  key: string
  label: string
  align?: 'left' | 'right'
  format?: (row: Row) => string
}

const props = defineProps<{
  columns: TableColumn<T>[]
  rows: T[]
  rowKey: (row: T) => string | number
  /** 行クリック可否。true のとき @row-click を発火し、カーソル等の見た目を変える。 */
  clickable?: boolean
  /** 親の高さに合わせて表領域を伸縮させ、内部スクロール時にヘッダーを上部固定する。 */
  fillHeight?: boolean
}>()

const emit = defineEmits<{ rowClick: [row: T] }>()

function display(row: T, column: TableColumn<T>): string {
  if (column.format) {
    return column.format(row)
  }
  const value = (row as Record<string, unknown>)[column.key]
  return value == null ? '-' : String(value)
}

function handleRowClick(row: T): void {
  if (props.clickable) {
    emit('rowClick', row)
  }
}
</script>

<template>
  <!-- デスクトップ: テーブル表示 -->
  <div
    class="hidden rounded-xl border border-slate-200 bg-white md:block"
    :class="fillHeight ? 'h-full overflow-auto' : 'overflow-x-auto'"
  >
    <table class="w-full text-sm">
      <thead class="text-slate-500">
        <tr>
          <th
            v-for="column in columns"
            :key="column.key"
            class="whitespace-nowrap bg-slate-50 px-4 py-2.5 font-medium"
            :class="[
              column.align === 'right' ? 'text-right' : 'text-left',
              fillHeight ? 'sticky top-0 z-10 border-b border-slate-200' : '',
            ]"
          >
            {{ column.label }}
          </th>
        </tr>
      </thead>
      <tbody class="divide-y divide-slate-100">
        <tr
          v-for="row in rows"
          :key="props.rowKey(row)"
          class="hover:bg-slate-50"
          :class="clickable ? 'cursor-pointer' : ''"
          @click="handleRowClick(row)"
        >
          <td
            v-for="column in columns"
            :key="column.key"
            class="px-4 py-2.5 text-slate-700"
            :class="
              column.align === 'right'
                ? 'text-right tabular-nums'
                : 'text-left'
            "
          >
            {{ display(row, column) }}
          </td>
        </tr>
      </tbody>
    </table>
  </div>

  <!-- モバイル: カード表示 -->
  <div
    class="space-y-2 md:hidden"
    :class="fillHeight ? 'h-full overflow-auto' : ''"
  >
    <div
      v-for="row in rows"
      :key="props.rowKey(row)"
      class="rounded-xl border border-slate-200 bg-white p-3 shadow-sm"
      :class="clickable ? 'cursor-pointer hover:bg-slate-50' : ''"
      @click="handleRowClick(row)"
    >
      <div
        v-for="column in columns"
        :key="column.key"
        class="flex justify-between gap-3 py-0.5 text-sm"
      >
        <span class="shrink-0 text-slate-500">{{ column.label }}</span>
        <span class="text-right font-medium text-slate-700">
          {{ display(row, column) }}
        </span>
      </div>
    </div>
  </div>
</template>
