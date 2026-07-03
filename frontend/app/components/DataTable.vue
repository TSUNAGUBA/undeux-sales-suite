<script setup lang="ts" generic="T extends object">
interface TableColumn<Row> {
  key: string
  label: string
  align?: 'left' | 'right'
  format?: (row: Row) => string
  /**
   * 横スクロール時に左固定する列か。マスタ・ステータス等の識別列に指定する。
   * 固定列は「先頭から連続」させること（間に非固定列を挟むと左オフセットが崩れる）。
   */
  frozen?: boolean
  /**
   * 固定列の幅(px)。固定列が2列以上のとき、後続固定列の左オフセット算出に必要。
   * 先頭の固定列のみなら（left=0 で足りるため）省略可。
   */
  width?: number
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

// 固定列の左オフセット（px）を、先頭からの固定列幅を累積して求める。
// z-index 規約: 固定ヘッダ z-30 ＞ 上部固定ヘッダ z-20 ＞ 固定ボディ z-10 ＞ 通常 0。
// これにより「縦スクロールで固定ヘッダが固定列ボディを覆い」「横スクロールで固定列が通常セルを覆う」。
const frozenLeft = computed<Map<string, number>>(() => {
  const map = new Map<string, number>()
  let acc = 0
  for (const column of props.columns) {
    if (!column.frozen) continue
    map.set(column.key, acc)
    acc += column.width ?? 0
  }
  return map
})

/** 固定列セルの寸法・位置スタイル（幅を固定して後続列の左オフセットが崩れないようにする）。 */
function frozenStyle(column: TableColumn<T>): Record<string, string> {
  if (!column.frozen) return {}
  const style: Record<string, string> = { left: `${frozenLeft.value.get(column.key) ?? 0}px` }
  if (column.width != null) {
    style.width = `${column.width}px`
    style.minWidth = `${column.width}px`
    style.maxWidth = `${column.width}px`
  }
  return style
}

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
              column.frozen
                ? ['sticky z-30 border-r border-slate-200', fillHeight ? 'top-0 border-b' : '']
                : (fillHeight ? 'sticky top-0 z-20 border-b border-slate-200' : ''),
            ]"
            :style="frozenStyle(column)"
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
            :class="[
              column.align === 'right' ? 'text-right tabular-nums' : 'text-left',
              column.frozen ? 'sticky z-10 bg-white border-r border-slate-200' : '',
            ]"
            :style="frozenStyle(column)"
          >
            <!--
              column.key をスロット名としても提供する。スロットが定義されていれば
              任意の DOM（画像セル等）を描画でき、未定義なら format/値の文字列表示にフォールバックする。
            -->
            <slot
              :name="column.key"
              :row="row"
              :column="column"
              :value="display(row, column)"
            >{{ display(row, column) }}</slot>
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
        <!--
          スロットに <img> や <div> などブロック要素を渡せるよう、値側のラッパは
          <div> を使用する（<span> 内へのブロック要素配置は無効HTMLになる）。
        -->
        <div class="min-w-0 text-right font-medium text-slate-700">
          <slot
            :name="column.key"
            :row="row"
            :column="column"
            :value="display(row, column)"
          >{{ display(row, column) }}</slot>
        </div>
      </div>
    </div>
  </div>
</template>
