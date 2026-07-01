<script setup lang="ts">
/**
 * ランキング表。
 *
 * - PC: テーブル（順位・名称＋ABCバッジ・複合スコア・各指標・構成比・累積・前順位・変動・成長率）。
 * - モバイル: カード型レイアウト（同内容を縦積み）。
 * - スティッキーヘッダ（PC）。順位上位3件はメダル色のバッジで強調。
 *
 * 順位・構成比・ABC・順位変動はすべて RankingViewRow に集約済み（算出は utils/ranking）。
 */
import type { RankingMetricKey } from '~/types/api'
import type { AbcTier, RankingViewRow } from '~/utils/ranking'

const props = defineProps<{
  rows: RankingViewRow[]
  /** 集計軸の表示名（例「品番3桁」）。名称列の見出しに使う。 */
  dimensionLabel: string
  /** 表示する指標列。 */
  metricColumns: RankingMetricKey[]
  /** 複合スコア列を表示するか。 */
  showComposite: boolean
  /** 比較（前順位・変動・成長率）列を表示するか。 */
  showComparison: boolean
  /** 成長率の基準指標ラベル（例「売上金額」）。 */
  growthLabel: string
  /** 行クリックでドリルダウンを許可するか。 */
  clickable?: boolean
  /**
   * 残在庫数・残在庫金額（最新週スナップショット）列を末尾（変動列の右）に追加するか。
   * 商品記号別ランキングで在庫の積み上がりを併記するために使う。
   */
  showStockColumns?: boolean
}>()

const emit = defineEmits<{ rowClick: [row: RankingViewRow] }>()

const TIER_BADGE: Record<AbcTier, string> = {
  A: 'bg-indigo-100 text-indigo-700',
  B: 'bg-sky-100 text-sky-700',
  C: 'bg-slate-100 text-slate-600',
}

function metricLabel(key: RankingMetricKey): string {
  return rankingMetricInfo(key).label
}

function metricCell(row: RankingViewRow, key: RankingMetricKey): string {
  return formatRankingMetric(metricRawValue(row.values, key), key)
}

/** 順位バッジの配色（上位3件はメダル色）。 */
function rankBadgeClass(rank: number): string {
  if (rank === 1) return 'bg-amber-100 text-amber-700 ring-1 ring-amber-300'
  if (rank === 2) return 'bg-slate-200 text-slate-700 ring-1 ring-slate-300'
  if (rank === 3) return 'bg-orange-100 text-orange-700 ring-1 ring-orange-300'
  return 'bg-slate-50 text-slate-500'
}

function deltaText(row: RankingViewRow): string {
  if (row.isNew) return 'NEW'
  if (row.rankDelta === null) return '—'
  if (row.rankDelta > 0) return `▲${row.rankDelta}`
  if (row.rankDelta < 0) return `▼${Math.abs(row.rankDelta)}`
  return '→'
}

function deltaClass(row: RankingViewRow): string {
  if (row.isNew) return 'bg-blue-50 text-blue-700'
  if (row.rankDelta === null) return 'text-slate-400'
  if (row.rankDelta > 0) return 'bg-emerald-50 text-emerald-700'
  if (row.rankDelta < 0) return 'bg-red-50 text-red-700'
  return 'text-slate-500'
}

function growthText(row: RankingViewRow): string {
  if (row.growth === null) return '—'
  const sign = row.growth > 0 ? '+' : ''
  return `${sign}${row.growth.toFixed(1)}%`
}

function growthClass(row: RankingViewRow): string {
  if (row.growth === null) return 'text-slate-400'
  if (row.growth > 0) return 'text-emerald-600'
  if (row.growth < 0) return 'text-red-600'
  return 'text-slate-500'
}

function compositeText(row: RankingViewRow): string {
  return row.compositeScore === null ? '—' : row.compositeScore.toFixed(2)
}

/** 残在庫数（最新週スナップショット。未取得は '—'）。 */
function stockText(row: RankingViewRow): string {
  return row.values.stock === null ? '—' : formatNumber(row.values.stock)
}

