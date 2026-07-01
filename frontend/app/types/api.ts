// バックエンドAPI（C#）のレスポンス型定義。JSON は camelCase。
import type { Component } from 'vue'

export interface AuthUser {
  uid: string
  email: string | null
}

/**
 * アカウント種別。
 * - `supplier`（サプライヤー＝メーカー）: 自社商品の売上・在庫の管理/分析を行う。既存の「売れた結果を見る」画面群。
 * - `buyer`（バイヤー＝小売）: 全サプライヤー横断で仕入予算・OTB を管理し、「未来の仕入意思決定」を行う。
 *
 * SoT は Firebase カスタムクレーム `accountType`（バックエンド発行）。未設定時の既定は `supplier`
 * （既存ユーザー＝メーカー想定）。本プロダクトはモック段階のため、デモ用にローカル切替も許容する
 * （`composables/useAccountType.ts`）。データスコープの物理的分離（メーカーは自社のみ）はマルチベンダ
 * データ／バックエンド強制が前提のため後続。現状はナビゲーション/画面のロール出し分けで表現する。
 */
export type AccountType = 'supplier' | 'buyer'

/** KPIカード1件の表示データ。 */
export interface KpiCardItem {
  label: string
  value: string
  icon: Component
  accentClass: string
  /** 補足行（前週差・注記等）。KpiCard の sub プロップに対応する。 */
  sub?: string
}

export interface CodeName {
  code: string
  name: string | null
}

/** 業態コード・表示名・略称の組（業態専用）。 */
export interface BusinessTypeOption {
  code: string
  name: string | null
  shortName: string | null
}

/**
 * フィルタUIの選択肢一式。
 * 取引先（customer_code）は本アプリでは常に同じ値（メーカー固有コード）のため
 * 選択肢としては提供しない。
 */
export interface FilterOptions {
  departments: CodeName[]
  businessTypes: BusinessTypeOption[]
  seasons: CodeName[]
  weeks: string[]
  /** 棚割1 の実値（NULL/空は除外、昇順）。フィルタ選択肢に用いる。 */
  tanawari1: string[]
}

export interface SalesKpi {
  quantity: number
  amount: number
  grossProfit: number
  grossProfitRate: number
  productCount: number
  currentStock: number
  sellThroughRate: number
  latestWeek: string | null
}

export interface TrendPoint {
  date: string
  quantity: number
  amount: number
  grossProfit: number
}

export interface SummaryResponse {
  kpi: SalesKpi
  weeklyTrend: TrendPoint[]
}

export interface TrendResponse {
  granularity: string
  points: TrendPoint[]
}

export interface BreakdownRow {
  key: string
  label: string
  quantity: number
  amount: number
  grossProfit: number
  sharePercent: number
}

export interface BreakdownResponse {
  dimension: string
  metric: string
  rows: BreakdownRow[]
}

export interface InventoryKpi {
  totalStock: number
  totalOrderQuantity: number
  totalAdvanceQuantity: number
  cumulativeSales: number
  cumulativeDelivery: number
  sellThroughRate: number
  averageStockDays: number
  latestWeek: string | null
}

export interface InventoryBreakdownRow {
  key: string
  label: string
  stock: number
  orderQuantity: number
  advanceQuantity: number
  sellThroughRate: number
}

export interface InventoryResponse {
  kpi: InventoryKpi
  byDepartment: InventoryBreakdownRow[]
}

/** 在庫健全性の状態コード（判定の SoT はバックエンド InventoryHealthRules）。 */
export type InventoryStatus = 'healthy' | 'caution' | 'stagnant' | 'dormant'

/** 推奨アクションコード（表示ラベル・CTA は utils/skuStatus.ts のカタログが射影）。 */
export type RecommendedActionCode =
  | 'reduce-order'
  | 'markdown-candidate'
  | 'review-shelf'
  | 'clearance'
  | 'observe'

/** アクションフィードの重大度。 */
export type InventoryActionSeverity = 'danger' | 'warning' | 'info'

