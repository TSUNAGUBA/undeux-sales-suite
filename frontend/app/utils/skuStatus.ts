/**
 * 在庫健全性・SKUバッジ・推奨アクションの「表示カタログ」（フロント表示の SoT）。
 *
 * - 判定ロジックの SoT はバックエンド（InventoryHealthRules）。閾値は API レスポンスの
 *   thresholds から描画するため、本ファイルには閾値リテラルを置かない。
 * - 在庫マネジメントの状態（INVENTORY_STATUSES。サーバ判定＝スナップショット実測）と
 *   商品マスタ詳細の SKU バッジ（SKU_BADGES。クライアント推計＝選択期間の販売ペース由来）は
 *   入力データが異なる別概念のため判定は統合しない。表示（ラベル・アイコン・色）のみ
 *   本カタログに一元化する。
 */
import type { Component } from 'vue'
import {
  Activity,
  AlertOctagon,
  AlertTriangle,
  CheckCircle,
  Clock,
  Minus,
  Snowflake,
  TrendingUp,
} from 'lucide-vue-next'
import type {
  InventoryActionSeverity,
  InventoryActionTargetTab,
  InventoryFlagStatus,
  InventoryFlagType,
  InventoryStatus,
  RecommendedActionCode,
} from '~/types/api'

/** バッジ系の表示定義（ラベル・アイコン・色クラス）。 */
export interface BadgePresentation {
  label: string
  icon: Component
  className: string
}

/** 在庫マネジメントの状態（サーバ判定）の表示カタログ。 */
export const INVENTORY_STATUSES: Record<InventoryStatus, BadgePresentation & { description: string }> = {
  healthy: {
    label: '健全',
    icon: CheckCircle,
    className: 'bg-emerald-100 text-emerald-700',
    description: '在庫日数・出荷ペースとも問題なし',
  },
  caution: {
    label: '注意',
    icon: AlertTriangle,
    className: 'bg-amber-100 text-amber-700',
    description: '在庫日数が長め（滞留の予備軍）',
  },
  stagnant: {
    label: '滞留',
    icon: AlertOctagon,
    className: 'bg-rose-100 text-rose-700',
    description: '在庫日数超過かつ消化率が低い',
  },
  dormant: {
    label: '不動',
    icon: Snowflake,
    className: 'bg-slate-200 text-slate-700',
    description: '出荷ゼロが継続（在庫あり）',
  },
}

/** 状態の表示順（タブ・チップ・凡例で共通）。 */
export const INVENTORY_STATUS_ORDER: InventoryStatus[] = [
  'healthy',
  'caution',
  'stagnant',
  'dormant',
]

/** 鮮度積上げ帯・凡例で使う状態カラー（Tailwind の bg クラス）。 */
export const INVENTORY_STATUS_BAR_CLASSES: Record<InventoryStatus, string> = {
  healthy: 'bg-emerald-500',
  caution: 'bg-amber-400',
  stagnant: 'bg-rose-500',
  dormant: 'bg-slate-500',
}

/** アクションフィードの重大度表示（アイコンブロックの色）。 */
export const ACTION_SEVERITIES: Record<InventoryActionSeverity, { icon: Component; className: string }> = {
  danger: { icon: AlertOctagon, className: 'bg-rose-50 text-rose-600' },
  warning: { icon: AlertTriangle, className: 'bg-amber-50 text-amber-600' },
  info: { icon: CheckCircle, className: 'bg-emerald-50 text-emerald-600' },
}

/** 推奨アクションコードの表示カタログ。to があれば該当機能への導線を併設する。 */
export const RECOMMENDED_ACTIONS: Record<RecommendedActionCode, { label: string; description: string; to?: string }> = {
  'reduce-order': {
    label: '発注抑制の検討',
    description: '発注残が残ったまま滞留・不動化しています。仕入れを止めるだけで改善が見込めます。',
  },
  'markdown-candidate': {
    label: '値下げ候補',
    description: '散布図（消化率×値引き率）の4象限で値下げ判断を確認できます。',
    to: '/mart/scatter?mode=markdown',
  },
  'review-shelf': {
    label: '売場・棚割の再点検',
    description: '出荷が止まっています。売場露出・棚割の確認を推奨します。',
  },
  'clearance': {
    label: '処分・値引き販売の検討',
    description: '不動化が長期化しています。処分・値引き販売の判断対象です。',
  },
  'observe': {
    label: '経過観察',
    description: '在庫日数が長めです。来週の取込後に再確認してください。',
  },
}

