using Dapper;
using UndeuxSales.Core;

namespace UndeuxSales.Infrastructure.Queries;

// =====================================================================================
// 在庫アクション分析（GET /api/mart/inventory/actions・/api/mart/inventory/items）の
// レスポンス・内部行・SQL 部品。集計クエリ本体は MartAnalyticsRepository に置き、
// 本ファイルはモデルと SQL 組立（導入管理の MartIntroductionQuery と同じ分担）を担う。
// 判定閾値の SoT は Core の InventoryHealthRules（値は Dapper パラメータで注入する）。
// =====================================================================================

/// <summary>状態別の SKU 件数（タブバッジ・フィルタチップ用）。</summary>
public sealed record InventoryStatusCounts(
    int Healthy,
    int Caution,
    int Stagnant,
    int Dormant,
    int Total);

/// <summary>在庫アクションサマリーの KPI（最新週スナップショット基準）。前週比較にも同型を使う。</summary>
/// <remarks>
/// StockValueCost は「在庫数 × 原価（同一グレインの fact_sales_weekly.cost_price）」の合計。
/// 原価未設定（cost_price=0 または売上行欠落）の SKU は 0 円として加算されるため下振れしうる。
/// HealthyStockRatio / StagnantDormantStockRatio は SKU 件数ではなく在庫点数ベースの比率。
/// </remarks>
public sealed record InventoryActionsKpi(
    long TotalStock,
    long StockValueCost,
    double HealthyStockRatio,
    double SellThroughRate,
    double AverageStockDays,
    double StagnantDormantStockRatio,
    decimal TotalOrderQuantity,
    long TotalAdvanceQuantity,
    long CumulativeDelivery);

/// <summary>部門別の在庫健全性1行（4象限バブルと鮮度積上げ帯の両方を賄う）。</summary>
public sealed record InventoryDepartmentHealthRow(
    string Key,
    string Label,
    long Stock,
    long StockValueCost,
    double SellThroughRate,
    double AverageStockDays,
    long HealthyStock,
    long CautionStock,
    long StagnantStock,
    long DormantStock);

/// <summary>GET /api/mart/inventory/actions のレスポンス。</summary>
/// <remarks>前週比較（PreviousKpi）の差分計算はフロント射影（ランキングの期間比較と同じ思想）。
/// 対象データがない場合は LatestWeek=null・空配列を返す。</remarks>
public sealed record InventoryActionsResponse(
    DateOnly? LatestWeek,
    DateOnly? PreviousWeek,
    InventoryThresholds Thresholds,
    InventoryActionsKpi Kpi,
    InventoryActionsKpi? PreviousKpi,
    InventoryStatusCounts StatusCounts,
    IReadOnlyList<InventoryActionItem> Actions,
    IReadOnlyList<InventoryDepartmentHealthRow> ByDepartment);

/// <summary>在庫アクション明細の1行（SKU 単位・最新週スナップショット基準）。</summary>
/// <remarks>
/// 自然キー（GyotaiCode×ShohinKigou×HinbanCode×TanpinCode）を必ず含める。mart のサロゲートキーは
/// 再構築（TRUNCATE）で振り直されるため、将来のアクションフラグ永続化はこの自然キーで結合する。
/// ZeroSalesWeeks は直近で売上ゼロが続く週数（InventoryThresholds.DormantScanWeeks でキャップ）。
/// </remarks>
public sealed record InventoryItemRow(
    string GyotaiCode,
    string ShohinKigou,
    string HinbanCode,
    string TanpinCode,
    string Hinmei,
    long Stock,
    long StockValueCost,
    double StockDays,
    double SellThroughRate,
    decimal OrderQuantity,
    long AdvanceQuantity,
    int ZeroSalesWeeks,
    string Status,
    string? RecommendedAction,
    Guid? MasterProductId,
    string? ProductName,
    string? Brand,
    string? PrimaryImageUrl);

/// <summary>GET /api/mart/inventory/items のレスポンス（ページング＋状態別件数＋経過バケット件数）。</summary>
/// <remarks>
/// StatusCounts・経過バケット件数は statuses 絞込前（フィルタ・検索適用後）の全体に対する件数。
/// TotalCount は statuses 絞込後の件数（ページングの分母）。
/// StagnantAgingCounts / DormantAgingCounts は Thresholds の AgingBoundaries に対応する3区分。
/// </remarks>
public sealed record InventoryItemsPage(
    IReadOnlyList<InventoryItemRow> Items,
    int TotalCount,
    int Page,
    int PageSize,
    DateOnly? LatestWeek,
    InventoryStatusCounts StatusCounts,
    IReadOnlyList<int> StagnantAgingCounts,
    IReadOnlyList<int> DormantAgingCounts,
    InventoryThresholds Thresholds);

