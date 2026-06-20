<script setup lang="ts">
/**
 * 全社OTBサマリー（/mart/otb）— バイヤー（小売）向けの「未来の仕入意思決定」ダッシュボード。
 *
 * メーカー向け売上サマリー（/mart）が「売れた結果（過去・現在）」を見る画面なのに対し、本画面は
 * OTB（Open To Buy＝まだ使える仕入枠）を軸に未来の発注を意思決定する対の画面。構成は売上サマリーの
 * デザインテイスト（カード型KPI → 分析サマリー(SWOT) → アクション提案 → グラフ → ランキング）を踏襲する。
 *
 * 色の意味（本画面の方針）: 青=OTB・発注余力／緑=健全／黄=注意／赤=欠品・過剰在庫・納期遅延。
 *
 * AI は後続で本格導入予定のため、数値・所見は現状ルールベースのモック（utils/otbMock が SoT）。
 * 目標売上・仕入予算は予算管理（useBudget）の登録値があれば反映し、OTB 残高・利用率が連動する。
 */
import {
  AlertTriangle,
  ArrowDownCircle,
  ArrowUpCircle,
  Boxes,
  ClipboardList,
  Clock,
  Gauge,
  Layers,
  Lightbulb,
  PackageX,
  ShieldAlert,
  Sparkles,
  Target,
  ThumbsUp,
  Truck,
  Wallet,
} from 'lucide-vue-next'
import type { Component } from 'vue'
import type { KpiCardItem } from '~/types/api'
import type { OtbActionType, OtbCategory } from '~/utils/otbMock'

useHead({ title: '全社OTBサマリー | UndeuxSales' })

const { load: loadBudget, companyBudget } = useBudget()

// 期間（年度）。予算管理の登録年度と連動させ、登録値を OTB に反映する。
const currentYear = new Date().getFullYear()
const years = [currentYear - 1, currentYear, currentYear + 1]
const year = ref(currentYear)

onMounted(() => {
  loadBudget()
})

const summary = computed(() => {
  const budget = companyBudget(year.value)
  return buildOtbSummary({
    salesBudget: budget?.salesBudget ?? null,
    purchaseBudget: budget?.purchaseBudget ?? null,
  })
})

const budgetApplied = computed(() => companyBudget(year.value) !== undefined)

// 標準リードタイム（日）。これを超えた分を「納期超過」とみなす（ルールベース）。
const STANDARD_LEAD_TIME = 16

// ---------------------------------------------------------------
// ② KPI カード（色の意味に沿ってアクセント色を出し分ける）
// ---------------------------------------------------------------
const ACCENT: Record<'blue' | 'green' | 'amber' | 'red' | 'slate', string> = {
  blue: 'bg-blue-50 text-blue-600',
  green: 'bg-emerald-50 text-emerald-600',
  amber: 'bg-amber-50 text-amber-600',
  red: 'bg-rose-50 text-rose-600',
  slate: 'bg-slate-100 text-slate-600',
}

function usageAccent(rate: number): keyof typeof ACCENT {
  if (rate < 0.75) return 'green'
  if (rate < 0.9) return 'amber'
  return 'red'
}
function wosAccent(wos: number): keyof typeof ACCENT {
  if (wos >= 2 && wos <= 8) return 'green'
  if (wos <= 12) return 'amber'
  return 'red'
}

const kpiItems = computed<KpiCardItem[]>(() => {
  const s = summary.value
  return [
    { label: 'OTB残高', value: formatCurrency(s.otbBalance), icon: Wallet, accentClass: ACCENT.blue, sub: 'まだ使える仕入枠' },
    { label: 'OTB利用率', value: formatRatioAsPercent(s.otbUsageRate), icon: Gauge, accentClass: ACCENT[usageAccent(s.otbUsageRate)], sub: `仕入予算 ${formatCurrency(s.purchaseBudget)}` },
    { label: '発注残', value: formatCurrency(s.backlog), icon: ClipboardList, accentClass: ACCENT.blue, sub: '発注 − 検収' },
    { label: '予測月末在庫', value: formatCurrency(s.forecastEomInventory), icon: Boxes, accentClass: ACCENT.slate, sub: `目標在庫 ${formatCurrency(s.targetInventory)}` },
    { label: 'WOS（週)', value: `${formatDecimal(s.wos, 1)} 週`, icon: Clock, accentClass: ACCENT[wosAccent(s.wos)], sub: 'Weeks of Supply' },
    { label: '欠品リスクSKU数', value: `${formatNumber(s.stockoutRiskSku)} SKU`, icon: AlertTriangle, accentClass: ACCENT.red },
    { label: '過剰在庫SKU数', value: `${formatNumber(s.excessStockSku)} SKU`, icon: Layers, accentClass: ACCENT.red },
    { label: '平均リードタイム', value: `${formatNumber(s.averageLeadTime)} 日`, icon: Truck, accentClass: ACCENT.slate },
  ]
})

