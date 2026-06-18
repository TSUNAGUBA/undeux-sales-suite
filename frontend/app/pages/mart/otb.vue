<script setup lang="ts">
/**
 * 全社OTBサマリー（/mart/otb）— バイヤー向けの「未来の仕入意思決定」ダッシュボード（モック）。
 *
 * 既存の「全社サマリー（/mart）」がメーカー視点で「何が売れたか（過去・現在分析）」を見る画面
 * なのに対し、本画面はバイヤー視点で「OTB（Open To Buy＝まだ使える仕入枠）」を起点に
 * 「次に何をどれだけ仕入れるか」を判断するための画面である。
 *
 * 構成は全社サマリーのレポート体裁（Hero ボトムライン → KPI → SWOT → 推奨アクション →
 * 構成図 → 推移 → ランキング → 内訳 → AIコメント）を踏襲し、視覚言語（slate/角丸カード/
 * shadow）も統一する。ただし色の意味だけは OTB 文脈に合わせて読み替える:
 *   青 = OTB・発注余力 / 緑 = 健全 / 黄 = 注意 / 赤 = 欠品・過剰在庫・納期遅延。
 *
 * 本ファイルは UI モックのため、表示値は画面内に閉じたダミーデータ（OTB_* 定数）で構成する。
 * OTB の算出式は次のとおり（⑤ ウォーターフォールで視覚化）:
 *   OTB ＝ 目標在庫 ＋ 目標売上 － 期首在庫 － 発注残
 */
import {
  AlertOctagon,
  AlertTriangle,
  Boxes,
  CalendarClock,
  CircleDollarSign,
  Clock,
  Gauge,
  Layers,
  Lightbulb,
  Package,
  RefreshCw,
  Scale,
  ShieldAlert,
  Sparkles,
  ThumbsUp,
  TrendingUp,
  Truck,
} from 'lucide-vue-next'
import { Bar } from 'vue-chartjs'
import type { Component } from 'vue'
import type { ChartData, ChartOptions, TooltipItem } from 'chart.js'

useHead({ title: '全社OTBサマリー | UndeuxSales' })

// ---------------------------------------------------------------
// 配色（色の意味は OTB 文脈に読み替える。スクリプト・テンプレ双方で参照）。
// ---------------------------------------------------------------
const COLOR = {
  otb: '#2563eb', // 青: OTB・発注余力
  order: '#6366f1', // 藍: 発注残
  healthy: '#10b981', // 緑: 健全
  caution: '#f59e0b', // 黄: 注意
  risk: '#dc2626', // 赤: 欠品・過剰・遅延
  forecast: '#8b5cf6', // 紫: 予測在庫（中立）
} as const

/** 大きな金額の軸目盛りを「○.○億」に丸めて読みやすくする（ツールチップは全額表示）。 */
function formatOku(value: number): string {
  return `¥${(value / 1e8).toFixed(1)}億`
}

// ---------------------------------------------------------------
// 絞り込み（モック）: 業態タブ・部門チップ・年度。選択はスコープ見出しに反映する。
// 実データ接続前のモックのため、KPI・グラフ本体はダミー固定。
// ---------------------------------------------------------------
const BUSINESS_TYPES = ['sm', 'av', 'sr', 'br', 'cm', 'di']
const DEPARTMENTS = [
  '11', '13', '21', '22', '23', '24', '31', '32', '41', '43', '44',
  '51', '52', '53', '54', '55', '56', '57', '61', '71', '72', '99',
]
const YEARS = [2026, 2025, 2024]

const activeBusinessType = ref<string | null>(null)
const activeDepartment = ref<string | null>(null)
const activeYear = ref<number>(2026)

const scopeLabel = computed(() => {
  const bt = activeBusinessType.value ? activeBusinessType.value.toUpperCase() : '全業態'
  const dept = activeDepartment.value ? `部門${activeDepartment.value}` : '全部門'
  return `${bt} × ${dept}`
})

