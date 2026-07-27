/**
 * 副資材チェックの「表示カタログ」＋クライアント検証定数（フロント表示の SoT）。
 *
 * - 判定ロジック（status / judgment / findings）の SoT はバックエンド
 *   （SubsidiaryCheckService / SubsidiaryCheckJudgment）。本ファイルは表示
 *   （ラベル・アイコン・色）と、サーバ検証と同値のクライアント側事前検証の定数のみを持つ。
 * - 一覧（subsidiary-check/index.vue）と結果詳細（subsidiary-check/[checkId].vue）で
 *   共用するため、skuStatus.ts と同じくカタログとして一元化する（原則3）。
 */
import type { Component } from 'vue'
import {
  CircleCheck,
  CircleX,
  LoaderCircle,
  TriangleAlert,
} from 'lucide-vue-next'
import type {
  SubsidiaryCheckCategory,
  SubsidiaryCheckImageKind,
  SubsidiaryCheckJudgment,
  SubsidiaryCheckSeverity,
  SubsidiaryCheckStatus,
} from '~/types/api'

// ------------------------------------------------------------
// 表示カタログ
// ------------------------------------------------------------

/** チェックの処理状態の表示カタログ。 */
export const SUBSIDIARY_CHECK_STATUSES: Record<
  SubsidiaryCheckStatus,
  { label: string; icon: Component; className: string }
> = {
  processing: {
    label: '処理中',
    icon: LoaderCircle,
    className: 'bg-sky-100 text-sky-700',
  },
  completed: {
    label: '完了',
    icon: CircleCheck,
    className: 'bg-emerald-100 text-emerald-700',
  },
  failed: {
    label: '失敗',
    icon: CircleX,
    className: 'bg-rose-100 text-rose-700',
  },
}

/**
 * AI 判定の表示カタログ。className は一覧のバッジ、bannerClass は
 * 結果詳細の判定バナー（背景・枠・文字色）に使う。
 */
export const SUBSIDIARY_CHECK_JUDGMENTS: Record<
  SubsidiaryCheckJudgment,
  { label: string; icon: Component; className: string; bannerClass: string }
> = {
  pass: {
    label: '合格',
    icon: CircleCheck,
    className: 'bg-emerald-100 text-emerald-700',
    bannerClass: 'border-emerald-200 bg-emerald-50 text-emerald-800',
  },
  warn: {
    label: '要確認',
    icon: TriangleAlert,
    className: 'bg-amber-100 text-amber-700',
    bannerClass: 'border-amber-200 bg-amber-50 text-amber-800',
  },
  fail: {
    label: '要修正',
    icon: CircleX,
    className: 'bg-rose-100 text-rose-700',
    bannerClass: 'border-rose-200 bg-rose-50 text-rose-800',
  },
}

/** 指摘の重要度バッジの表示カタログ（fail=要修正 / warn=要確認 / pass=OK）。 */
export const SUBSIDIARY_FINDING_SEVERITIES: Record<
  SubsidiaryCheckSeverity,
  { label: string; icon: Component; className: string }
> = {
  fail: {
    label: '要修正',
    icon: CircleX,
    className: 'bg-rose-100 text-rose-700',
  },
  warn: {
    label: '要確認',
    icon: TriangleAlert,
    className: 'bg-amber-100 text-amber-700',
  },
  pass: {
    label: 'OK',
    icon: CircleCheck,
    className: 'bg-emerald-100 text-emerald-700',
  },
}

/** 指摘カテゴリの表示カタログ（結果詳細のセクション見出し）。 */
export const SUBSIDIARY_CHECK_CATEGORIES: Record<
  SubsidiaryCheckCategory,
  { label: string; description: string }
> = {
  layout: {
    label: 'レイアウト',
    description: 'タグの構成・配置が指示書・規定どおりか（表面の並び等）',
  },
  order: {
    label: '順番',
    description: '表示の順序が指示書どおりか（品番→サイズ→混率→洗濯表示→色落ち表示等→原産国表示→製造者、アイコンの優先順位）',
  },
  content: {
    label: '内容',
    description: '品番・サイズ・混率・原産国・洗濯絵表示等の記載内容が指示書・付属情報と一致するか、禁止用語がないか',
  },
}

/** 指摘カテゴリの表示順（結果詳細のセクション順で共通）。 */
export const SUBSIDIARY_CHECK_CATEGORY_ORDER: SubsidiaryCheckCategory[] = [
  'layout',
  'order',
  'content',
]

/** チェック画像の種別ラベル（アップロード枠・ギャラリーの見出しで共通）。 */
export const SUBSIDIARY_CHECK_IMAGE_KINDS: Record<SubsidiaryCheckImageKind, { label: string }> = {
  instruction: { label: '指示書' },
  tag: { label: 'タグ' },
}

/** 付属情報のフィールドキー（updatedAt を除く表示対象項目）。 */
export type MasterProductAttachmentFieldKey =
  | 'composition'
  | 'originCountry'
  | 'careLabels'
  | 'colorFastnessNote'
  | 'displayOrder'
  | 'qualityNotes'

/**
 * 付属情報（m_product_attachment）の項目ラベル（表示順つき）。
 * 商品マスタ詳細の「付属情報」セクションとチェック結果詳細の商品カードで共用する。
 */
export const SUBSIDIARY_ATTACHMENT_FIELDS: ReadonlyArray<{
  key: MasterProductAttachmentFieldKey
  label: string
}> = [
  { key: 'composition', label: '組成・混率' },
  { key: 'originCountry', label: '原産国' },
  { key: 'careLabels', label: '洗濯表示' },
  { key: 'colorFastnessNote', label: '色落ち注意' },
  { key: 'displayOrder', label: '表示順序' },
  { key: 'qualityNotes', label: '注意事項' },
]

// ------------------------------------------------------------
// アップロードのクライアント検証定数（サーバ側検証と同値。SoT はバックエンド）
// ------------------------------------------------------------

/** 指示書画像の枚数上限。 */
export const SUBSIDIARY_INSTRUCTION_MAX_COUNT = 3
/** タグ画像の枚数上限。 */
export const SUBSIDIARY_TAG_MAX_COUNT = 10
/** 画像1枚のサイズ上限（5MB）。 */
export const SUBSIDIARY_IMAGE_MAX_BYTES = 5 * 1024 * 1024
/**
 * 全画像（指示書＋タグ）の合計サイズ上限（20MB）。
 * SoT はバックエンド SubsidiaryCheckService.MaxTotalImageBytes（Anthropic API の
 * リクエスト32MB制限・base64膨張約1.33倍に対する安全値）。変更時は両者を同期させること。
 */
export const SUBSIDIARY_TOTAL_IMAGE_MAX_BYTES = 20 * 1024 * 1024
/** 受け付ける画像の MIME タイプ。 */
export const SUBSIDIARY_IMAGE_TYPES = ['image/jpeg', 'image/png'] as const
/** input[accept] に渡す受付形式。 */
export const SUBSIDIARY_IMAGE_ACCEPT = SUBSIDIARY_IMAGE_TYPES.join(',')

/**
 * processing のまま経過した場合に「孤児」とみなし再実行導線を出すまでの時間（10分）。
 * SoT はバックエンド SubsidiaryCheckService.ProcessingStaleAfter（rerun の許可条件）。
 * 変更時は両者を同期させること。
 */
export const SUBSIDIARY_PROCESSING_STALE_MS = 10 * 60 * 1000