/** アクションフィードの遷移先（在庫マネジメントのページ内タブ）。 */
export type InventoryActionTargetTab = 'dashboard' | 'items' | 'stagnant' | 'dormant'

/**
 * 適用中の在庫健全性閾値。SoT はバックエンド（InventoryHealthRules）で、
 * 定義バナー・経過バケットのラベルは必ず本値から描画する（フロントに閾値リテラルを置かない）。
 */
export interface InventoryThresholds {
  cautionStockDays: number
  stagnantStockDays: number
  stagnantSellThroughCeiling: number
  dormantLookbackWeeks: number
  dormantScanWeeks: number
  stagnantAgingBoundaries: number[]
  dormantAgingBoundaries: number[]
}

/** 状態別の SKU 件数（タブバッジ・フィルタチップ用）。 */
export interface InventoryStatusCounts {
  healthy: number
  caution: number
  stagnant: number
  dormant: number
  total: number
}

/** 在庫アクションサマリーの KPI（比率は在庫点数ベース）。 */
export interface InventoryActionsKpi {
  totalStock: number
  /** 在庫金額（原価ベース。原価未設定 SKU は 0 円加算のため下振れしうる）。 */
  stockValueCost: number
  healthyStockRatio: number
  sellThroughRate: number
  averageStockDays: number
  stagnantDormantStockRatio: number
  totalOrderQuantity: number
  totalAdvanceQuantity: number
  cumulativeDelivery: number
}

/** 「今週のアクション」フィードの1件。 */
export interface InventoryActionItem {
  code: string
  severity: InventoryActionSeverity
  message: string
  targetTab: InventoryActionTargetTab
  count: number | null
}

/** 部門別の在庫健全性1行（4象限バブル・鮮度積上げ帯の素材）。 */
export interface InventoryDepartmentHealthRow {
  key: string
  label: string
  stock: number
  stockValueCost: number
  sellThroughRate: number
  averageStockDays: number
  healthyStock: number
  cautionStock: number
  stagnantStock: number
  dormantStock: number
}

/** GET /api/mart/inventory/actions のレスポンス。 */
export interface InventoryActionsResponse {
  latestWeek: string | null
  previousWeek: string | null
  thresholds: InventoryThresholds
  kpi: InventoryActionsKpi
  /** 前週の KPI（差分計算はフロント射影）。前週スナップショットが無ければ null。 */
  previousKpi: InventoryActionsKpi | null
  statusCounts: InventoryStatusCounts
  actions: InventoryActionItem[]
  byDepartment: InventoryDepartmentHealthRow[]
  /** 品番3桁（product_code）別の健全性（品番ポジショニング用。includeHinban=true 時のみ・在庫上位50）。 */
  byHinban: InventoryDepartmentHealthRow[]
}

/**
 * 在庫アクション明細の1行（SKU 単位・最新週スナップショット基準）。
 * 真のユニークキーは (gyotaiCode, shohinKigou, hinbanCode, tanpinCode) の4組（ProductRow と同じ）。
 */
export interface InventoryItemRow {
  gyotaiCode: string
  shohinKigou: string
  hinbanCode: string
  tanpinCode: string
  hinmei: string
  stock: number
  stockValueCost: number
  stockDays: number
  sellThroughRate: number
  orderQuantity: number
  advanceQuantity: number
  /** 直近で出荷ゼロが続く週数（thresholds.dormantScanWeeks でキャップ）。 */
  zeroSalesWeeks: number
  status: InventoryStatus
  recommendedAction: RecommendedActionCode | null
  masterProductId: string | null
  productName: string | null
  brand: string | null
  primaryImageUrl: string | null
  /** 結合された在庫アクションフラグ（0〜2件。additive 追加フィールド）。 */
  flags: InventoryItemFlag[]
}

/** 在庫アクションフラグの種別（実装 SoT はバックエンド InventoryFlagRules・DB CHECK 制約）。 */
export type InventoryFlagType = 'stop-order' | 'markdown'

