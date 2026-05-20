namespace UndeuxSales.Infrastructure.Queries;

/// <summary>コードと表示名の組。</summary>
public sealed record CodeName(string Code, string? Name);

/// <summary>フィルタUIの選択肢一式。</summary>
public sealed record FilterOptions(
    IReadOnlyList<CodeName> Departments,
    IReadOnlyList<CodeName> Customers,
    IReadOnlyList<CodeName> BusinessTypes,
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

/// <summary>商品別分析の1行。</summary>
public sealed record ProductRow(
    string HinbanCode,
    string TanpinCode,
    string Hinmei,
    string ShohinKigou,
    string Kisetsu,
    long SalesQuantity,
    long SalesAmount,
    long GrossProfit,
    long Stock,
    double SellThroughRate,
    double AverageStockDays);

/// <summary>商品別分析のページ。</summary>
public sealed record ProductPage(
    IReadOnlyList<ProductRow> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>クロス集計の基本項目（単品レベル時のみ設定される）。</summary>
public sealed record CrosstabBasicItems(
    string Hinban,
    string Tanpin,
    string Hinmei,
    string ShohinKigo,
    string Color,
    string Size,
    string Kisetsu);

/// <summary>クロス集計の1行。基本項目は単品レベル時のみ非nullになる。</summary>
public sealed record CrosstabRow(
    string Key,
    string Label,
    CrosstabBasicItems? BasicItems,
    long Quantity,
    long Amount,
    long GrossProfit,
    double SharePercent,
    long Stock,
    double StockDays,
    double SellThroughRate);

/// <summary>クロス集計のレスポンス（売上金額の降順）。</summary>
public sealed record CrosstabResponse(
    string Dimension,
    IReadOnlyList<CrosstabRow> Rows,
    DateOnly? LatestWeek);
