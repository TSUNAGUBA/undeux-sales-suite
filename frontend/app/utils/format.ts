// 表示用フォーマットユーティリティ（ロケール: 日本）。

/** 整数を3桁区切りで表示する。 */
export function formatNumber(value: number | null | undefined): string {
  return value == null ? '-' : Math.round(value).toLocaleString('ja-JP')
}

/** 金額を円表記で表示する。 */
export function formatCurrency(value: number | null | undefined): string {
  return value == null ? '-' : '¥' + Math.round(value).toLocaleString('ja-JP')
}

/** 比率（0〜1）をパーセント表記で表示する。 */
export function formatRatioAsPercent(
  ratio: number | null | undefined,
  digits = 1,
): string {
  return ratio == null ? '-' : (ratio * 100).toFixed(digits) + '%'
}

/** 既にパーセント値（0〜100）の数値をパーセント表記で表示する。 */
export function formatPercent(
  value: number | null | undefined,
  digits = 1,
): string {
  return value == null ? '-' : value.toFixed(digits) + '%'
}

/** 小数を指定桁で表示する。 */
export function formatDecimal(
  value: number | null | undefined,
  digits = 1,
): string {
  return value == null ? '-' : value.toFixed(digits)
}

/** ISO日付文字列を日本ロケールの日付で表示する。 */
export function formatDate(value: string | null | undefined): string {
  if (!value) {
    return '-'
  }
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime())
    ? '-'
    : parsed.toLocaleDateString('ja-JP')
}

/** ISO日時文字列を日本ロケールの日時で表示する。 */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) {
    return '-'
  }
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime())
    ? '-'
    : parsed.toLocaleString('ja-JP')
}