// ---------------------------------------------------------------
// ② KPI カード（ダミー）。色の意味は OTB 文脈準拠。
// ---------------------------------------------------------------
interface OtbKpi {
  label: string
  value: string
  sub: string
  icon: Component
  accentClass: string
}
const kpiItems: OtbKpi[] = [
  {
    label: 'OTB残高',
    value: '¥800,000,000',
    sub: '計画仕入枠の残余',
    icon: CircleDollarSign,
    accentClass: 'bg-blue-50 text-blue-600',
  },
  {
    label: 'OTB利用率',
    value: '62%',
    sub: '健全レンジ（50〜80%）',
    icon: Gauge,
    accentClass: 'bg-emerald-50 text-emerald-600',
  },
  {
    label: '発注残',
    value: '¥500,000,000',
    sub: '未検収の発注金額',
    icon: Package,
    accentClass: 'bg-sky-50 text-sky-600',
  },
  {
    label: '予測月末在庫',
    value: '¥1,120,000,000',
    sub: '目標 ¥1,800,000,000 を下回る',
    icon: Boxes,
    accentClass: 'bg-violet-50 text-violet-600',
  },
  {
    label: 'WOS（在庫週数）',
    value: '4.8週',
    sub: '適正圏（4〜8週）',
    icon: CalendarClock,
    accentClass: 'bg-teal-50 text-teal-600',
  },
  {
    label: '欠品リスクSKU数',
    value: '351 SKU',
    sub: '8週以内に欠品見込み',
    icon: AlertTriangle,
    accentClass: 'bg-rose-50 text-rose-600',
  },
  {
    label: '過剰在庫SKU数',
    value: '573 SKU',
    sub: 'WOS 12週超',
    icon: Layers,
    accentClass: 'bg-red-50 text-red-600',
  },
  {
    label: '平均リードタイム',
    value: '21日',
    sub: '前月比 +2日',
    icon: Clock,
    accentClass: 'bg-amber-50 text-amber-600',
  },
]

// ---------------------------------------------------------------
// 要点 ＋ ③ SWOT（ダミー）
// ---------------------------------------------------------------
const highlights = [
  'OTB残高は ¥800,000,000、利用率 62% で健全レンジ。',
  '発注残 ¥500,000,000 の内訳は未出荷 ¥300,000,000 / 輸送中 ¥200,000,000。',
  '予測月末在庫 ¥1,120,000,000 は目標 ¥1,800,000,000 を下回り、補充余地あり。',
  'WOS は全社平均 4.8週で適正。欠品リスク 351 SKU／過剰在庫 573 SKU。',
  '高回転カテゴリ（55・22・71）に追加発注余力、21・32 は発注停止を推奨。',
]

interface SwotPanel {
  key: string
  title: string
  icon: Component
  items: string[]
  cardClass: string
  iconClass: string
}
const swotPanels: SwotPanel[] = [
  {
    key: 'strengths',
    title: '強み',
    icon: ThumbsUp,
    items: [
      'OTB利用率が健全レンジ（62%）',
      '欠品率は低水準で機会損失を抑制',
      '主要カテゴリの在庫回転が良好（WOS 3〜5週）',
    ],
    cardClass: 'border-emerald-200 bg-emerald-50/50',
    iconClass: 'text-emerald-600',
  },
  {
    key: 'weaknesses',
    title: '弱み',
    icon: AlertTriangle,
    items: [
      'カテゴリ21・32 で在庫過多（WOS 11〜14週）',
      '一部カテゴリで OTB枠超過（利用率 108〜112%）',
      'リードタイムが前月比 +2日に悪化',
    ],
    cardClass: 'border-rose-200 bg-rose-50/50',
    iconClass: 'text-rose-600',
  },
  {
    key: 'opportunities',
    title: '機会',
    icon: Lightbulb,
    items: [
      '高回転カテゴリ55・22・71 に追加発注余地（OTB余力計 ¥480M）',
      '売上予測が計画比で上振れ傾向',
      '早期発注により欠品リスクを低減できる',
    ],
    cardClass: 'border-sky-200 bg-sky-50/50',
    iconClass: 'text-sky-600',
  },
  {
    key: 'threats',
    title: 'リスク',
    icon: ShieldAlert,
    items: [
      '一部発注で納期遅延が発生（超過14日）',
      '予測在庫の下振れで欠品リスク上昇',
      '過剰在庫カテゴリで値下げ・処分の圧力',
    ],
    cardClass: 'border-amber-200 bg-amber-50/50',
    iconClass: 'text-amber-600',
  },
]

