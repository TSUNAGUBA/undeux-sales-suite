// バックエンドAPI（C#）のレスポンス型定義。JSON は camelCase。
import type { Component } from 'vue'

export interface AuthUser {
  uid: string
  email: string | null
}

/** KPIカード1件の表示データ。 */
export interface KpiCardItem {
  label: string
  value: string
  icon: Component
  accentClass: string
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

export interface FilterOptions {
  departments: CodeName[]
  customers: CodeName[]
  businessTypes: BusinessTypeOption[]
  seasons: CodeName[]
  weeks: string[]
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

export interface ProductRow {
  hinbanCode: string
  tanpinCode: string
  hinmei: string
  shohinKigou: string
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

/** 売上分析クエリの共通フィルタ状態（年度 = 1月〜12月のカレンダー年）。 */
export interface SalesFilterState {
  /** 西暦年。null=全期間。 */
  year: number | null
  departments: string[]
  customers: string[]
  businessTypes: string[]
  seasons: string[]
  /** 品番コード。ドリルダウンで追加される（UIには直接の入力枠は無い）。 */
  hinbans: string[]
}

/** クロス集計の基本項目（単品レベル時のみ非null）。商品マスタが解決できれば商品名・画像も含む。 */
export interface CrosstabBasicItems {
  hinban: string
  tanpin: string
  hinmei: string
  shohinKigo: string
  color: string
  size: string
  kisetsu: string
  masterProductId: string | null
  productName: string | null
  brand: string | null
  primaryImageUrl: string | null
}

/** クロス集計の1行。 */
export interface CrosstabRow {
  key: string
  label: string
  basicItems: CrosstabBasicItems | null
  quantity: number
  amount: number
  grossProfit: number
  sharePercent: number
  stock: number
  stockDays: number
  sellThroughRate: number
}

/** クロス集計のレスポンス（売上金額の降順）。 */
export interface CrosstabResponse {
  dimension: string
  rows: CrosstabRow[]
  latestWeek: string | null
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
  storeCount: number
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

/** 取引先（店舗）別の売上集計。 */
export interface ProductCustomerPerformance {
  customerCode: string
  customerName: string | null
  quantity: number
  amount: number
  grossProfit: number
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
  byCustomer: ProductCustomerPerformance[]
  byBusinessType: ProductBusinessTypePerformance[]
}