/** 在庫アクションフラグの対応状況。 */
export type InventoryFlagStatus = 'candidate' | 'in-progress' | 'done' | 'dismissed'

/** 明細行に結合された在庫アクションフラグ1件（SoT: inventory_action_flag テーブル）。 */
export interface InventoryItemFlag {
  id: number
  flagType: InventoryFlagType
  status: InventoryFlagStatus
  note: string | null
  /** 付与時のアンカー週。 */
  flaggedWeek: string
  /** 付与時の判定状態（現在の行 status と異なる場合「現在は判定対象外」を注記する）。 */
  flaggedStatus: string
  updatedBy: string
  updatedAt: string
}

/** POST /api/inventory-flags/bulk のレスポンス。 */
export interface InventoryFlagBulkResult {
  created: number
  skipped: number
}

/** フラグ種別×対応状況の件数1行。 */
export interface InventoryFlagSummaryRow {
  flagType: string
  status: string
  count: number
}

/** GET /api/inventory-flags/summary のレスポンス。 */
export interface InventoryFlagSummary {
  rows: InventoryFlagSummaryRow[]
  /** 最新週スナップショットに存在しない SKU のフラグ件数（完売等＝対応完了の確認候補）。 */
  orphanCount: number
  latestWeek: string | null
}

/** GET /api/mart/inventory/items のレスポンス。 */
export interface InventoryItemsPage {
  items: InventoryItemRow[]
  /** statuses 絞込後の総件数（ページングの分母）。 */
  totalCount: number
  page: number
  pageSize: number
  latestWeek: string | null
  /** statuses 絞込前（フィルタ・検索適用後）の状態別件数。 */
  statusCounts: InventoryStatusCounts
  /** 滞留の経過バケット件数（thresholds.stagnantAgingBoundaries の3区分に対応）。 */
  stagnantAgingCounts: number[]
  /** 不動の経過バケット件数（thresholds.dormantAgingBoundaries の3区分に対応）。 */
  dormantAgingCounts: number[]
  thresholds: InventoryThresholds
}

/**
 * 商品別分析の1行。
 * 真のユニークキーは (gyotaiCode, shohinKigou, hinbanCode, tanpinCode) の4組。
 * v-for :key は必ずこの4組で組み立てること（hinban-tanpin だけでは衝突する）。
 */
export interface ProductRow {
  gyotaiCode: string
  shohinKigou: string
  hinbanCode: string
  tanpinCode: string
  hinmei: string
  kisetsu: string
  salesQuantity: number
  salesAmount: number
  grossProfit: number
  stock: number
  sellThroughRate: number
  averageStockDays: number
  /** 商品マスタが結合できた場合の商品ID（無ければ null）。 */
  masterProductId: string | null
  /** 商品マスタに登録された商品名（マスタ無し時は null。表示時は hinmei を fallback）。 */
  productName: string | null
  /** 商品マスタのブランド（任意項目）。 */
  brand: string | null
  /** 当該 tanpin の代表画像URL（image_index 最小のSKU画像）。 */
  primaryImageUrl: string | null
}

export interface ProductPage {
  items: ProductRow[]
  totalCount: number
  page: number
  pageSize: number
}

export interface ImportBatchInfo {
  id: number
  sourceType: string
  fileName: string
  status: string
  rowCount: number
  weekCount: number
  minImportDate: string | null
  maxImportDate: string | null
  errorMessage: string | null
  startedAt: string
  completedAt: string | null
}

export interface ImportResult {
  batchId: number
  rowCount: number
  weekCount: number
  minImportDate: string | null
  maxImportDate: string | null
}

export interface ApiError {
  errorCode: string
  summary: string
  remedy: string
  detail?: string
  details?: string[]
}

/**
 * 売上分析クエリの共通フィルタ状態（年度 = 1月〜12月のカレンダー年）。
 * 取引先（customer_code）は本アプリでは常に同じ値（メーカー固有コード）のためフィルタには含めない。
 */