// ---------------------------------------------------------------
// ④ 今週の推奨アクション（ダミー）。最重要エリアとして上部に大きく配置する。
// ---------------------------------------------------------------
interface RecommendedAction {
  key: string
  badge: string
  icon: Component
  category: string
  cardClass: string
  iconClass: string
  badgeClass: string
  metrics: { label: string; value: string; emphasis?: boolean }[]
  note: string
}
const recommendedActions: RecommendedAction[] = [
  {
    key: 'add',
    badge: '追加発注推奨',
    icon: TrendingUp,
    category: 'カテゴリ55',
    cardClass: 'border-blue-200 bg-blue-50/60',
    iconClass: 'bg-blue-100 text-blue-700',
    badgeClass: 'bg-blue-600 text-white',
    metrics: [
      { label: 'OTB余力', value: '¥120,000,000' },
      { label: '推奨発注額', value: '¥50,000,000', emphasis: true },
    ],
    note: '販売速度が直近4週で +18%。欠品前に追加発注の好機。',
  },
  {
    key: 'stop',
    badge: '発注停止推奨',
    icon: AlertOctagon,
    category: 'カテゴリ21',
    cardClass: 'border-red-200 bg-red-50/60',
    iconClass: 'bg-red-100 text-red-700',
    badgeClass: 'bg-red-600 text-white',
    metrics: [
      { label: 'WOS', value: '14週', emphasis: true },
      { label: '状態', value: '過剰在庫' },
    ],
    note: 'OTB枠超過（利用率112%）。新規発注を止め、消化を優先。',
  },
  {
    key: 'delay',
    badge: '納期遅延アラート',
    icon: Clock,
    category: '発注残の一部',
    cardClass: 'border-amber-200 bg-amber-50/60',
    iconClass: 'bg-amber-100 text-amber-700',
    badgeClass: 'bg-amber-500 text-white',
    metrics: [
      { label: '対象発注残', value: '¥50,000,000' },
      { label: '納期超過', value: '14日', emphasis: true },
    ],
    note: 'サプライヤー納期が超過。投入計画の見直しが必要。',
  },
]

// ---------------------------------------------------------------
// ⑤ OTB構成ウォーターフォール（OTB ＝ 目標在庫 ＋ 目標売上 － 期首在庫 － 発注残）。
// floating bar（data を [start, end] のタプルで与える）で増減を表現する。
// ---------------------------------------------------------------
interface WaterfallStep {
  label: string
  range: [number, number]
  delta: number
  color: string
}
const waterfallSteps: WaterfallStep[] = [
  { label: '目標在庫', range: [0, 1_800_000_000], delta: 1_800_000_000, color: COLOR.order },
  { label: '＋ 目標売上', range: [1_800_000_000, 3_300_000_000], delta: 1_500_000_000, color: COLOR.otb },
  { label: '− 期首在庫', range: [1_300_000_000, 3_300_000_000], delta: -2_000_000_000, color: COLOR.caution },
  { label: '− 発注残', range: [800_000_000, 1_300_000_000], delta: -500_000_000, color: COLOR.caution },
  { label: '＝ OTB', range: [0, 800_000_000], delta: 800_000_000, color: COLOR.healthy },
]

const waterfallData = computed<ChartData<'bar'>>(() => ({
  labels: waterfallSteps.map((s) => s.label),
  datasets: [
    {
      label: 'OTB構成',
      data: waterfallSteps.map((s) => s.range),
      backgroundColor: waterfallSteps.map((s) => s.color),
      borderRadius: 4,
      maxBarThickness: 64,
    },
  ],
}))

const waterfallOptions = computed<ChartOptions<'bar'>>(() => ({
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: { display: false },
    tooltip: {
      callbacks: {
        label: (ctx: TooltipItem<'bar'>) => {
          const step = waterfallSteps[ctx.dataIndex]
          if (!step) return ''
          const sign = step.delta < 0 ? '−' : ''
          return `${formatCurrency(Math.abs(step.delta))}（${sign}増減）`
        },
      },
    },
  },
  scales: {
    x: { ticks: { maxRotation: 0, autoSkip: false } },
    y: { beginAtZero: true, ticks: { callback: (v) => formatOku(Number(v)) } },
  },
}))