/** アクションフィードの遷移先タブ → CTA ラベル。 */
export const ACTION_TARGET_LABELS: Record<InventoryActionTargetTab, string> = {
  dashboard: 'ダッシュボードを見る',
  items: '在庫一覧を見る',
  stagnant: '滞留一覧を見る',
  dormant: '不動一覧を見る',
}

/** 在庫アクションフラグ種別の表示カタログ（コードの SoT はバックエンド InventoryFlagRules）。 */
export const FLAG_TYPES: Record<InventoryFlagType, { label: string; className: string }> = {
  'stop-order': {
    label: '発注停止候補',
    className: 'bg-rose-50 text-rose-700 ring-1 ring-rose-200',
  },
  'markdown': {
    label: '値下げ候補',
    className: 'bg-indigo-50 text-indigo-700 ring-1 ring-indigo-200',
  },
}

/** フラグ種別の表示順。 */
export const FLAG_TYPE_ORDER: InventoryFlagType[] = ['stop-order', 'markdown']

/** フラグ対応状況の表示カタログ。 */
export const FLAG_STATUSES: Record<InventoryFlagStatus, { label: string; className: string }> = {
  'candidate': { label: '候補', className: 'bg-slate-100 text-slate-600' },
  'in-progress': { label: '対応中', className: 'bg-amber-100 text-amber-700' },
  'done': { label: '対応済', className: 'bg-emerald-100 text-emerald-700' },
  'dismissed': { label: '見送り', className: 'bg-slate-200 text-slate-500' },
}

/** フラグ対応状況の表示順（セレクト・チップで共通）。 */
export const FLAG_STATUS_ORDER: InventoryFlagStatus[] = [
  'candidate',
  'in-progress',
  'done',
  'dismissed',
]

/** 商品マスタ詳細ページの SKU バッジ種別（クライアント推計による分類）。 */
export type SkuBadgeKind =
  | 'hot' // 売れ筋
  | 'near-stockout' // 在庫切れ間近
  | 'stagnant' // 要警戒（在庫あり・期間内売上ゼロ）
  | 'sold-out' // 完売
  | 'aging' // 滞留疑い（推計在庫日数が高め）
  | 'inactive' // 期間内活動なし（売上ゼロ・在庫ゼロ）
  | 'normal' // 通常

/**
 * SKU バッジの表示カタログ。判定（kind の決定）は商品マスタ詳細ページ側の
 * クライアント推計ロジックが担い、表現（ラベル・アイコン・色）はここが SoT。
 */
export const SKU_BADGES: Record<SkuBadgeKind, BadgePresentation> = {
  'hot': {
    label: '売れ筋',
    icon: TrendingUp,
    className: 'bg-emerald-100 text-emerald-700',
  },
  'near-stockout': {
    label: '在庫切れ間近',
    icon: AlertTriangle,
    className: 'bg-amber-100 text-amber-700',
  },
  'stagnant': {
    label: '要警戒（滞留）',
    icon: AlertOctagon,
    className: 'bg-rose-100 text-rose-700',
  },
  'sold-out': {
    label: '完売',
    icon: CheckCircle,
    className: 'bg-slate-100 text-slate-700',
  },
  'aging': {
    label: '滞留疑い',
    icon: Clock,
    className: 'bg-amber-50 text-amber-700',
  },
  'inactive': {
    label: '期間内活動なし',
    icon: Minus,
    // text-slate-500 で WCAG AA を確保（slate-50 上で 4.5:1+）。
    className: 'bg-slate-50 text-slate-500',
  },
  'normal': {
    label: '通常',
    icon: Activity,
    className: 'bg-slate-50 text-slate-600',
  },
}