export interface SalesFilterState {
  /** 西暦年。null=全期間。 */
  year: number | null
  departments: string[]
  businessTypes: string[]
  seasons: string[]
  /** 品番コード。ドリルダウンで追加される（UIには直接の入力枠は無い）。 */
  hinbans: string[]
  /** 棚割1（複数選択）。 */
  tanawari1: string[]
  /**
   * 平均在庫日数（在日）のバケット（複数選択＝OR）。値は {@link StockDaysBucket}
   * （'le30' / 'd31to60' / 'ge61'）。UI は STOCK_DAYS_BUCKETS カタログで制約する。
   */
  stockDaysBuckets: string[]
}

/** 平均在庫日数バケットのキー。 */
export type StockDaysBucket = 'le30' | 'd31to60' | 'ge61'

/** 気温分析のエリア種別（括弧内は参照観測地点）。 */
export type TemperatureArea = 'standard' | 'cold' | 'warm'

// ============================================================
// クロス集計（行×列マトリクス）
// ============================================================

/**
 * クロス集計のディメンションキー。
 * - 時間軸: `time:year`, `time:quarter`, `time:month`
 * - カテゴリ軸: `category:department`, `category:businessType`, `category:season`,
 *   `category:hinban`, `category:product`, `category:color`, `category:size`,
 *   `category:chohyoKubun`, `category:tanawari1`, `category:tanawari2`, `category:shohinKigo`
 */
export type CrosstabDimensionKey =
  | 'time:year'
  | 'time:quarter'
  | 'time:month'
  | 'category:department'
  | 'category:businessType'
  | 'category:season'
  | 'category:hinban'
  | 'category:product'
  | 'category:color'
  | 'category:size'
  | 'category:chohyoKubun'
  | 'category:tanawari1'
  | 'category:tanawari2'
  | 'category:shohinKigo'

/** クロス集計のメトリクスキー。気温系（temp*）は時間軸＋エリア種別指定時のみ利用可能。 */
export type CrosstabMetricKey =
  | 'amount'
  | 'quantity'
  | 'grossProfit'
  | 'sharePercent'
  | 'stockDays'
  | 'sellThroughRate'
  | 'stock'
  | 'tempAvg'
  | 'tempMax'
  | 'tempMin'

/** ディメンションの表示情報。バックエンドが返す行・列メタ情報。 */
export interface CrosstabDimensionInfo {
  key: CrosstabDimensionKey
  /** 'time' または 'category'。 */
  category: 'time' | 'category'
  label: string
  /**
   * 時間軸ディメンション（年・四半期・月）かどうか。
   * 在庫系メトリクスの利用可否判定は本フィールドを SoT とし、
   * フロント側の文字列前方一致判定（`key.startsWith('time:')`）は使わない。
   */
  isTimeAxis: boolean
}

/** メトリクスのカタログ情報（フロント側で定義）。 */
export interface CrosstabMetricInfo {
  key: CrosstabMetricKey
  label: string
  /** セル値のフォーマット種別。 */
  format: 'currency' | 'number' | 'decimal' | 'percent' | 'temperature'
}

/** 1セルの値（在庫系は時間軸絡みの場合 null。気温系は時間軸＋エリア指定時のみ設定）。 */
export interface CrosstabCellValues {
  amount: number | null
  quantity: number | null
  grossProfit: number | null
  sharePercent: number | null
  stockDays: number | null
  sellThroughRate: number | null
  stock: number | null
  tempAvg: number | null
  tempMax: number | null
  tempMin: number | null
}

/** 1セル。 */
export interface CrosstabCell {
  values: CrosstabCellValues
}

