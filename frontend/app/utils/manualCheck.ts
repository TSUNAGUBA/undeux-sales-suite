/**
 * 手入力チェックの「表示カタログ」＋クライアント検証定数（フロント表示の SoT）。
 *
 * - 突合ロジック（verdict / judgment）の SoT はバックエンド（ManualCheckComparer）。
 *   本ファイルは表示（ラベル・アイコン・色）とクライアント側事前検証の定数のみを持つ。
 * - 画像アップロードの上限（サイズ・形式・合計）は副資材チェックと共有するため
 *   utils/subsidiaryCheck.ts の定数を再利用する（原則3。Nuxt の自動 import 経由で参照）。
 */
import type { Component } from 'vue'
import {
  CircleCheck,
  CircleHelp,
  CircleX,
  TriangleAlert,
} from 'lucide-vue-next'
import type {
  ManualCheckVerdict,
  ManualFieldCompareMode,
  ManualFieldValueType,
} from '~/types/api'

/** 突合判定（項目単位）の表示カタログ。 */
export const MANUAL_CHECK_VERDICTS: Record<
  ManualCheckVerdict,
  { label: string; icon: Component; className: string }
> = {
  match: {
    label: '一致',
    icon: CircleCheck,
    className: 'bg-emerald-100 text-emerald-700',
  },
  mismatch: {
    label: '相違',
    icon: CircleX,
    className: 'bg-rose-100 text-rose-700',
  },
  warn: {
    label: '要確認',
    icon: TriangleAlert,
    className: 'bg-amber-100 text-amber-700',
  },
  missing: {
    label: 'タグに無し',
    icon: CircleHelp,
    className: 'bg-slate-100 text-slate-600',
  },
}

/**
 * 突合判定の表示定義を返す（未知の値でも未定義参照で描画ごと落とさない・
 * subsidiaryCheckStatusPresentation と同じ方針・原則3）。
 */
export function manualCheckVerdictPresentation(
  verdict: ManualCheckVerdict,
): { label: string; icon: Component; className: string } {
  return MANUAL_CHECK_VERDICTS[verdict] ?? {
    label: String(verdict),
    icon: CircleHelp,
    className: 'bg-slate-100 text-slate-600',
  }
}

/** 突合方式のラベル（パターン設定・フォームの説明表示）。 */
export const MANUAL_COMPARE_MODES: Record<ManualFieldCompareMode, { label: string; hint: string }> = {
  text: { label: '文字列', hint: '文字列として突合（全角/半角・空白の揺れは吸収）' },
  ratio: { label: '組成（割合）', hint: '材質と割合の組で突合。並び順の相違は要確認' },
  careIcon: { label: '洗濯表示（アイコン）', hint: 'アイコンの種類と並び順で突合' },
}

export function manualCompareModeLabel(compare: ManualFieldCompareMode): string {
  return MANUAL_COMPARE_MODES[compare]?.label ?? String(compare)
}

/** 値型のラベル。 */
export const MANUAL_VALUE_TYPES: Record<ManualFieldValueType, { label: string }> = {
  string: { label: '文字列' },
  number: { label: '数値' },
}

export function manualValueTypeLabel(valueType: ManualFieldValueType): string {
  return MANUAL_VALUE_TYPES[valueType]?.label ?? String(valueType)
}

/** タグ画像の枚数上限（バックエンド ManualCheckService.MaxTagImages と同期）。 */
export const MANUAL_TAG_MAX_COUNT = 3

/** 入力項目数の上限（バックエンド ManualCheckService.MaxInputFields と同期）。 */
export const MANUAL_MAX_FIELDS = 40

/** 1項目あたりの入力値・組成成分の上限（バックエンド ManualCheckService.MaxValuesPerField と同期）。 */
export const MANUAL_MAX_VALUES_PER_FIELD = 30
