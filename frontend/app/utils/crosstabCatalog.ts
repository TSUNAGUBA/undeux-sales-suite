import type {
  CrosstabDimensionInfo,
  CrosstabDimensionKey,
  CrosstabMetricInfo,
  CrosstabMetricKey,
} from '~/types/api'

/**
 * クロス集計の集計軸（ディメンション）・メトリクスの共有カタログ（SoT）。
 *
 * これまで各ページ（/mart/crosstab・商品詳細の埋め込み等）でローカルに重複定義していた
 * 軸・メトリクスの一覧を一箇所に集約する（原則3: 既存パターンの再利用 / 重複排除）。
 * クロス集計を専用メニューへ集約するにあたり、参照元を本ファイルへ統一する。
 *
 * 注記（mart が集計軸として保持しない軸）:
 * mart は帳票区分（chohyoKubun）・棚割1/2（tanawari1/tanawari2）を集計軸としては保持しない
 * ため候補から除外する（棚割1のフィルタは対応済み）。バックエンドはこれらの軸に HTTP 400 を返す。
 */

/**
 * mart のクロス集計で選択できる集計軸の一覧（商品系は「商品記号 → 単品 → 品番3桁」の並び）。
 * 表示専用コンポーネント（CrossTabConditionPanel 等）の prop 型に合わせ配列型は可変で公開するが、
 * 実体は参照専用（消費側は絞り込みに filter で新配列を作る）であり書き換えない。
 */
export const CROSSTAB_DIMENSIONS: CrosstabDimensionInfo[] = [
  { key: 'time:year', category: 'time', label: '年', isTimeAxis: true },
  { key: 'time:quarter', category: 'time', label: '四半期', isTimeAxis: true },
  { key: 'time:month', category: 'time', label: '月', isTimeAxis: true },
  { key: 'category:department', category: 'category', label: '部門', isTimeAxis: false },
  { key: 'category:businessType', category: 'category', label: '業態', isTimeAxis: false },
  { key: 'category:season', category: 'category', label: '季節区分', isTimeAxis: false },
  { key: 'category:shohinKigo', category: 'category', label: '商品記号', isTimeAxis: false },
  { key: 'category:product', category: 'category', label: '単品（品番-単品）', isTimeAxis: false },
  { key: 'category:hinban', category: 'category', label: '品番3桁', isTimeAxis: false },
  { key: 'category:color', category: 'category', label: 'カラー', isTimeAxis: false },
  { key: 'category:size', category: 'category', label: 'サイズ', isTimeAxis: false },
]

/** 集計軸キーの集合（ルートクエリ検証用）。 */
export const CROSSTAB_DIMENSION_KEYS: ReadonlySet<CrosstabDimensionKey> = new Set(
  CROSSTAB_DIMENSIONS.map((d) => d.key),
)

/** キーから集計軸情報を引く（未定義なら undefined）。 */
export function crosstabDimension(key: CrosstabDimensionKey): CrosstabDimensionInfo | undefined {
  return CROSSTAB_DIMENSIONS.find((d) => d.key === key)
}

/**
 * mart のクロス集計で表示できるメトリクスの一覧。
 * stock は店頭在庫（sales_weekly.zaikosu 由来の時点値）を表すため「店頭在庫」表記とする。
 */
export const CROSSTAB_METRICS: CrosstabMetricInfo[] = [
  { key: 'amount', label: '売上金額', format: 'currency' },
  { key: 'quantity', label: '売上数量', format: 'number' },
  { key: 'grossProfit', label: '粗利', format: 'currency' },
  { key: 'sharePercent', label: '構成比率', format: 'percent' },
  { key: 'stockDays', label: '在日（平均）', format: 'decimal' },
  { key: 'sellThroughRate', label: '消化率', format: 'percent' },
  { key: 'stock', label: '店頭在庫', format: 'number' },
  { key: 'tempAvg', label: '週平均気温', format: 'temperature' },
  { key: 'tempMax', label: '週最高気温', format: 'temperature' },
  { key: 'tempMin', label: '週最低気温', format: 'temperature' },
]

/** 気温系メトリクス（時間軸＋エリア種別指定時のみ利用可能。在庫系とは排他）。 */
export const CROSSTAB_TEMP_METRICS: readonly CrosstabMetricKey[] = ['tempAvg', 'tempMax', 'tempMin']

/** 在庫系メトリクス（時間軸との組合せでは表示不可）。 */
export const CROSSTAB_STOCK_METRICS: readonly CrosstabMetricKey[] = ['stockDays', 'sellThroughRate', 'stock']

/** クロス集計ページの既定メトリクス。 */
export const CROSSTAB_DEFAULT_METRICS: readonly CrosstabMetricKey[] = ['amount', 'quantity', 'grossProfit']

/** 旧 `?dimension=hinban` 形式を新 API 仕様の `category:xxx` に変換するマップ（mart 対応軸のみ）。 */
export const CROSSTAB_LEGACY_DIMENSION_MAP: Readonly<Record<string, CrosstabDimensionKey>> = {
  department: 'category:department',
  businessType: 'category:businessType',
  season: 'category:season',
  hinban: 'category:hinban',
  product: 'category:product',
  color: 'category:color',
  size: 'category:size',
  shohinKigo: 'category:shohinKigo',
}

/** ルートクエリから有効なディメンションキーを抽出。無効（mart 非対応軸含む）なら undefined。 */
export function pickCrosstabDimensionKey(raw: unknown): CrosstabDimensionKey | undefined {
  if (typeof raw !== 'string') return undefined
  return CROSSTAB_DIMENSION_KEYS.has(raw as CrosstabDimensionKey)
    ? (raw as CrosstabDimensionKey)
    : undefined
}
