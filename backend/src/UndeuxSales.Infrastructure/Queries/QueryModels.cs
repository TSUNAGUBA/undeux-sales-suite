namespace UndeuxSales.Infrastructure.Queries;

/// <summary>コードと表示名の組。</summary>
public sealed record CodeName(string Code, string? Name);

/// <summary>業態コード・表示名・略称の組（業態専用）。</summary>
public sealed record BusinessTypeOption(string Code, string? Name, string? ShortName);

/// <summary>
/// フィルタUIの選択肢一式。
/// 取引先（customer_code）は本アプリのユーザー（メーカー）に対して小売側が振り出した
/// 固有コードで、本アプリ内では常に同じ値となるため選択肢として提供しない。
/// </summary>
public sealed record FilterOptions(
    IReadOnlyList<CodeName> Departments,
    IReadOnlyList<BusinessTypeOption> BusinessTypes,
    IReadOnlyList<CodeName> Seasons,
    IReadOnlyList<DateOnly> Weeks);

/// <summary>全社サマリーの主要KPI。</summary>
public sealed record SalesKpi(
    long Quantity,
    long Amount,
    long GrossProfit,
    double GrossProfitRate,
    int ProductCount,
    long CurrentStock,
    double SellThroughRate,
    DateOnly? LatestWeek);

/// <summary>時系列トレンドの1点。</summary>
public sealed record TrendPoint(DateOnly Date, long Quantity, long Amount, long GrossProfit);

/// <summary>全社サマリーのレスポンス。</summary>
public sealed record SummaryResponse(SalesKpi Kpi, IReadOnlyList<TrendPoint> WeeklyTrend);

/// <summary>売上トレンドのレスポンス。</summary>
public sealed record TrendResponse(string Granularity, IReadOnlyList<TrendPoint> Points);

/// <summary>集計軸別の1行。</summary>
public sealed record BreakdownRow(
    string Key,
    string Label,
    long Quantity,
    long Amount,
    long GrossProfit,
    double SharePercent);

/// <summary>集計軸別分析のレスポンス。</summary>
public sealed record BreakdownResponse(
    string Dimension,
    string Metric,
    IReadOnlyList<BreakdownRow> Rows);

/// <summary>在庫・発注の主要KPI（最新週スナップショット基準）。</summary>
public sealed record InventoryKpi(
    long TotalStock,
    decimal TotalOrderQuantity,
    long TotalAdvanceQuantity,
    long CumulativeSales,
    long CumulativeDelivery,
    double SellThroughRate,
    double AverageStockDays,
    DateOnly? LatestWeek);

/// <summary>在庫・発注の部門別1行。</summary>
public sealed record InventoryBreakdownRow(
    string Key,
    string Label,
    long Stock,
    decimal OrderQuantity,
    long AdvanceQuantity,
    double SellThroughRate);

/// <summary>在庫・発注分析のレスポンス。</summary>
public sealed record InventoryResponse(
    InventoryKpi Kpi,
    IReadOnlyList<InventoryBreakdownRow> ByDepartment);

/// <summary>商品別分析の1行。商品マスタが結合できた行のみ MasterProductId 等が設定される。</summary>
/// <remarks>
/// 同一の (hinban_code, tanpin_code) が複数業態 (gyotai_code) で売られているとき行が分裂し、
/// (gyotai_code, shohin_kigou, hinban_code, tanpin_code) が真の行キーになる。フロント側の
/// v-for :key にもこの 4 つを使用すること（hinban-tanpin だけでは衝突する）。
/// </remarks>
public sealed record ProductRow(
    string GyotaiCode,
    string ShohinKigou,
    string HinbanCode,
    string TanpinCode,
    string Hinmei,
    string Kisetsu,
    long SalesQuantity,
    long SalesAmount,
    long GrossProfit,
    long Stock,
    double SellThroughRate,
    double AverageStockDays,
    Guid? MasterProductId,
    string? ProductName,
    string? Brand,
    string? PrimaryImageUrl);