/** クロス集計マトリクスのレスポンス。 */
export interface CrosstabMatrixResponse {
  rowDimension: CrosstabDimensionInfo
  columnDimension: CrosstabDimensionInfo
  /** 行ラベルの順序付きリスト（最大100件）。 */
  rowLabels: string[]
  /** 列ラベルの順序付きリスト（最大100件）。 */
  columnLabels: string[]
  /** [行ラベル][列ラベル] = CrosstabCell。空セルは省略。 */
  cells: Record<string, Record<string, CrosstabCell>>
  /** 行ごとの合計（最終列）。表示行の和と一致する。 */
  rowTotals: Record<string, CrosstabCell>
  /** 列ごとの合計（最終行）。表示列の和と一致する。 */
  columnTotals: Record<string, CrosstabCell>
  /** 総計（右下セル）。切り詰め後でも rowTotals/columnTotals/cells の和と完全に整合する。 */
  grandTotal: CrosstabCell
  /** 在庫スナップショット基準週（時間軸絡みでない場合）。 */
  latestWeek: string | null
  /** 利用可能なメトリクスキー一覧。時間軸絡みなら在庫系は除外される。 */
  availableMetrics: CrosstabMetricKey[]
  /** 行ラベルが 100 件で切り詰められたか。 */
  rowTruncated: boolean
  /** 列ラベルが 100 件で切り詰められたか。 */
  columnTruncated: boolean
}

/**
 * 複数メトリクスのセル表示モード。
 * - `stacked`: セル内で縦並び
 * - `inlineColumns`: 列を増やして横並び（列×メトリクスのサブ列）
 * - `metricRows`: 行を増やして横並び（行×メトリクスのサブ行）
 */
export type MetricDisplayMode = 'stacked' | 'inlineColumns' | 'metricRows'

// ============================================================
// ランキング分析（単軸ランキング + 期間比較 + ABC/複合スコア）
// ============================================================

/**
 * ランキングの集計軸キー（バックエンドの BreakdownDimension に対応）。
 * クエリには小文字始まりのこのキーをそのまま渡す（RequestParsing.Dimension が大小無視で解釈）。
 */
export type RankingDimensionKey =
  | 'department'
  | 'businessType'
  | 'season'
  | 'product'
  | 'color'
  | 'size'
  | 'hinban'
  | 'chohyoKubun'
  | 'tanawari1'
  | 'tanawari2'
  | 'shohinKigo'

/** ランキングで並び替え・複合スコアに使える指標キー。 */
export type RankingMetricKey =
  | 'amount'
  | 'quantity'
  | 'grossProfit'
  | 'grossProfitRate'
  | 'sellThroughRate'
  | 'stockDays'
  | 'stock'

/**
 * 1行・1期間ぶんの集計値。在庫系（stock / sellThroughRate / stockDays）は
 * その期間の最新週スナップショットが無い場合 null。粗利率・構成比はこの値から導出する。
 */
export interface RankingMetricValues {
  quantity: number
  amount: number
  grossProfit: number
  stock: number | null
  sellThroughRate: number | null
  stockDays: number | null
  /** 残在庫金額（原価ベース＝在庫数 × 原価の合計）。最新週スナップショットが無い場合 null。 */
  stockValueCost: number | null
}

/** ランキング1行（主期間 current・比較期間 comparison）。 */
export interface RankingRow {
  key: string
  label: string
  /** 主期間の集計値。主期間に存在しない（比較期間のみ＝圏外転落）の場合 null。 */
  current: RankingMetricValues | null
  /** 比較期間の集計値。比較未指定／比較期間に存在しない（＝新規）の場合 null。 */
  comparison: RankingMetricValues | null
}

/**
 * ランキング分析 API のレスポンス。
 * 順位・複合スコア・累積構成比・ABC ランクはフロント側で本素材から算出する（表示射影）。
 */
export interface RankingResponse {
  /** 集計軸（BreakdownDimension の名前。例 "Department"）。 */
  dimension: string
  /** 集計行（主期間/比較期間の売上金額の大きい方の降順、最大件数で切り詰め）。 */
  rows: RankingRow[]
  /** 主期間の在庫スナップショット基準週（データなしは null）。 */
  latestWeek: string | null
  /** 比較期間の在庫スナップショット基準週（比較未指定／データなしは null）。 */
  comparisonLatestWeek: string | null
  /** 並び替え・複合スコアに利用可能な指標キー一覧（在庫系は最新週がある場合のみ）。 */
  availableMetrics: RankingMetricKey[]
  /** 対象キー数が上限を超え切り詰められた場合 true。 */
  truncated: boolean
}