// ---------------------------------------------------------------
// ⑥ OTB推移グラフ（週次・複数系列）。LineChartCard を再利用するため、軸の可読性優先で
// 値は「億円」単位に換算して渡す（KPI 等の全額表示とは別物）。
// ---------------------------------------------------------------
const trendLabels = [
  '2026-03-02', '2026-03-09', '2026-03-16', '2026-03-23', '2026-03-30', '2026-04-06',
  '2026-04-13', '2026-04-20', '2026-04-27', '2026-05-04', '2026-05-11', '2026-05-18',
]
const trendSeries = [
  {
    label: 'OTB残高（億円）',
    data: [6.2, 5.8, 5.4, 6.9, 7.6, 8.2, 8.8, 8.4, 8.0, 7.8, 8.1, 8.0],
    color: COLOR.otb,
  },
  {
    label: '発注残（億円）',
    data: [5.6, 6.0, 6.4, 5.2, 4.7, 4.4, 4.2, 4.6, 5.0, 5.4, 5.2, 5.0],
    color: COLOR.caution,
  },
  {
    label: '月末予測在庫（億円）',
    data: [9.8, 10.1, 10.4, 10.6, 10.8, 11.0, 11.1, 11.2, 11.15, 11.18, 11.2, 11.2],
    color: COLOR.forecast,
  },
]

// ---------------------------------------------------------------
// ⑦ カテゴリ別OTBランキング（ダミー）
// ---------------------------------------------------------------
type ActionKind = 'add' | 'stop' | 'hold' | 'delay'
const ACTION_BADGES: Record<ActionKind, { label: string; className: string }> = {
  add: { label: '追加発注', className: 'bg-blue-100 text-blue-700' },
  stop: { label: '発注停止', className: 'bg-red-100 text-red-700' },
  hold: { label: '維持', className: 'bg-emerald-100 text-emerald-700' },
  delay: { label: '納期注意', className: 'bg-amber-100 text-amber-700' },
}

interface RankingRow {
  key: string
  label: string
  otb: number
  utilization: number
  wos: number
  onOrder: number
  action: ActionKind
}
const rankingRows: RankingRow[] = [
  { key: '22', label: '22', otb: 210_000_000, utilization: 41, wos: 3.9, onOrder: 30_000_000, action: 'add' },
  { key: '72', label: '72', otb: 190_000_000, utilization: 55, wos: 5.5, onOrder: 10_000_000, action: 'delay' },
  { key: '71', label: '71', otb: 150_000_000, utilization: 49, wos: 4.2, onOrder: 60_000_000, action: 'add' },
  { key: '55', label: '55', otb: 120_000_000, utilization: 58, wos: 3.2, onOrder: 180_000_000, action: 'add' },
  { key: '11', label: '11', otb: 95_000_000, utilization: 64, wos: 5.1, onOrder: 70_000_000, action: 'hold' },
  { key: '56', label: '56', otb: 80_000_000, utilization: 66, wos: 6.8, onOrder: 45_000_000, action: 'hold' },
  { key: '32', label: '32', otb: -15_000_000, utilization: 108, wos: 11.5, onOrder: 15_000_000, action: 'stop' },
  { key: '21', label: '21', otb: -30_000_000, utilization: 112, wos: 14.0, onOrder: 90_000_000, action: 'stop' },
]

const rankingChartData = computed<ChartData<'bar'>>(() => ({
  labels: rankingRows.map((r) => r.label),
  datasets: [
    {
      label: 'OTB',
      data: rankingRows.map((r) => r.otb),
      backgroundColor: rankingRows.map((r) => (r.otb < 0 ? COLOR.risk : COLOR.otb)),
      borderRadius: 4,
      maxBarThickness: 28,
    },
  ],
}))

const rankingChartOptions: ChartOptions<'bar'> = {
  responsive: true,
  maintainAspectRatio: false,
  indexAxis: 'y',
  plugins: {
    legend: { display: false },
    tooltip: {
      callbacks: { label: (ctx: TooltipItem<'bar'>) => formatCurrency(Number(ctx.raw)) },
    },
  },
  scales: {
    x: { beginAtZero: true, ticks: { callback: (v) => formatOku(Number(v)) } },
    y: { ticks: { autoSkip: false } },
  },
}