/// <summary>商品別分析のページ。</summary>
public sealed record ProductPage(
    IReadOnlyList<ProductRow> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>クロス集計（行×列マトリクス）で使用可能なディメンション。</summary>
/// <remarks>
/// プレフィックスで時間軸（time）かカテゴリ軸（category）を区別する。
/// 時間軸は <c>sw.import_date</c> から導出し、カテゴリ軸は <see cref="BreakdownDimension"/> と
/// 同等の SQL 式（商品=複数列連結など）を使う。
/// </remarks>
public enum CrosstabDimension
{
    // 時間軸
    /// <summary>年（YYYY）。</summary>
    TimeYear,
    /// <summary>四半期（YYYY-Q1 等）。</summary>
    TimeQuarter,
    /// <summary>月（YYYY-MM）。</summary>
    TimeMonth,
    // カテゴリ軸
    /// <summary>部門。</summary>
    CategoryDepartment,
    /// <summary>業態。</summary>
    CategoryBusinessType,
    /// <summary>季節区分。</summary>
    CategorySeason,
    /// <summary>品番3桁（hinban_code 単独）。</summary>
    CategoryHinban,
    /// <summary>単品（品番-単品）。</summary>
    CategoryProduct,
    /// <summary>カラー。</summary>
    CategoryColor,
    /// <summary>サイズ。</summary>
    CategorySize,
    /// <summary>帳票区分名。</summary>
    CategoryChohyoKubun,
    /// <summary>棚割1。</summary>
    CategoryTanawari1,
    /// <summary>棚割2。</summary>
    CategoryTanawari2,
    /// <summary>商品記号。</summary>
    CategoryShohinKigo,
}

/// <summary>クロス集計のディメンション情報（行・列のメタ情報）。</summary>
/// <param name="Key">フロント側の文字列キー（例 "time:year", "category:department"）。</param>
/// <param name="Category">"time" または "category"。</param>
/// <param name="Label">表示ラベル。</param>
/// <param name="IsTimeAxis">
/// 時間軸ディメンション（年・四半期・月）かどうか。フロント側で在庫系メトリクスの可否判定に使う。
/// バックエンドの <c>TimeDimensions</c> 判定と一致しており、フロント側の文字列前方一致判定の
/// 重複実装を避けるため、API レスポンスから渡す（SoT 統一）。
/// </param>
public sealed record CrosstabDimensionInfo(string Key, string Category, string Label, bool IsTimeAxis);

/// <summary>
/// クロス集計の1セルの値。7メトリクスを保持する。在庫系（stockDays / stock / sellThroughRate）は
/// 時間軸を含む組合せでは <c>null</c> になる（最新週スナップショットに基づくため）。
/// </summary>
public sealed record CrosstabCellValues(
    long? Amount,
    long? Quantity,
    long? GrossProfit,
    double? SharePercent,
    double? StockDays,
    double? SellThroughRate,
    long? Stock);

/// <summary>クロス集計の1セル。</summary>
public sealed record CrosstabCell(CrosstabCellValues Values);

/// <summary>クロス集計（マトリクス）のレスポンス。</summary>
/// <param name="RowDimension">行ディメンション情報。</param>
/// <param name="ColumnDimension">列ディメンション情報。</param>
/// <param name="RowLabels">行ラベルの順序付きリスト（最大 100 件で切り詰め）。</param>
/// <param name="ColumnLabels">列ラベルの順序付きリスト（最大 100 件で切り詰め）。</param>
/// <param name="Cells">[行ラベル][列ラベル] = CrosstabCell。空セルは省略。</param>
/// <param name="RowTotals">行ごとの合計（最終列に表示）。表示行の和と一致する。</param>
/// <param name="ColumnTotals">列ごとの合計（最終行に表示）。表示列の和と一致する。</param>
/// <param name="GrandTotal">
/// 全体合計（右下セル）。切り詰め後の表示セル・行合計・列合計と完全に整合する。
/// </param>
/// <param name="LatestWeek">在庫スナップショット基準週（時間軸絡みでない場合に設定）。</param>
/// <param name="AvailableMetrics">時間軸が含まれる場合は在庫系を除いたメトリクスキー一覧。</param>
/// <param name="RowTruncated">行ラベル数が 100 を超え切り詰められた場合 true。</param>
/// <param name="ColumnTruncated">列ラベル数が 100 を超え切り詰められた場合 true。</param>
public sealed record CrosstabMatrixResponse(
    CrosstabDimensionInfo RowDimension,
    CrosstabDimensionInfo ColumnDimension,
    IReadOnlyList<string> RowLabels,
    IReadOnlyList<string> ColumnLabels,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, CrosstabCell>> Cells,
    IReadOnlyDictionary<string, CrosstabCell> RowTotals,
    IReadOnlyDictionary<string, CrosstabCell> ColumnTotals,
    CrosstabCell GrandTotal,
    string? LatestWeek,
    IReadOnlyList<string> AvailableMetrics,
    bool RowTruncated,
    bool ColumnTruncated);

// ============================================================
// 商品マスタ（m_product / m_product_sku）の参照モデル
// ============================================================

/// <summary>商品マスタの代表画像情報（SKU+image_index の代表1枚）。</summary>
public sealed record MasterProductSummary(
    Guid ProductId,
    string BusinessCategoryCd,
    string BusinessCategorySign,
    int DivisionCd,
    string DivisionName,
    string ProductName,
    string? Brand,
    string ProductSign,
    string? Manager,
    string ProductTypeCrd,
    int SkuCount,
    int ColorCount,
    int SizeCount,
    int? MinSalesPrice,
    int? MaxSalesPrice,
    string? PrimaryImageUrl);

/// <summary>商品マスタの SKU 1件（同一 SKU の画像は IReadOnlyList で持つ）。</summary>
public sealed record MasterProductSku(
    Guid SkuItemId,
    string UnitCd,
    string ColorName,
    string SizeName,
    int SalesPrice,
    int CostPrice,
    IReadOnlyList<MasterProductSkuImage> Images);

/// <summary>SKU の画像1件。</summary>
public sealed record MasterProductSkuImage(
    Guid ImageId,
    int ImageIndex,
    string? ImageFileName,
    string ImageUrl);

/// <summary>商品マスタ詳細（親 + SKU 一覧）。</summary>
public sealed record MasterProductDetail(
    MasterProductSummary Summary,
    IReadOnlyList<MasterProductSku> Skus);

/// <summary>商品マスタ一覧のページ。</summary>
public sealed record MasterProductPage(
    IReadOnlyList<MasterProductSummary> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>商品マスタ専用フィルタの選択肢一式。</summary>
public sealed record MasterFilterOptions(
    IReadOnlyList<BusinessTypeOption> BusinessTypes,
    IReadOnlyList<CodeName> Divisions,
    IReadOnlyList<string> Brands,
    IReadOnlyList<string> Managers);

// ============================================================
// 商品軸の分析
// ============================================================

/// <summary>商品単位の期間内 KPI。</summary>
/// <remarks>
/// 取引先（customer_code）は本アプリでは常に同じ値（メーカー固有コード）のため、
/// 旧 StoreCount = COUNT(DISTINCT customer_code) は常に 1 となり指標として無意味だった。
/// よって本 KPI からは除外している。
/// </remarks>
public sealed record ProductAnalyticsKpi(
    long Quantity,
    long Amount,
    long GrossProfit,
    double GrossProfitRate,
    long CurrentStock,
    double SellThroughRate,
    double AverageStockDays,
    DateOnly? LatestWeek);

/// <summary>SKU別の売上集計（色・サイズ別）。</summary>
public sealed record ProductSkuPerformance(
    string UnitCd,
    string ColorName,
    string SizeName,
    int SalesPrice,
    string? PrimaryImageUrl,
    long Quantity,
    long Amount,
    long GrossProfit,
    long Stock,
    double SharePercent);

/// <summary>業態別の売上集計。</summary>
public sealed record ProductBusinessTypePerformance(
    string BusinessCategoryCd,
    string? DisplayName,
    string? ShortName,
    long Quantity,
    long Amount,
    long GrossProfit,
    double SharePercent);

/// <summary>
/// 商品分析のレスポンス（指定商品の包括的な売上分析）。
/// 取引先別売上は customer_code が常に同じ値のため意味を持たず、提供しない。
/// </summary>
public sealed record ProductAnalyticsResponse(
    MasterProductSummary Product,
    ProductAnalyticsKpi Kpi,
    IReadOnlyList<TrendPoint> WeeklyTrend,
    IReadOnlyList<ProductSkuPerformance> BySku,
    IReadOnlyList<ProductBusinessTypePerformance> ByBusinessType);