// ============================================================
// 商品マスタ（m_product / m_product_sku）
// ============================================================

/** 商品マスタ一覧（カード型UI）の1件。 */
export interface MasterProductSummary {
  productId: string
  businessCategoryCd: string
  businessCategorySign: string
  divisionCd: number
  divisionName: string
  productName: string
  brand: string | null
  productSign: string
  manager: string | null
  productTypeCrd: string
  skuCount: number
  colorCount: number
  sizeCount: number
  minSalesPrice: number | null
  maxSalesPrice: number | null
  primaryImageUrl: string | null
  /** 全期間の売上数量（sales_weekly 自然キー結合の実績。マスタのみ存在は 0）。 */
  salesQuantity: number
  /** 平均在庫日数（在日 zainiti の平均、最新取込週基準）。 */
  averageStockDays: number
  /** 季節区分（最頻値。実績が無ければ空文字）。 */
  kisetsu: string
  /** 店頭在庫数（zaikosu、最新取込週基準）。 */
  storeStock: number
}

/** 商品マスタの SKU 画像（1枚）。 */
export interface MasterProductSkuImage {
  imageId: string
  imageIndex: number
  imageFileName: string | null
  imageUrl: string
}

/** 商品マスタの SKU 1件（同一 SKU の画像はリストで保持）。 */
export interface MasterProductSku {
  skuItemId: string
  unitCd: string
  colorName: string
  sizeName: string
  salesPrice: number
  costPrice: number
  images: MasterProductSkuImage[]
}

/** 商品マスタ詳細（親 + SKU 一覧）。 */
export interface MasterProductDetail {
  summary: MasterProductSummary
  skus: MasterProductSku[]
}

/** 商品マスタ一覧のページ。 */
export interface MasterProductPage {
  items: MasterProductSummary[]
  totalCount: number
  page: number
  pageSize: number
}

/** 商品マスタ専用フィルタの選択肢一式。 */
export interface MasterFilterOptions {
  businessTypes: BusinessTypeOption[]
  divisions: CodeName[]
  brands: string[]
  managers: string[]
}

/** 商品マスタ画面の検索フィルタ状態。 */
export interface ProductMasterFilterState {
  search: string
  businessCategoryCds: string[]
  divisionCds: number[]
  brands: string[]
  managers: string[]
}

// ============================================================
// 商品軸の分析
// ============================================================

/** 商品単位の期間内 KPI。 */
export interface ProductAnalyticsKpi {
  quantity: number
  amount: number
  grossProfit: number
  grossProfitRate: number
  currentStock: number
  sellThroughRate: number
  averageStockDays: number
  latestWeek: string | null
}

/** SKU別の売上集計（色・サイズ別）。 */
export interface ProductSkuPerformance {
  unitCd: string
  colorName: string
  sizeName: string
  salesPrice: number
  primaryImageUrl: string | null
  quantity: number
  amount: number
  grossProfit: number
  stock: number
  sharePercent: number
}

/** 業態別の売上集計（同一の商品記号・品番で別業態に同名商品があるケース）。 */
export interface ProductBusinessTypePerformance {
  businessCategoryCd: string
  displayName: string | null
  shortName: string | null
  quantity: number
  amount: number
  grossProfit: number
  sharePercent: number
}

/** 商品分析のレスポンス（指定商品の包括的な売上分析）。 */
export interface ProductAnalyticsResponse {
  product: MasterProductSummary
  kpi: ProductAnalyticsKpi
  weeklyTrend: TrendPoint[]
  bySku: ProductSkuPerformance[]
  byBusinessType: ProductBusinessTypePerformance[]
}

// ============================================================
// 分析（散布図・単回帰 / 重回帰シミュレーション）
// バックエンドは集計素材のみ返し、回帰・予測・象限分類はフロント（utils/regression）で算出する。
// ============================================================