/// <summary>
/// 在庫健全性分類の SQL 部品。閾値は <see cref="InventoryHealthRules"/>（SoT）から
/// Dapper パラメータとして注入し、SQL 文字列に値を埋め込まない。
/// </summary>
internal static class InventoryHealthSql
{
    /// <summary>閾値パラメータを登録する（@anchorWeek・@scanStartWeek は呼び出し側で別途登録）。</summary>
    public static void AddThresholdParameters(DynamicParameters parameters)
    {
        parameters.Add("cautionStockDays", InventoryHealthRules.CautionStockDays);
        parameters.Add("stagnantStockDays", InventoryHealthRules.StagnantStockDays);
        parameters.Add("stagnantSellThroughCeiling", InventoryHealthRules.StagnantSellThroughCeiling);
        parameters.Add("dormantLookbackWeeks", InventoryHealthRules.DormantLookbackWeeks);
        parameters.Add("dormantScanWeeks", InventoryHealthRules.DormantScanWeeks);
    }

    /// <summary>
    /// 出荷ゼロ継続週数の式（dim_date 結合済みの last_sold 行 <c>ls</c> を前提）。
    /// 直近 DormantScanWeeks 週に売上がない SKU は走査上限でキャップする。
    /// PostgreSQL の date 同士の減算は日数（integer）を返すため /7 で週数に変換する。
    /// </summary>
    private const string ZeroSalesWeeksExpression =
        "LEAST(COALESCE((@anchorWeek - ls.last_sold_week) / 7, @dormantScanWeeks), @dormantScanWeeks)";

    /// <summary>
    /// 在庫健全性の状態 CASE 式。<see cref="InventoryHealthRules.Classify"/> と同一論理
    /// （変更時は両方を更新し単体テストで検証すること）。在庫ゼロは healthy、不動が滞留より優先。
    /// </summary>
    private const string StatusCaseExpression = $"""
        CASE
            WHEN i.stock <= 0 THEN '{InventoryHealthRules.StatusHealthy}'
            WHEN i.zero_sales_weeks >= @dormantLookbackWeeks THEN '{InventoryHealthRules.StatusDormant}'
            WHEN i.stock_days > @stagnantStockDays
                 AND i.sell_through_rate < @stagnantSellThroughCeiling THEN '{InventoryHealthRules.StatusStagnant}'
            WHEN i.stock_days > @cautionStockDays THEN '{InventoryHealthRules.StatusCaution}'
            ELSE '{InventoryHealthRules.StatusHealthy}'
        END
        """;

    /// <summary>
    /// 共通 CTE（inv → last_sold → classified）を組み立てる。利用側は <c>classified c</c> を参照する。
    /// </summary>
    /// <param name="filterAndClause"><c>MartFilterSql.AndClause(filter, "inv")</c> の結果。</param>
    /// <remarks>
    /// <para>
    /// inv: 指定週（@anchorWeek）のスナップショットを SKU 単位に集約。在庫金額（原価）は同一グレイン
    /// （週×小売×SKU）で 1:1 に存在する fact_sales_weekly.cost_price を LEFT JOIN して算出する
    /// （mart.rebuild() が両ファクトを同一 CTE から INSERT するため欠落しない。防御的に COALESCE 0）。
    /// </para>
    /// <para>
    /// last_sold: 売上ファクトの走査を直近 @dormantScanWeeks 週（@scanStartWeek 超〜@anchorWeek）に
    /// 限定し、SKU ごとの最終販売週を取得する（約160万行のフルスキャン・ウィンドウ関数を回避）。
    /// </para>
    /// <para>
    /// stock_days は SKU 単位の平均（既存 GetInventoryAsync の KPI は生スナップショット行の平均。
    /// 商品自然キーに業態を含むため SKU の小売行はほぼ1件であり実用上は一致する）。
    /// </para>
    /// </remarks>
    public static string ClassifiedCte(string filterAndClause) => $"""
        WITH inv AS (
            SELECT inv.sku_key,
                   COALESCE(SUM(inv.stock), 0)::bigint        AS stock,
                   COALESCE(SUM(inv.cum_sales), 0)::bigint    AS cum_sales,
                   COALESCE(SUM(inv.cum_delivery), 0)::bigint AS cum_delivery,
                   COALESCE(SUM(inv.order_qty), 0)            AS order_qty,
                   COALESCE(SUM(inv.advance_qty), 0)::bigint  AS advance_qty,
                   COALESCE(AVG(inv.stock_days), 0)::float8   AS stock_days,
                   COALESCE(SUM(inv.stock * COALESCE(f.cost_price, 0)), 0)::bigint AS stock_value_cost
            FROM mart.fact_inventory_snapshot inv
            JOIN mart.dim_date     dd ON dd.date_key     = inv.date_key
            JOIN mart.dim_product  dp ON dp.product_key  = inv.product_key
            JOIN mart.dim_retailer dr ON dr.retailer_key = inv.retailer_key
            LEFT JOIN mart.fact_sales_weekly f
                   ON f.date_key     = inv.date_key
                  AND f.retailer_key = inv.retailer_key
                  AND f.product_key  = inv.product_key
                  AND f.sku_key      = inv.sku_key
            WHERE dd.week_monday = @anchorWeek{filterAndClause}
            GROUP BY inv.sku_key
        ),
        last_sold AS (
            SELECT f.sku_key,
                   MAX(dd.week_monday) FILTER (WHERE f.quantity > 0) AS last_sold_week
            FROM mart.fact_sales_weekly f
            JOIN mart.dim_date dd ON dd.date_key = f.date_key
            WHERE dd.week_monday > @scanStartWeek AND dd.week_monday <= @anchorWeek
            GROUP BY f.sku_key
        ),
        enriched AS (
            SELECT i.*,
                   ({ZeroSalesWeeksExpression})::int AS zero_sales_weeks,
                   COALESCE(i.cum_sales::float8 / NULLIF(i.cum_delivery, 0), 0) AS sell_through_rate
            FROM inv i
            LEFT JOIN last_sold ls ON ls.sku_key = i.sku_key
        ),
        classified AS (
            SELECT i.*,
                   {StatusCaseExpression} AS status
            FROM enriched i
        )
        """;