const rankingColumns = [
  { key: 'label', label: 'カテゴリ' },
  { key: 'otb', label: 'OTB', align: 'right' as const, format: (r: RankingRow) => formatCurrency(r.otb) },
  {
    key: 'utilization',
    label: 'OTB利用率',
    align: 'right' as const,
    format: (r: RankingRow) => formatPercent(r.utilization),
  },
  { key: 'wos', label: 'WOS', align: 'right' as const, format: (r: RankingRow) => `${formatDecimal(r.wos)}週` },
  { key: 'onOrder', label: '発注残', align: 'right' as const, format: (r: RankingRow) => formatCurrency(r.onOrder) },
  { key: 'action', label: '推奨アクション' },
]

// ---------------------------------------------------------------
// ⑧ 発注残分析（状態別の積み上げ棒）。
//   未出荷 = 発注 − 出荷EDI / 輸送中 = 出荷EDI − 検収 / 検収済 = 在庫計上済。
//   発注残 ＝ 未出荷 ＋ 輸送中（検収済は受領済みのため発注残には含めない・パイプライン文脈で併記）。
// ---------------------------------------------------------------
interface PipelineSegment {
  key: string
  label: string
  value: number
  color: string
  note: string
}
const pipelineSegments: PipelineSegment[] = [
  { key: 'unshipped', label: '未出荷', value: 300_000_000, color: COLOR.caution, note: '発注 − 出荷EDI' },
  { key: 'inTransit', label: '輸送中', value: 200_000_000, color: COLOR.otb, note: '出荷EDI − 検収' },
  { key: 'received', label: '検収済', value: 420_000_000, color: COLOR.healthy, note: '在庫計上済' },
]
const pipelineTotal = pipelineSegments.reduce((sum, s) => sum + s.value, 0)
const onOrderTotal = pipelineSegments
  .filter((s) => s.key !== 'received')
  .reduce((sum, s) => sum + s.value, 0)

const pipelineData = computed<ChartData<'bar'>>(() => ({
  labels: ['発注パイプライン'],
  datasets: pipelineSegments.map((s) => ({
    label: s.label,
    data: [s.value],
    backgroundColor: s.color,
    borderRadius: 4,
    maxBarThickness: 56,
  })),
}))

const pipelineOptions: ChartOptions<'bar'> = {
  responsive: true,
  maintainAspectRatio: false,
  indexAxis: 'y',
  plugins: {
    legend: { display: true, position: 'top', labels: { boxWidth: 12 } },
    tooltip: {
      callbacks: {
        label: (ctx: TooltipItem<'bar'>) => `${ctx.dataset.label}: ${formatCurrency(Number(ctx.raw))}`,
      },
    },
  },
  scales: {
    x: { stacked: true, beginAtZero: true, ticks: { callback: (v) => formatOku(Number(v)) } },
    y: { stacked: true },
  },
}

// ---------------------------------------------------------------
// ⑨ AIコメント（ダミー）
// ---------------------------------------------------------------
const aiComment =
  'カテゴリ55は直近4週間で販売速度が +18% と向上しています。OTB余力は ¥120,000,000 あり、'
  + '欠品前の追加発注（推奨額 ¥50,000,000）を推奨します。一方カテゴリ21は WOS 14週となり過剰在庫'
  + '傾向で、OTB利用率も 112% と枠超過です。新規発注を停止し消化を優先してください。'
  + '全社の予測月末在庫は目標を下回っており、高回転カテゴリ（22・71）への計画的な前倒し発注で'
  + '欠品リスク（351 SKU）を抑制できます。'
</script>