// ---------------------------------------------------------------
// ③ SWOT（強み・機会・弱み・リスク）— 主要指標からルールベースで生成
// ---------------------------------------------------------------
interface SwotPanel {
  key: string
  title: string
  icon: Component
  items: string[]
  cardClass: string
  iconClass: string
}

const topAddCategory = computed<OtbCategory | null>(() => {
  const adds = summary.value.categories.filter((c) => c.action === 'add')
  return adds.length > 0 ? adds.reduce((a, b) => (b.otb > a.otb ? b : a)) : null
})
const topExcessCategory = computed<OtbCategory>(() =>
  summary.value.categories.reduce((a, b) => (b.wos > a.wos ? b : a)),
)
const topLeadTimeCategory = computed<OtbCategory>(() =>
  summary.value.categories.reduce((a, b) => (b.leadTimeDays > a.leadTimeDays ? b : a)),
)

const swotPanels = computed<SwotPanel[]>(() => {
  const s = summary.value
  const add = topAddCategory.value
  const excess = topExcessCategory.value
  const lead = topLeadTimeCategory.value
  const overOtb = s.categories.filter((c) => c.otb < 0)

  const strengths: string[] = []
  if (s.otbUsageRate < 0.75) strengths.push(`OTB利用率は${formatRatioAsPercent(s.otbUsageRate)}と健全な水準です`)
  strengths.push(`欠品率は低水準（欠品リスク ${formatNumber(s.stockoutRiskSku)} SKU）を維持しています`)

  const opportunities: string[] = []
  if (add) opportunities.push(`高回転カテゴリ（${add.name}）に追加発注の余地があります`)
  opportunities.push('売上予測は計画を上回る見込みで、OTB枠の有効活用が狙えます')

  const weaknesses: string[] = []
  weaknesses.push(`特定カテゴリ（${excess.name}）で在庫が過多です（WOS ${formatDecimal(excess.wos, 1)}週）`)
  weaknesses.push(overOtb.length > 0 ? `OTBを超過したカテゴリが${overOtb.length}件あります` : '一部カテゴリで OTB 利用率が高止まりしています')

  const threats: string[] = []
  threats.push(`納期遅延が発生しています（${lead.name} リードタイム ${lead.leadTimeDays}日）`)
  threats.push('高回転カテゴリで欠品リスクが上昇する可能性があります')

  return [
    { key: 'strengths', title: '強み', icon: ThumbsUp, items: strengths, cardClass: 'border-emerald-200 bg-emerald-50/50', iconClass: 'text-emerald-600' },
    { key: 'opportunities', title: '機会', icon: Lightbulb, items: opportunities, cardClass: 'border-blue-200 bg-blue-50/50', iconClass: 'text-blue-600' },
    { key: 'weaknesses', title: '弱み', icon: AlertTriangle, items: weaknesses, cardClass: 'border-rose-200 bg-rose-50/50', iconClass: 'text-rose-600' },
    { key: 'threats', title: 'リスク', icon: ShieldAlert, items: threats, cardClass: 'border-amber-200 bg-amber-50/50', iconClass: 'text-amber-600' },
  ]
})

// ---------------------------------------------------------------
// ④ 今週の推奨アクション（最重要エリア）
// ---------------------------------------------------------------
const delayExcessDays = computed(() => Math.max(0, topLeadTimeCategory.value.leadTimeDays - STANDARD_LEAD_TIME))

// ---------------------------------------------------------------
// ⑤ OTB構成ウォーターフォール（目標在庫 ＋目標売上 －期首在庫 －発注残 ＝ OTB）
// 各ステップの running 値から、浮遊バーの下端/高さ（最大値に対する%）を算出する。
// ---------------------------------------------------------------
interface WaterfallBar {
  label: string
  kind: 'base' | 'add' | 'sub' | 'total'
  value: number
  /** バー下端の位置（%）。 */
  bottomPct: number
  /** バーの高さ（%）。 */
  heightPct: number
}