/** 残在庫金額（原価ベース。未取得は '—'）。 */
function stockValueText(row: RankingViewRow): string {
  return row.values.stockValueCost === null ? '—' : formatCurrency(row.values.stockValueCost)
}

function onRowClick(row: RankingViewRow): void {
  if (props.clickable) {
    emit('rowClick', row)
  }
}
</script>

<template>
  <div>
    <!-- PC: テーブル -->
    <div class="hidden overflow-x-auto rounded-xl border border-slate-200 bg-white md:block">
      <table class="w-full text-sm">
        <thead class="text-slate-500">
          <tr>
            <th class="sticky top-0 z-10 whitespace-nowrap bg-slate-50 px-3 py-2.5 text-center font-medium">順位</th>
            <th class="sticky top-0 z-10 whitespace-nowrap bg-slate-50 px-3 py-2.5 text-left font-medium">
              {{ dimensionLabel }}
            </th>
            <th
              v-if="showComposite"
              class="sticky top-0 z-10 whitespace-nowrap bg-slate-50 px-3 py-2.5 text-right font-medium"
            >複合スコア</th>
            <th
              v-for="key in metricColumns"
              :key="key"
              class="sticky top-0 z-10 whitespace-nowrap bg-slate-50 px-3 py-2.5 text-right font-medium"
            >{{ metricLabel(key) }}</th>
            <th class="sticky top-0 z-10 whitespace-nowrap bg-slate-50 px-3 py-2.5 text-right font-medium">構成比</th>
            <th class="sticky top-0 z-10 whitespace-nowrap bg-slate-50 px-3 py-2.5 text-right font-medium">累積</th>
            <template v-if="showComparison">
              <th class="sticky top-0 z-10 whitespace-nowrap bg-slate-50 px-3 py-2.5 text-center font-medium">前順位</th>
              <th class="sticky top-0 z-10 whitespace-nowrap bg-slate-50 px-3 py-2.5 text-center font-medium">変動</th>
              <th class="sticky top-0 z-10 whitespace-nowrap bg-slate-50 px-3 py-2.5 text-right font-medium">
                成長率<span class="text-xs text-slate-400">（{{ growthLabel }}）</span>
              </th>
            </template>
            <template v-if="showStockColumns">
              <th class="sticky top-0 z-10 whitespace-nowrap bg-slate-50 px-3 py-2.5 text-right font-medium">残在庫数</th>
              <th class="sticky top-0 z-10 whitespace-nowrap bg-slate-50 px-3 py-2.5 text-right font-medium">残在庫金額</th>
            </template>
          </tr>
        </thead>
        <tbody class="divide-y divide-slate-100">
          <tr
            v-for="row in rows"
            :key="row.key"
            class="hover:bg-slate-50"
            :class="clickable ? 'cursor-pointer' : ''"
            @click="onRowClick(row)"
          >
            <td class="whitespace-nowrap px-3 py-2 text-center">
              <span
                class="inline-flex h-6 min-w-6 items-center justify-center rounded-full px-1.5 text-xs font-bold tabular-nums"
                :class="rankBadgeClass(row.rank)"
              >{{ row.rank }}</span>
            </td>
            <td class="px-3 py-2 text-left text-slate-700">
              <div class="flex items-center gap-2">
                <span
                  class="inline-flex h-5 w-5 shrink-0 items-center justify-center rounded text-xs font-bold"
                  :class="TIER_BADGE[row.tier]"
                  :title="`ABC ランク ${row.tier}`"
                >{{ row.tier }}</span>
                <span class="truncate font-medium" :title="row.label">{{ row.label }}</span>
              </div>
            </td>
            <td v-if="showComposite" class="whitespace-nowrap px-3 py-2 text-right">
              <div class="flex items-center justify-end gap-1.5">
                <span class="tabular-nums font-semibold text-slate-700">{{ compositeText(row) }}</span>
                <span
                  v-if="row.compositeScore !== null"
                  class="hidden h-1.5 w-12 overflow-hidden rounded-full bg-slate-100 lg:inline-block"
                  aria-hidden="true"
                >
                  <span
                    class="block h-full rounded-full bg-indigo-500"
                    :style="{ width: `${Math.round(row.compositeScore * 100)}%` }"
                  />
                </span>
              </div>
            </td>
            <td
              v-for="key in metricColumns"
              :key="key"
              class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-600"
            >{{ metricCell(row, key) }}</td>
            <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-600">
              {{ formatPercent(row.share) }}
            </td>
            <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-500">
              {{ formatPercent(row.cumulative) }}
            </td>
            <template v-if="showComparison">
              <td class="whitespace-nowrap px-3 py-2 text-center tabular-nums text-slate-500">
                {{ row.prevRank ?? '—' }}
              </td>
              <td class="whitespace-nowrap px-3 py-2 text-center">
                <span
                  class="inline-flex items-center rounded px-1.5 py-0.5 text-xs font-semibold tabular-nums"
                  :class="deltaClass(row)"
                >{{ deltaText(row) }}</span>
              </td>
              <td
                class="whitespace-nowrap px-3 py-2 text-right tabular-nums font-medium"
                :class="growthClass(row)"
              >{{ growthText(row) }}</td>
            </template>
            <template v-if="showStockColumns">
              <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-600">{{ stockText(row) }}</td>
              <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums text-slate-600">{{ stockValueText(row) }}</td>
            </template>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- モバイル: カード -->
    <div class="space-y-2 md:hidden">
      <div
        v-for="row in rows"
        :key="row.key"
        class="rounded-xl border border-slate-200 bg-white p-3 shadow-sm"
        :class="clickable ? 'cursor-pointer hover:bg-slate-50' : ''"
        @click="onRowClick(row)"
      >
        <div class="mb-2 flex items-center gap-2">
          <span
            class="inline-flex h-6 min-w-6 items-center justify-center rounded-full px-1.5 text-xs font-bold tabular-nums"
            :class="rankBadgeClass(row.rank)"
          >{{ row.rank }}</span>
          <span
            class="inline-flex h-5 w-5 shrink-0 items-center justify-center rounded text-xs font-bold"
            :class="TIER_BADGE[row.tier]"
          >{{ row.tier }}</span>
          <span class="min-w-0 flex-1 truncate font-semibold text-slate-700" :title="row.label">{{ row.label }}</span>
          <template v-if="showComparison">
            <span
              class="inline-flex items-center rounded px-1.5 py-0.5 text-xs font-semibold tabular-nums"
              :class="deltaClass(row)"
            >{{ deltaText(row) }}</span>
          </template>
        </div>
        <div class="grid grid-cols-2 gap-x-3 gap-y-1 text-sm">
          <div v-if="showComposite" class="flex justify-between gap-2">
            <span class="text-slate-500">複合スコア</span>
            <span class="tabular-nums font-semibold text-slate-700">{{ compositeText(row) }}</span>
          </div>
          <div v-for="key in metricColumns" :key="key" class="flex justify-between gap-2">
            <span class="text-slate-500">{{ metricLabel(key) }}</span>
            <span class="tabular-nums text-slate-700">{{ metricCell(row, key) }}</span>
          </div>
          <div class="flex justify-between gap-2">
            <span class="text-slate-500">構成比 / 累積</span>
            <span class="tabular-nums text-slate-700">{{ formatPercent(row.share) }} / {{ formatPercent(row.cumulative) }}</span>
          </div>
          <div v-if="showComparison" class="flex justify-between gap-2">
            <span class="text-slate-500">成長率（{{ growthLabel }}）</span>
            <span class="tabular-nums font-medium" :class="growthClass(row)">{{ growthText(row) }}</span>
          </div>
          <div v-if="showStockColumns" class="flex justify-between gap-2">
            <span class="text-slate-500">残在庫数</span>
            <span class="tabular-nums text-slate-700">{{ stockText(row) }}</span>
          </div>
          <div v-if="showStockColumns" class="flex justify-between gap-2">
            <span class="text-slate-500">残在庫金額</span>
            <span class="tabular-nums text-slate-700">{{ stockValueText(row) }}</span>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