    /// <summary>
    /// 明細の検索条件（品番・商品名・単品コード・ブランド）を返す（未指定は null）。
    /// 導入管理のキーワード検索と同方式の ILIKE エスケープ。dp / ds エイリアスの結合を前提とする。
    /// </summary>
    public static string? SearchCondition(string? search, DynamicParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        var escaped = search
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        parameters.Add("searchPattern", $"%{escaped}%");
        return "(dp.product_code ILIKE @searchPattern ESCAPE '\\' "
            + "OR COALESCE(dp.product_name, '') ILIKE @searchPattern ESCAPE '\\' "
            + "OR ds.unit_code ILIKE @searchPattern ESCAPE '\\' "
            + "OR COALESCE(dp.brand, '') ILIKE @searchPattern ESCAPE '\\')";
    }
}

/// <summary>Dapper マッピング用の内部行（在庫アクションの全体集約）。</summary>
internal sealed record InventoryActionsAggregateRow(
    int HealthyCount,
    int CautionCount,
    int StagnantCount,
    int DormantCount,
    int TotalCount,
    long TotalStock,
    long StockValueCost,
    long HealthyStock,
    long StagnantDormantStock,
    long CumulativeSales,
    long CumulativeDelivery,
    decimal TotalOrderQuantity,
    long TotalAdvanceQuantity,
    double AverageStockDays,
    int StagnantWithOrderCount,
    long DormantStock);

/// <summary>Dapper マッピング用の内部行（部門別在庫健全性）。</summary>
internal sealed record InventoryDepartmentHealthRawRow(
    string Key,
    string Label,
    long Stock,
    long StockValueCost,
    long CumulativeSales,
    long CumulativeDelivery,
    double AverageStockDays,
    long HealthyStock,
    long CautionStock,
    long StagnantStock,
    long DormantStock);

/// <summary>Dapper マッピング用の内部行（在庫アクション明細の1行）。</summary>
internal sealed record InventoryItemRawRow(
    string GyotaiCode,
    string ShohinKigou,
    string HinbanCode,
    string TanpinCode,
    string Hinmei,
    long Stock,
    long StockValueCost,
    double StockDays,
    long CumulativeSales,
    long CumulativeDelivery,
    decimal OrderQuantity,
    long AdvanceQuantity,
    int ZeroSalesWeeks,
    string Status,
    Guid? MasterProductId,
    string? ProductName,
    string? Brand,
    string? PrimaryImageUrl);

/// <summary>Dapper マッピング用の内部行（状態別件数＋経過バケット件数）。</summary>
internal sealed record InventoryItemsCountRow(
    int HealthyCount,
    int CautionCount,
    int StagnantCount,
    int DormantCount,
    int TotalCount,
    int StagnantAging1,
    int StagnantAging2,
    int StagnantAging3,
    int DormantAging1,
    int DormantAging2,
    int DormantAging3);