/** 週次系列の1点（売上フロー指標 + その週・エリアの標準気温 + 当週在庫スナップショット）。 */
export interface WeeklySeriesPoint {
  /** 取込日（月曜）。表す週は前週 月〜日。 */
  week: string
  quantity: number
  amount: number
  grossProfit: number
  tempAvg: number
  tempMax: number
  tempMin: number
  /** 当週の店頭在庫数（在庫スナップショットの時点値）。 */
  stock: number
  /** 当週の在日（平均）。 */
  stockDays: number
  /** 当週の消化率（0..1）。formatRatioAsPercent で描画する。 */
  sellThroughRate: number
}

/** 週次系列レスポンス（散布図 気温×売上 と重回帰シミュレーションの素材）。 */
export interface WeeklySeriesResponse {
  /** エリア種別。 */
  area: TemperatureArea
  /** 参照観測地点名（"東京"/"札幌"/"那覇"）。 */
  areaCity: string
  points: WeeklySeriesPoint[]
}

/** 消化率×値引き率 散布図の1点（型番＝業態×記号×品番 単位）。 */
export interface MarkdownScatterPoint {
  key: string
  label: string
  businessType: string
  /** 消化率（%）。 */
  sellThroughRate: number
  /** 値引き率（%）。 */
  markdownRate: number
  /** 期間内売上数量（バブルサイズ用）。 */
  quantity: number
  /** 季節区分（未設定は null）。 */
  season: string | null
  /** 店頭在庫数（最新週スナップショット）。 */
  stock: number
  /** 平均在庫日数＝在日（最新週スナップショットの平均）。 */
  stockDays: number
}

/** 消化率×値引き率 散布図のレスポンス。 */
export interface MarkdownScatterResponse {
  latestWeek: string | null
  points: MarkdownScatterPoint[]
}

// ============================================================
// 分析 mart（スタースキーマ）
// 既存 sales_weekly 直参照とは別系統。docs/star-schema-design.md。
// ============================================================

/** mart 全社サマリーのKPI（週次フロー指標＋最新週の在庫スナップショット）。 */
export interface MartKpi {
  quantity: number
  amount: number
  grossProfit: number
  grossProfitRate: number
  productCount: number
  skuCount: number
  /** 最新週の在庫数（fact_inventory_snapshot 由来）。 */
  currentStock: number
  /** 最新週の消化率（%）。 */
  sellThroughRate: number
  latestWeek: string | null
}

/** mart 全社サマリーのレスポンス（KPI＋週次トレンド）。 */
export interface MartSummaryResponse {
  kpi: MartKpi
  weeklyTrend: TrendPoint[]
}

/** mart 集計軸別分析のレスポンス（BreakdownRow を sales 系と共有）。 */
export interface MartBreakdownResponse {
  dimension: string
  rows: BreakdownRow[]
}

/** 商品導入管理（mart）の1行（商品＝業態×記号×品番 単位）。 */
export interface MartIntroductionRow {
  channelCode: string
  channelName: string | null
  departmentCode: string | null
  departmentName: string | null
  productSign: string
  /** 品番CD（服種）。 */
  productCode: string
  productName: string | null
  brand: string | null
  manager: string | null
  /** 導入日（ISO yyyy-MM-dd。未設定は null）。 */
  donyuDate: string | null
  skuCount: number
  /** 全期間の売上数量。 */
  salesQuantity: number
  primaryImageUrl: string | null
}

/** 商品導入管理一覧のページ。 */
export interface MartIntroductionPage {
  items: MartIntroductionRow[]
  totalCount: number
  page: number
  pageSize: number
}

/** 商品導入管理のフィルタ選択肢（業態・部門は FilterOptions を再利用する）。 */
export interface MartIntroductionOptions {
  brands: string[]
  managers: string[]
  /** 服種＝品番CD の実値。 */
  hinbans: string[]
}

// ============================================================
// 週間モニタリング — 日次分析（特定品番）
// ============================================================