const waterfallBars = computed<WaterfallBar[]>(() => {
  const steps = summary.value.waterfall
  let running = 0
  const segments = steps.map((step) => {
    let bottom: number
    let top: number
    if (step.kind === 'base') {
      bottom = 0
      top = step.value
      running = step.value
    } else if (step.kind === 'total') {
      bottom = 0
      top = running
    } else {
      // add: running → running+value／sub: value は負。running+value → running。
      const next = running + step.value
      bottom = Math.min(running, next)
      top = Math.max(running, next)
      running = next
    }
    return { label: step.label, kind: step.kind, value: step.value, bottom, top }
  })
  const max = Math.max(...segments.map((s) => s.top), 1)
  return segments.map((s) => ({
    label: s.label,
    kind: s.kind,
    value: s.value,
    bottomPct: (s.bottom / max) * 100,
    heightPct: Math.max(((s.top - s.bottom) / max) * 100, 0.5),
  }))
})

const WATERFALL_COLOR: Record<WaterfallBar['kind'], string> = {
  base: 'bg-slate-400',
  add: 'bg-emerald-400',
  sub: 'bg-rose-400',
  total: 'bg-blue-600',
}

// ---------------------------------------------------------------
// ⑥ OTB推移グラフ（週次・複数系列の折れ線）
// ---------------------------------------------------------------
const trendLabels = computed(() => summary.value.trend.map((p) => p.week))
const trendSeries = computed(() => {
  const trend = summary.value.trend
  return [
    { label: 'OTB残高', data: trend.map((p) => p.otb), color: '#2563eb' },
    { label: '発注残', data: trend.map((p) => p.backlog), color: '#0ea5e9' },
    { label: '月末予測在庫', data: trend.map((p) => p.forecastInventory), color: '#64748b' },
  ]
})

// ---------------------------------------------------------------
// ⑦ カテゴリ別OTBランキング
// ---------------------------------------------------------------
const ACTION_META: Record<OtbActionType, { label: string; class: string }> = {
  add: { label: '追加発注', class: 'bg-blue-50 text-blue-700' },
  stop: { label: '発注停止', class: 'bg-rose-50 text-rose-700' },
  hold: { label: '維持', class: 'bg-slate-100 text-slate-600' },
}

const rankedCategories = computed(() =>
  [...summary.value.categories].sort((a, b) => b.otb - a.otb),
)

const categoryColumns = [
  { key: 'name', label: 'カテゴリ' },
  { key: 'otb', label: 'OTB', align: 'right' as const, format: (r: OtbCategory) => formatCurrency(r.otb) },
  { key: 'otbUsageRate', label: 'OTB利用率', align: 'right' as const, format: (r: OtbCategory) => formatRatioAsPercent(r.otbUsageRate) },
  { key: 'wos', label: 'WOS', align: 'right' as const, format: (r: OtbCategory) => `${formatDecimal(r.wos, 1)}週` },
  { key: 'backlog', label: '発注残', align: 'right' as const, format: (r: OtbCategory) => formatCurrency(r.backlog) },
  { key: 'action', label: '推奨アクション' },
]

// ---------------------------------------------------------------
// ⑧ 発注残分析（未出荷／輸送中／検収済の積み上げ）
// ---------------------------------------------------------------
const backlogSegments = computed(() => {
  const b = summary.value.backlogBreakdown
  const total = Math.max(b.unshipped + b.inTransit + b.received, 1)
  return [
    { key: 'unshipped', label: '未出荷', hint: '発注 − 出荷EDI', value: b.unshipped, pct: (b.unshipped / total) * 100, barClass: 'bg-amber-400', dotClass: 'bg-amber-400' },
    { key: 'inTransit', label: '輸送中', hint: '出荷EDI − 検収', value: b.inTransit, pct: (b.inTransit / total) * 100, barClass: 'bg-sky-400', dotClass: 'bg-sky-400' },
    { key: 'received', label: '検収済', hint: '在庫計上済', value: b.received, pct: (b.received / total) * 100, barClass: 'bg-emerald-400', dotClass: 'bg-emerald-400' },
  ]
})
</script>

