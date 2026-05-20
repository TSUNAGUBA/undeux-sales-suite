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

export interface FilterOptions {
  departments: CodeName[]
  customers: CodeName[]
  businessTypes: CodeName[]
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
}

/** クロス集計の基本項目（単品レベル時のみ非null）。 */
export interface CrosstabBasicItems {
  hinban: string
  tanpin: string
  hinmei: string
  shohinKigo: string
  color: string
  size: string
  kisetsu: string
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
