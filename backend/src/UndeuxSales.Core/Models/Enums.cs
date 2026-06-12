namespace UndeuxSales.Core.Models;

/// <summary>取込種別。</summary>
public enum ImportSourceType
{
    /// <summary>初期DBダンプの一括投入。</summary>
    InitialDump,

    /// <summary>週次CSVファイルの取込。</summary>
    WeeklyCsv,
}

/// <summary>取込バッチの状態。</summary>
public enum ImportStatus
{
    Processing,
    Completed,
    Failed,
}

/// <summary>売上分析の集計軸。</summary>
/// <remarks>
/// 取引先（customer_code）は本アプリのユーザー（メーカー）に対して小売側が振り出した
/// 固有コードで、本アプリ内では常に同じ値となるため集計軸・フィルタとして無意味。
/// よって BreakdownDimension からは除外している（sales_weekly.customer_code カラム自体は
/// 取込で必要なため残置）。
/// </remarks>
public enum BreakdownDimension
{
    /// <summary>部門。</summary>
    Department,

    /// <summary>業態。</summary>
    BusinessType,

    /// <summary>季節区分。</summary>
    Season,

    /// <summary>商品（品番・単品）。</summary>
    Product,

    /// <summary>カラー。</summary>
    Color,

    /// <summary>サイズ。</summary>
    Size,

    /// <summary>品番3桁（hinban_code 単独）。</summary>
    Hinban,

    /// <summary>帳票区分名。</summary>
    ChohyoKubun,

    /// <summary>棚割1。</summary>
    Tanawari1,

    /// <summary>棚割2。</summary>
    Tanawari2,

    /// <summary>商品記号。</summary>
    ShohinKigo,
}

/// <summary>時系列トレンドの粒度。</summary>
public enum TrendGranularity
{
    /// <summary>日次。</summary>
    Daily,

    /// <summary>週次（取込日単位）。</summary>
    Weekly,
}

/// <summary>売上指標。</summary>
public enum SalesMetric
{
    /// <summary>売上数量。</summary>
    Quantity,

    /// <summary>売上金額（数量 × 売価）。</summary>
    Amount,

    /// <summary>粗利（数量 × (売価 - 原価)）。</summary>
    GrossProfit,
}

/// <summary>
/// 気温分析のエリア種別。括弧内は参照する観測地点。
/// クロス集計の気温メトリクスや散布図・重回帰の説明変数に用いる。
/// </summary>
public enum TemperatureArea
{
    /// <summary>標準（東京）。</summary>
    Standard,

    /// <summary>寒冷（札幌）。</summary>
    Cold,

    /// <summary>温暖（那覇）。</summary>
    Warm,
}

/// <summary>商品別分析の並び替えキー。</summary>
public enum ProductSortKey
{
    /// <summary>売上数量。</summary>
    SalesQuantity,

    /// <summary>売上金額。</summary>
    SalesAmount,

    /// <summary>粗利。</summary>
    GrossProfit,

    /// <summary>在庫数。</summary>
    Stock,

    /// <summary>消化率。</summary>
    SellThroughRate,

    /// <summary>在庫日数（在日）。</summary>
    StockDays,
}

/// <summary>在庫アクション明細（inventory/items）の並び替えキー。</summary>
public enum InventoryItemSortKey
{
    /// <summary>在庫数。</summary>
    Stock,

    /// <summary>在庫金額（原価ベース）。</summary>
    StockValueCost,

    /// <summary>在庫日数（在日）。</summary>
    StockDays,

    /// <summary>消化率。</summary>
    SellThroughRate,

    /// <summary>出荷ゼロ継続週数。</summary>
    ZeroSalesWeeks,

    /// <summary>発注残。</summary>
    OrderQuantity,
}