<template>
  <div class="space-y-4">
    <!-- 見出し ＋ 期間 -->
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div>
        <h1 class="text-xl font-bold text-slate-800">全社OTBサマリー</h1>
        <p class="text-sm text-slate-500">
          全社・部門横断でOTB（仕入枠）状況と在庫健全性を俯瞰し、未来の仕入を意思決定します。
        </p>
      </div>
      <div class="flex items-center gap-2">
        <label class="text-xs font-medium text-slate-400">期間（年度）</label>
        <select v-model.number="year" class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm">
          <option v-for="y in years" :key="y" :value="y">{{ y }}年</option>
        </select>
      </div>
    </div>

    <!-- ① エグゼクティブサマリー（青テーマ）＋ ⑨ AIコメント -->
    <div class="rounded-xl border border-blue-200 bg-gradient-to-br from-blue-50 to-white p-5">
      <div class="flex flex-wrap items-start justify-between gap-3">
        <div class="min-w-0">
          <p class="text-xs font-semibold uppercase tracking-wider text-blue-500">エグゼクティブサマリー</p>
          <h2 class="mt-1 text-lg font-bold text-slate-800">
            {{ year }}年度・全社OTBと在庫健全性の俯瞰
          </h2>
          <p class="mt-1 text-xs text-slate-400">
            {{ budgetApplied
              ? '予算管理に登録した売上予算・仕入予算を反映しています。'
              : '予算未登録のため標準値で表示中（データ管理＞予算管理で登録すると反映されます）。' }}
          </p>
        </div>
        <div class="flex min-w-0 max-w-md items-start gap-2 rounded-lg border border-blue-100 bg-white/70 p-3">
          <Sparkles class="mt-0.5 h-4 w-4 shrink-0 text-blue-500" />
          <div class="min-w-0">
            <p class="text-xs font-bold text-slate-700">AIコメント</p>
            <p class="mt-0.5 text-xs text-slate-600">{{ summary.aiComment }}</p>
          </div>
        </div>
      </div>
    </div>

    <!-- ② KPI -->
    <div class="grid grid-cols-2 gap-3 md:grid-cols-4">
      <KpiCard
        v-for="item in kpiItems"
        :key="item.label"
        :label="item.label"
        :value="item.value"
        :icon="item.icon"
        :accent-class="item.accentClass"
        :sub="item.sub"
      />
    </div>

    <!-- ③ SWOT -->
    <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
      <div v-for="panel in swotPanels" :key="panel.key" class="rounded-xl border p-3" :class="panel.cardClass">
        <div class="mb-1.5 flex items-center gap-1.5">
          <component :is="panel.icon" class="h-4 w-4" :class="panel.iconClass" />
          <h4 class="text-sm font-bold text-slate-800">{{ panel.title }}</h4>
        </div>
        <ul class="space-y-1">
          <li v-for="(item, i) in panel.items" :key="i" class="text-xs text-slate-600">{{ item }}</li>
        </ul>
      </div>
    </div>

    <!-- ④ 今週の推奨アクション -->
    <div class="rounded-xl border border-slate-200 bg-white p-4">
      <h2 class="mb-3 text-sm font-bold text-slate-800">今週の推奨アクション（最重要エリア）</h2>
      <div class="grid grid-cols-1 gap-3 md:grid-cols-3">
        <!-- 追加発注推奨 -->
        <div class="rounded-xl border border-blue-200 bg-blue-50/50 p-3">
          <div class="mb-1 flex items-center gap-1.5 text-blue-700">
            <ArrowUpCircle class="h-4 w-4" />
            <span class="text-sm font-bold">追加発注推奨</span>
          </div>
          <template v-if="topAddCategory">
            <p class="text-sm font-semibold text-slate-800">{{ topAddCategory.name }}</p>
            <dl class="mt-1 space-y-0.5 text-xs text-slate-600">
              <div class="flex justify-between gap-2"><dt>OTB余力</dt><dd class="font-medium text-blue-700">{{ formatCurrency(topAddCategory.otb) }}</dd></div>
              <div class="flex justify-between gap-2"><dt>推奨発注額</dt><dd class="font-medium">{{ formatCurrency(topAddCategory.recommendedOrder) }}</dd></div>
            </dl>
          </template>
          <p v-else class="text-xs text-slate-400">追加発注を推奨するカテゴリはありません。</p>
        </div>

        <!-- 発注停止推奨 -->
        <div class="rounded-xl border border-rose-200 bg-rose-50/50 p-3">
          <div class="mb-1 flex items-center gap-1.5 text-rose-700">
            <ArrowDownCircle class="h-4 w-4" />
            <span class="text-sm font-bold">発注停止推奨</span>
          </div>
          <p class="text-sm font-semibold text-slate-800">{{ topExcessCategory.name }}</p>
          <dl class="mt-1 space-y-0.5 text-xs text-slate-600">
            <div class="flex justify-between gap-2"><dt>WOS</dt><dd class="font-medium text-rose-700">{{ formatDecimal(topExcessCategory.wos, 1) }}週</dd></div>
            <div class="flex justify-between gap-2"><dt>過剰在庫</dt><dd class="font-medium">{{ formatNumber(topExcessCategory.excessStockSku) }} SKU</dd></div>
          </dl>
        </div>

        <!-- 納期遅延アラート -->
        <div class="rounded-xl border border-amber-200 bg-amber-50/50 p-3">
          <div class="mb-1 flex items-center gap-1.5 text-amber-700">
            <Truck class="h-4 w-4" />
            <span class="text-sm font-bold">納期遅延アラート</span>
          </div>
          <p class="text-sm font-semibold text-slate-800">{{ topLeadTimeCategory.name }}</p>
          <dl class="mt-1 space-y-0.5 text-xs text-slate-600">
            <div class="flex justify-between gap-2"><dt>発注残</dt><dd class="font-medium">{{ formatCurrency(topLeadTimeCategory.backlog) }}</dd></div>
            <div class="flex justify-between gap-2"><dt>納期超過</dt><dd class="font-medium text-amber-700">{{ delayExcessDays }}日</dd></div>
          </dl>
        </div>
      </div>
    </div>

    <!-- ⑤ OTB構成ウォーターフォール ＋ ⑧ 発注残分析 -->
    <div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
      <div class="rounded-xl border border-slate-200 bg-white p-4">
        <h3 class="mb-1 text-sm font-semibold text-slate-700">OTB構成（ウォーターフォール）</h3>
        <p class="mb-3 text-xs text-slate-400">目標在庫 ＋ 目標売上 － 期首在庫 － 発注残 ＝ OTB残高</p>
        <div class="flex h-56 items-end gap-2 sm:gap-3">
          <div v-for="bar in waterfallBars" :key="bar.label" class="flex h-full flex-1 flex-col items-center">
            <div class="relative h-full w-full">
              <div
                class="absolute w-full rounded"
                :class="WATERFALL_COLOR[bar.kind]"
                :style="{ bottom: bar.bottomPct + '%', height: bar.heightPct + '%' }"
                :title="`${bar.label}: ${formatCurrency(Math.abs(bar.value))}`"
              />
            </div>
            <span class="mt-1 truncate text-[10px] text-slate-500" :title="bar.label">{{ bar.label }}</span>
            <span class="text-[10px] font-medium tabular-nums text-slate-700">{{ formatCurrency(Math.abs(bar.value)) }}</span>
          </div>
        </div>
      </div>

      <div class="rounded-xl border border-slate-200 bg-white p-4">
        <h3 class="mb-1 text-sm font-semibold text-slate-700">発注残分析（状態別）</h3>
        <p class="mb-3 text-xs text-slate-400">発注残 {{ formatCurrency(summary.backlog) }} の状態内訳</p>
        <div class="flex h-5 w-full overflow-hidden rounded-full">
          <div
            v-for="seg in backlogSegments"
            :key="seg.key"
            class="h-full"
            :class="seg.barClass"
            :style="{ width: seg.pct + '%' }"
            :title="`${seg.label}: ${formatCurrency(seg.value)}`"
          />
        </div>
        <ul class="mt-4 space-y-2">
          <li v-for="seg in backlogSegments" :key="seg.key" class="flex items-center justify-between gap-3 text-sm">
            <span class="flex items-center gap-2 text-slate-600">
              <span class="h-2.5 w-2.5 shrink-0 rounded-full" :class="seg.dotClass" />
              {{ seg.label }}
              <span class="text-xs text-slate-400">（{{ seg.hint }}）</span>
            </span>
            <span class="tabular-nums font-medium text-slate-700">
              {{ formatCurrency(seg.value) }}
              <span class="ml-1 text-xs text-slate-400">{{ formatPercent(seg.pct) }}</span>
            </span>
          </li>
        </ul>
      </div>
    </div>

    <!-- ⑥ OTB推移グラフ -->
    <LineChartCard title="OTB推移（週次）" :labels="trendLabels" :series="trendSeries" />

    <!-- ⑦ カテゴリ別OTBランキング -->
    <div class="space-y-1">
      <h3 class="text-sm font-semibold text-slate-700">カテゴリ別OTBランキング</h3>
      <DataTable :columns="categoryColumns" :rows="rankedCategories" :row-key="(row: OtbCategory) => row.code">
        <template #action="{ row }">
          <span class="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium" :class="ACTION_META[(row as OtbCategory).action].class">
            <PackageX v-if="(row as OtbCategory).action === 'stop'" class="h-3 w-3" />
            <Target v-else-if="(row as OtbCategory).action === 'add'" class="h-3 w-3" />
            {{ ACTION_META[(row as OtbCategory).action].label }}
          </span>
        </template>
      </DataTable>
    </div>
  </div>
</template>