/** 日次系列の1点（売上＋東京の平均気温）。気温は dim_climate 実測優先・未カバーは平年値。 */
export interface DailySeriesPoint {
  /** 実日付（yyyy-MM-dd）。 */
  date: string
  quantity: number
  amount: number
  grossProfit: number
  /** 平均気温（東京）。 */
  tempAvg: number
}

/** GET /api/mart/daily-series のレスポンス（気温参照都市は常に東京）。 */
export interface DailySeriesResponse {
  areaCity: string
  points: DailySeriesPoint[]
}

/**
 * 帳票区分の前週比較による SKU 遷移区分。
 * new=当週新規 / unchanged=変化なし / sale-to-info=売→情 / info-to-sale=情→売 / dropped=情売→無。
 */
export type ChohyoTransitionKind = 'new' | 'unchanged' | 'sale-to-info' | 'info-to-sale' | 'dropped'

/** 帳票区分遷移の1行（SKU＝業態×記号×品番3桁×単品4桁）。 */
export interface ChohyoTransitionRow {
  gyotaiCode: string
  shohinKigou: string
  hinbanCode: string
  tanpinCode: string
  hinmei: string
  currentKubun: string
  previousKubun: string
  transition: ChohyoTransitionKind
  quantity: number
  stock: number
}

/** GET /api/mart/chohyo-transition のレスポンス。 */
export interface ChohyoTransitionResponse {
  currentWeek: string | null
  previousWeek: string | null
  rows: ChohyoTransitionRow[]
  /** 行数が上限（2000）で切り詰められたか（品番未指定などで多数該当時）。 */
  truncated: boolean
}

// ============================================================
// 商品詳細分析（定番／トレンド）— SKU × 週マトリクス
// ============================================================

/** 商品詳細分析の週次1点（SKU × 週）。 */
export interface ItemDetailWeekPoint {
  /** 取込週（月曜、yyyy-MM-dd）。 */
  week: string
  /** 当週の売数。 */
  quantity: number
  /** 当週の店頭在庫数。 */
  stock: number
  /** 当週の在日（平均）。 */
  stockDays: number
  /** 当週の売価（baika 最大。売価変更＝値下げ検出に用いる。売上のない週は 0）。 */
  salePrice: number
}

/** 商品詳細分析の1行（SKU＝業態×記号×品番3桁×単品4桁）。 */
export interface ItemDetailRow {
  gyotaiCode: string
  shohinKigou: string
  hinbanCode: string
  tanpinCode: string
  hinmei: string
  colorName: string
  sizeName: string
  /** 上代（定価）。未登録は 0。 */
  listPrice: number
  kisetsu: string
  /** 導入日（yyyy-MM-dd。未設定は null）。 */
  donyuDate: string | null
  departmentCode: string | null
  departmentName: string | null
  brand: string | null
  manager: string | null
  primaryImageUrl: string | null
  /** 期間内の売数合計。 */
  periodQuantity: number
  /** SKU の最新在庫週の累計売上数（消化率算出用）。 */
  cumulativeSales: number
  /** SKU の最新在庫週の累計納品数（消化率算出用）。 */
  cumulativeDelivery: number
  /** 週次の系列（取込週昇順・データのある週のみ）。 */
  points: ItemDetailWeekPoint[]
}

/** 商品詳細分析のレスポンス（SKU × 週マトリクスの素材）。 */
export interface ItemDetailResponse {
  /** 列展開に使う週（取込週昇順・全 SKU の和集合）。 */
  weeks: string[]
  rows: ItemDetailRow[]
  latestWeek: string | null
  truncated: boolean
}

/** mart（スタースキーマ）の構築状態。フロントの鮮度表示・再構築UIに使う。 */
export interface MartStatus {
  built: boolean
  /** 再構築の状態: 'idle' | 'running' | 'completed' | 'failed'。 */
  status: string
  /** 失敗時のエラーメッセージ（それ以外は null）。 */
  error: string | null
  /** 直近の再構築開始時刻。 */
  startedAt: string | null
  rebuiltAt: string | null
  sourceRows: number
  factRows: number
  earliestWeek: string | null
  latestWeek: string | null
}