<template>
  <div class="space-y-4">
    <!-- ① 見出し -->
    <div>
      <h1 class="text-xl font-bold text-slate-800">全社OTBサマリー</h1>
      <p class="text-sm text-slate-500">
        全社・部門横断で OTB（Open To Buy＝まだ使える仕入枠）状況と在庫健全性を俯瞰し、
        次の仕入意思決定につなげます。
      </p>
    </div>

    <!-- データ状態バー（モック）。全社サマリーの再構築バーと同じ体裁。 -->
    <div class="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-slate-200 bg-white p-3">
      <div class="flex items-center gap-2 text-sm text-slate-600">
        <Scale class="h-4 w-4 shrink-0 text-blue-500" />
        <span>
          サンプルデータ表示中 ／ 最終更新: 2026/6/18 09:00
          <span class="text-slate-400">（OTB ＝ 目標在庫 ＋ 目標売上 − 期首在庫 − 発注残）</span>
        </span>
      </div>
      <button
        type="button"
        class="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-3 py-1.5 text-sm font-medium text-white transition-colors hover:bg-blue-500"
      >
        <RefreshCw class="h-4 w-4" />
        OTB を再計算
      </button>
    </div>

    <!-- OTB 定義バナー（在庫定義バナーと同じ体裁） -->
    <div class="flex items-start gap-2.5 rounded-xl border border-blue-200 bg-blue-50 p-3 text-sm text-blue-800 sm:items-center">
      <CircleDollarSign class="mt-0.5 h-4 w-4 shrink-0 sm:mt-0" />
      <p>
        <span class="font-bold">OTBの定義:</span>
        目標売上と目標在庫を達成するために、まだ使える仕入枠。
        <span class="font-semibold">OTB ＝ 目標在庫 ＋ 目標売上 − 期首在庫 − 発注残</span>
        （発注残 ＝ 発注金額 − 検収金額）。
      </p>
    </div>

    <!-- 業態タブ -->
    <nav class="border-b border-slate-200" aria-label="業態の切替">
      <ul class="scrollbar-hide -mb-px flex gap-1 overflow-x-auto">
        <li class="shrink-0">
          <button
            type="button"
            class="whitespace-nowrap border-b-2 px-3 py-2.5 text-sm font-medium transition-colors"
            :class="
              activeBusinessType === null
                ? 'border-blue-600 text-blue-700'
                : 'border-transparent text-slate-500 hover:border-slate-300 hover:text-slate-800'
            "
            :aria-current="activeBusinessType === null ? 'page' : undefined"
            @click="activeBusinessType = null"
          >
            すべて
          </button>
        </li>
        <li v-for="bt in BUSINESS_TYPES" :key="bt" class="shrink-0">
          <button
            type="button"
            class="whitespace-nowrap border-b-2 px-3 py-2.5 text-sm font-medium uppercase transition-colors"
            :class="
              activeBusinessType === bt
                ? 'border-blue-600 text-blue-700'
                : 'border-transparent text-slate-500 hover:border-slate-300 hover:text-slate-800'
            "
            :aria-current="activeBusinessType === bt ? 'page' : undefined"
            @click="activeBusinessType = bt"
          >
            {{ bt }}
          </button>
        </li>
      </ul>
    </nav>

    <!-- 部門チップ ＋ 年度 -->
    <div class="flex flex-wrap items-center gap-2">
      <span class="text-xs font-medium text-slate-400">部門</span>
      <button
        type="button"
        class="inline-flex items-center rounded-full border px-3 py-1.5 text-xs font-medium transition-colors"
        :class="
          activeDepartment === null
            ? 'border-blue-500 bg-blue-50 text-blue-700'
            : 'border-slate-200 bg-white text-slate-600 hover:border-blue-300'
        "
        @click="activeDepartment = null"
      >
        すべて
      </button>
      <button
        v-for="dept in DEPARTMENTS"
        :key="dept"
        type="button"
        class="inline-flex items-center rounded-full border px-3 py-1.5 text-xs font-medium transition-colors"
        :class="
          activeDepartment === dept
            ? 'border-blue-500 bg-blue-50 text-blue-700'
            : 'border-slate-200 bg-white text-slate-600 hover:border-blue-300'
        "
        @click="activeDepartment = dept"
      >
        {{ dept }}
      </button>
      <div class="ml-auto flex items-center gap-2">
        <label class="text-xs font-medium text-slate-400">期間（年度）</label>
        <select v-model="activeYear" class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm">
          <option v-for="y in YEARS" :key="y" :value="y">{{ y }}年</option>
        </select>
      </div>
    </div>

    <!-- ① Hero: エグゼクティブサマリー（OTB は青系グラデーション） -->
    <div class="rounded-xl border border-blue-200 bg-gradient-to-br from-blue-50 to-white p-5">
      <p class="text-xs font-semibold uppercase tracking-wider text-blue-500">エグゼクティブサマリー</p>
      <h2 class="mt-1 text-lg font-bold text-slate-800">
        {{ scopeLabel }}・{{ activeYear }}年 の全社OTBサマリー
      </h2>
      <p class="mt-2 text-sm text-slate-700">
        全社OTB残高は ¥800,000,000（利用率 62%）。高回転カテゴリ（55・22・71）に追加発注余力があり、
        一方カテゴリ21・32 は過剰在庫傾向で発注抑制が必要です。予測月末在庫は目標を下回り、
        欠品リスクは 351 SKU。納期遅延（超過14日）にも注意が必要です。
      </p>
      <p class="mt-1 text-xs text-slate-400">主要指標から自動生成した所見です（基準週: 2026-05-18）。</p>
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

    <!-- 要点 ＋ ③ SWOT -->
    <div class="grid grid-cols-1 gap-4 lg:grid-cols-3">
      <div class="rounded-xl border border-slate-200 bg-white p-4 lg:col-span-1">
        <div class="mb-2 flex items-center gap-1.5">
          <Sparkles class="h-4 w-4 text-blue-500" />
          <h3 class="text-sm font-bold text-slate-800">要点</h3>
        </div>
        <ul class="space-y-1.5">
          <li v-for="(h, i) in highlights" :key="i" class="flex gap-2 text-sm text-slate-600">
            <span class="mt-1 h-1.5 w-1.5 shrink-0 rounded-full bg-blue-400" aria-hidden="true" />
            <span>{{ h }}</span>
          </li>
        </ul>
      </div>

      <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:col-span-2">
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
    </div>

    <!-- ④ 今週の推奨アクション（最重要エリア） -->
    <div class="rounded-xl border border-slate-200 bg-white p-4">
      <div class="mb-3 flex items-baseline justify-between gap-2">
        <h2 class="text-sm font-bold text-slate-800">今週の推奨アクション</h2>
        <span class="text-xs text-slate-400">OTB余力・在庫健全性から自動抽出</span>
      </div>
      <div class="grid grid-cols-1 gap-3 md:grid-cols-3">
        <div
          v-for="action in recommendedActions"
          :key="action.key"
          class="rounded-xl border p-4"
          :class="action.cardClass"
        >
          <div class="mb-3 flex items-center justify-between gap-2">
            <span
              class="inline-flex items-center gap-1.5 rounded-lg px-2 py-1 text-xs font-bold"
              :class="action.badgeClass"
            >
              <component :is="action.icon" class="h-3.5 w-3.5" />
              {{ action.badge }}
            </span>
            <span class="text-sm font-bold text-slate-800">{{ action.category }}</span>
          </div>
          <dl class="grid grid-cols-2 gap-2">
            <div v-for="m in action.metrics" :key="m.label" class="rounded-lg bg-white/70 px-2.5 py-1.5">
              <dt class="text-xs text-slate-500">{{ m.label }}</dt>
              <dd
                class="truncate font-bold text-slate-800"
                :class="m.emphasis ? 'text-base' : 'text-sm'"
              >
                {{ m.value }}
              </dd>
            </div>
          </dl>
          <p class="mt-2.5 text-xs leading-relaxed text-slate-600">{{ action.note }}</p>
        </div>
      </div>
    </div>

    <!-- ⑤ OTB構成ウォーターフォール -->
    <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <div class="mb-1 flex items-baseline justify-between gap-2">
        <h3 class="text-sm font-semibold text-slate-700">OTB構成（ウォーターフォール）</h3>
        <span class="text-xs font-bold text-emerald-600">OTB ¥800,000,000</span>
      </div>
      <p class="mb-3 text-xs text-slate-400">
        目標在庫 ＋ 目標売上 − 期首在庫 − 発注残 ＝ OTB の算出ロジックを視覚化しています。
      </p>
      <div class="h-80">
        <Bar :data="waterfallData" :options="waterfallOptions" />
      </div>
      <div class="mt-3 flex flex-wrap items-center justify-center gap-x-2 gap-y-1 text-sm">
        <span class="font-medium text-slate-700">¥1,800,000,000<span class="ml-1 text-xs text-slate-400">目標在庫</span></span>
        <span class="text-slate-400">＋</span>
        <span class="font-medium text-slate-700">¥1,500,000,000<span class="ml-1 text-xs text-slate-400">目標売上</span></span>
        <span class="text-slate-400">−</span>
        <span class="font-medium text-slate-700">¥2,000,000,000<span class="ml-1 text-xs text-slate-400">期首在庫</span></span>
        <span class="text-slate-400">−</span>
        <span class="font-medium text-slate-700">¥500,000,000<span class="ml-1 text-xs text-slate-400">発注残</span></span>
        <span class="text-slate-400">＝</span>
        <span class="font-bold text-emerald-600">¥800,000,000<span class="ml-1 text-xs text-emerald-500">OTB</span></span>
      </div>
    </div>

    <!-- ⑥ OTB推移グラフ -->
    <LineChartCard title="週次OTB推移（億円）" :labels="trendLabels" :series="trendSeries" />

    <!-- ⑦ カテゴリ別OTBランキング -->
    <div class="space-y-2">
      <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <h3 class="mb-3 text-sm font-semibold text-slate-700">カテゴリ別OTB（億円・赤は枠超過）</h3>
        <div class="h-80">
          <Bar :data="rankingChartData" :options="rankingChartOptions" />
        </div>
      </div>

      <DataTable :columns="rankingColumns" :rows="rankingRows" :row-key="(row: RankingRow) => row.key">
        <template #otb="{ row }">
          <span :class="(row as RankingRow).otb < 0 ? 'font-semibold text-red-600' : 'text-slate-700'">
            {{ formatCurrency((row as RankingRow).otb) }}
          </span>
        </template>
        <template #utilization="{ row }">
          <span :class="(row as RankingRow).utilization > 100 ? 'font-semibold text-red-600' : 'text-slate-700'">
            {{ formatPercent((row as RankingRow).utilization) }}
          </span>
        </template>
        <template #wos="{ row }">
          <span :class="(row as RankingRow).wos > 8 ? 'font-semibold text-amber-600' : 'text-slate-700'">
            {{ formatDecimal((row as RankingRow).wos) }}週
          </span>
        </template>
        <template #action="{ row }">
          <span
            class="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium"
            :class="ACTION_BADGES[(row as RankingRow).action].className"
          >
            {{ ACTION_BADGES[(row as RankingRow).action].label }}
          </span>
        </template>
      </DataTable>
    </div>

    <!-- ⑧ 発注残分析 -->
    <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <div class="mb-1 flex items-baseline justify-between gap-2">
        <h3 class="text-sm font-semibold text-slate-700">発注残分析（状態別）</h3>
        <span class="text-xs font-bold text-blue-600">発注残 {{ formatCurrency(onOrderTotal) }}</span>
      </div>
      <p class="mb-3 text-xs text-slate-400">
        発注残 ＝ 未出荷 ＋ 輸送中（検収済は受領済みのためパイプライン文脈で併記）。
      </p>
      <div class="grid grid-cols-1 gap-4 lg:grid-cols-3">
        <div class="lg:col-span-2">
          <div class="h-40">
            <Bar :data="pipelineData" :options="pipelineOptions" />
          </div>
        </div>
        <ul class="space-y-2 lg:col-span-1">
          <li
            v-for="seg in pipelineSegments"
            :key="seg.key"
            class="flex items-center justify-between gap-2 rounded-lg border border-slate-100 bg-slate-50 px-3 py-2"
          >
            <span class="flex items-center gap-2 text-sm text-slate-700">
              <span class="h-2.5 w-2.5 shrink-0 rounded-full" :style="{ backgroundColor: seg.color }" aria-hidden="true" />
              <span>
                <span class="font-medium">{{ seg.label }}</span>
                <span class="ml-1 text-xs text-slate-400">{{ seg.note }}</span>
              </span>
            </span>
            <span class="shrink-0 text-right text-sm font-semibold tabular-nums text-slate-800">
              {{ formatCurrency(seg.value) }}
              <span class="ml-1 text-xs font-normal text-slate-400">
                {{ formatPercent((seg.value / pipelineTotal) * 100) }}
              </span>
            </span>
          </li>
        </ul>
      </div>
    </div>

    <!-- ⑨ AIコメント -->
    <div class="rounded-xl border border-blue-200 bg-gradient-to-br from-blue-50 to-white p-4">
      <div class="mb-1.5 flex items-center gap-1.5">
        <Sparkles class="h-4 w-4 text-blue-500" />
        <h3 class="text-sm font-bold text-slate-800">AIコメント</h3>
      </div>
      <p class="text-sm leading-relaxed text-slate-700">{{ aiComment }}</p>
      <p class="mt-1.5 flex items-center gap-1 text-xs text-slate-400">
        <Truck class="h-3.5 w-3.5" />
        販売速度・OTB余力・納期状況から自動生成（サンプル）。
      </p>
    </div>
  </div>
</template>
