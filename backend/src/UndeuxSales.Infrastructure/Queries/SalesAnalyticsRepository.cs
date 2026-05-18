using Dapper;
using Npgsql;
using UndeuxSales.Core;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>売上参照データに対する分析クエリを提供するリポジトリ。</summary>
public sealed class SalesAnalyticsRepository
{
    // 当週（月〜日）の売上数量合計式。テーブル別名は sw に固定。
    private const string WeekQty =
        "(sw.toshu_uriage_count1 + sw.toshu_uriage_count2 + sw.toshu_uriage_count3 "
        + "+ sw.toshu_uriage_count4 + sw.toshu_uriage_count5 + sw.toshu_uriage_count6 "
        + "+ sw.toshu_uriage_count7)";

    private const int MaxBreakdownLimit = 1000;
    private const int MaxPageSize = 200;

    private readonly IDbConnectionFactory _connectionFactory;

    public SalesAnalyticsRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        DapperConfiguration.Initialize();
    }

    /// <summary>全社サマリー（KPI＋週次トレンド）を取得する。</summary>
    public async Task<SummaryResponse> GetSummaryAsync(
        SalesQueryFilter filter, CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);

        var flowSql = $"""
            SELECT
                COALESCE(SUM(week_qty), 0)::bigint                   AS quantity,
                COALESCE(SUM(week_qty * baika), 0)::bigint           AS amount,
                COALESCE(SUM(week_qty * (baika - genka)), 0)::bigint AS gross_profit,
                COUNT(DISTINCT product_key)::int                    AS product_count
            FROM (
                SELECT {WeekQty}::bigint AS week_qty,
                       sw.baika, sw.genka,
                       sw.hinban_code || '|' || sw.tanpin_code || '|' || sw.shohin_kigou
                           AS product_key
                FROM sales_weekly sw
                {SalesFilterSql.WhereClause(filter, "sw")}
            ) t;
            """;
        var flow = await connection.QuerySingleAsync<FlowRow>(
            new CommandDefinition(flowSql, parameters, cancellationToken: cancellationToken));

        var latestWeek = await QueryLatestWeekAsync(connection, filter, parameters, cancellationToken);

        long currentStock = 0;
        long cumulativeSales = 0;
        long cumulativeDelivery = 0;
        if (latestWeek.HasValue)
        {
            parameters.Add("latestWeek", latestWeek.Value);
            var snapshotSql = $"""
                SELECT COALESCE(SUM(sw.zaikosu), 0)::bigint             AS stock,
                       COALESCE(SUM(sw.ruikei_uriage_count), 0)::bigint AS cumulative_sales,
                       COALESCE(SUM(sw.ruikei_nohin_count), 0)::bigint  AS cumulative_delivery
                FROM sales_weekly sw
                WHERE sw.import_date = @latestWeek{SalesFilterSql.AndClause(filter, "sw")};
                """;
            var snapshot = await connection.QuerySingleAsync<SnapshotRow>(
                new CommandDefinition(snapshotSql, parameters, cancellationToken: cancellationToken));
            currentStock = snapshot.Stock;
            cumulativeSales = snapshot.CumulativeSales;
            cumulativeDelivery = snapshot.CumulativeDelivery;
        }

        var weeklyTrend = await QueryWeeklyTrendAsync(connection, filter, parameters, cancellationToken);

        var kpi = new SalesKpi(
            flow.Quantity,
            flow.Amount,
            flow.GrossProfit,
            Ratio(flow.GrossProfit, flow.Amount),
            flow.ProductCount,
            currentStock,
            Ratio(cumulativeSales, cumulativeDelivery),
            latestWeek);

        return new SummaryResponse(kpi, weeklyTrend);
    }

    /// <summary>売上トレンド（日次／週次）を取得する。</summary>
    public async Task<TrendResponse> GetTrendAsync(
        SalesQueryFilter filter,
        TrendGranularity granularity,
        CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);

        IReadOnlyList<TrendPoint> points = granularity == TrendGranularity.Daily
            ? await QueryDailyTrendAsync(connection, filter, parameters, cancellationToken)
            : await QueryWeeklyTrendAsync(connection, filter, parameters, cancellationToken);

        return new TrendResponse(granularity.ToString().ToLowerInvariant(), points);
    }

    /// <summary>集計軸別の売上ランキングを取得する。</summary>
    public async Task<BreakdownResponse> GetBreakdownAsync(
        SalesQueryFilter filter,
        BreakdownDimension dimension,
        SalesMetric metric,
        bool ascending,
        int limit,
        CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        limit = Math.Clamp(limit, 1, MaxBreakdownLimit);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);
        parameters.Add("limit", limit);

        var (groupBy, keyExpr, labelExpr) = ResolveDimension(dimension);
        var metricColumn = MetricColumn(metric);
        var direction = ascending ? "ASC" : "DESC";

        // ウィンドウ SUM は bigint 入力でも numeric を返すため明示的に bigint へ戻す。
        var sql = $"""
            SELECT key, label, quantity, amount, gross_profit,
                   (SUM(quantity) OVER ())::bigint     AS total_quantity,
                   (SUM(amount) OVER ())::bigint       AS total_amount,
                   (SUM(gross_profit) OVER ())::bigint AS total_gross_profit
            FROM (
                SELECT {keyExpr} AS key,
                       {labelExpr} AS label,
                       COALESCE(SUM({WeekQty}), 0)::bigint                   AS quantity,
                       COALESCE(SUM({WeekQty}::bigint * sw.baika), 0)::bigint AS amount,
                       COALESCE(SUM({WeekQty}::bigint * (sw.baika - sw.genka)), 0)::bigint
                           AS gross_profit
                FROM sales_weekly sw
                {SalesFilterSql.WhereClause(filter, "sw")}
                GROUP BY {groupBy}
            ) g
            ORDER BY {metricColumn} {direction}, key
            LIMIT @limit;
            """;

        var rawRows = (await connection.QueryAsync<BreakdownRawRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        var rows = rawRows
            .Select(r => new BreakdownRow(
                r.Key,
                r.Label,
                r.Quantity,
                r.Amount,
                r.GrossProfit,
                SharePercent(r, metric)))
            .ToList();

        return new BreakdownResponse(
            dimension.ToString(),
            metric.ToString(),
            rows);
    }

    /// <summary>在庫・発注分析（最新週スナップショット基準）を取得する。</summary>
    public async Task<InventoryResponse> GetInventoryAsync(
        SalesQueryFilter filter, CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);

        var latestWeek = await QueryLatestWeekAsync(connection, filter, parameters, cancellationToken);
        if (!latestWeek.HasValue)
        {
            return new InventoryResponse(
                new InventoryKpi(0, 0m, 0, 0, 0, 0, 0, null),
                Array.Empty<InventoryBreakdownRow>());
        }

        parameters.Add("latestWeek", latestWeek.Value);
        var andClause = SalesFilterSql.AndClause(filter, "sw");

        var kpiSql = $"""
            SELECT COALESCE(SUM(sw.zaikosu), 0)::bigint             AS total_stock,
                   COALESCE(SUM(sw.hatchu_count), 0)                AS total_order_quantity,
                   COALESCE(SUM(sw.sakizuke_count), 0)::bigint      AS total_advance_quantity,
                   COALESCE(SUM(sw.ruikei_uriage_count), 0)::bigint AS cumulative_sales,
                   COALESCE(SUM(sw.ruikei_nohin_count), 0)::bigint  AS cumulative_delivery,
                   COALESCE(AVG(sw.zainiti), 0)::float8             AS average_stock_days
            FROM sales_weekly sw
            WHERE sw.import_date = @latestWeek{andClause};
            """;
        var kpiRow = await connection.QuerySingleAsync<InventoryKpiRow>(
            new CommandDefinition(kpiSql, parameters, cancellationToken: cancellationToken));

        var breakdownSql = $"""
            SELECT sw.department AS key,
                   sw.department AS label,
                   COALESCE(SUM(sw.zaikosu), 0)::bigint             AS stock,
                   COALESCE(SUM(sw.hatchu_count), 0)                AS order_quantity,
                   COALESCE(SUM(sw.sakizuke_count), 0)::bigint      AS advance_quantity,
                   COALESCE(SUM(sw.ruikei_uriage_count), 0)::bigint AS cumulative_sales,
                   COALESCE(SUM(sw.ruikei_nohin_count), 0)::bigint  AS cumulative_delivery
            FROM sales_weekly sw
            WHERE sw.import_date = @latestWeek{andClause}
            GROUP BY sw.department
            ORDER BY stock DESC, key;
            """;
        var breakdownRaw = await connection.QueryAsync<InventoryBreakdownRawRow>(
            new CommandDefinition(breakdownSql, parameters, cancellationToken: cancellationToken));

        var byDepartment = breakdownRaw
            .Select(r => new InventoryBreakdownRow(
                r.Key,
                r.Label,
                r.Stock,
                r.OrderQuantity,
                r.AdvanceQuantity,
                Ratio(r.CumulativeSales, r.CumulativeDelivery)))
            .ToList();

        var kpi = new InventoryKpi(
            kpiRow.TotalStock,
            kpiRow.TotalOrderQuantity,
            kpiRow.TotalAdvanceQuantity,
            kpiRow.CumulativeSales,
            kpiRow.CumulativeDelivery,
            Ratio(kpiRow.CumulativeSales, kpiRow.CumulativeDelivery),
            kpiRow.AverageStockDays,
            latestWeek);

        return new InventoryResponse(kpi, byDepartment);
    }

    /// <summary>商品別分析の一覧（ページング）を取得する。</summary>
    public async Task<ProductPage> GetProductsAsync(
        SalesQueryFilter filter,
        ProductSortKey sortKey,
        bool ascending,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);

        var latestWeek = await QueryLatestWeekAsync(connection, filter, parameters, cancellationToken);
        if (!latestWeek.HasValue)
        {
            return new ProductPage(Array.Empty<ProductRow>(), 0, page, pageSize);
        }

        parameters.Add("latestWeek", latestWeek.Value);
        parameters.Add("limit", pageSize);
        parameters.Add("offset", (page - 1) * pageSize);

        var sortExpression = ProductSortExpression(sortKey);
        var direction = ascending ? "ASC" : "DESC";

        var sql = $"""
            WITH stock AS (
                SELECT sw.hinban_code,
                       sw.tanpin_code,
                       MAX(sw.hinmei)       AS hinmei,
                       MAX(sw.shohin_kigou) AS shohin_kigou,
                       MAX(sw.kisetsu)      AS kisetsu,
                       COALESCE(SUM(sw.zaikosu), 0)::bigint             AS stock,
                       COALESCE(SUM(sw.ruikei_uriage_count), 0)::bigint AS cumulative_sales,
                       COALESCE(SUM(sw.ruikei_nohin_count), 0)::bigint  AS cumulative_delivery,
                       COALESCE(AVG(sw.zainiti), 0)::float8             AS average_stock_days
                FROM sales_weekly sw
                WHERE sw.import_date = @latestWeek{SalesFilterSql.AndClause(filter, "sw")}
                GROUP BY sw.hinban_code, sw.tanpin_code
            ),
            flow AS (
                SELECT sw.hinban_code,
                       sw.tanpin_code,
                       COALESCE(SUM({WeekQty}), 0)::bigint                   AS sales_quantity,
                       COALESCE(SUM({WeekQty}::bigint * sw.baika), 0)::bigint AS sales_amount,
                       COALESCE(SUM({WeekQty}::bigint * (sw.baika - sw.genka)), 0)::bigint
                           AS gross_profit
                FROM sales_weekly sw
                {SalesFilterSql.WhereClause(filter, "sw")}
                GROUP BY sw.hinban_code, sw.tanpin_code
            )
            SELECT s.hinban_code,
                   s.tanpin_code,
                   s.hinmei,
                   s.shohin_kigou,
                   s.kisetsu,
                   COALESCE(f.sales_quantity, 0) AS sales_quantity,
                   COALESCE(f.sales_amount, 0)   AS sales_amount,
                   COALESCE(f.gross_profit, 0)   AS gross_profit,
                   s.stock,
                   s.cumulative_sales,
                   s.cumulative_delivery,
                   s.average_stock_days,
                   (COUNT(*) OVER ())::int       AS total_count
            FROM stock s
            LEFT JOIN flow f
                ON f.hinban_code = s.hinban_code AND f.tanpin_code = s.tanpin_code
            ORDER BY {sortExpression} {direction} NULLS LAST, s.hinban_code, s.tanpin_code
            LIMIT @limit OFFSET @offset;
            """;

        var rawRows = (await connection.QueryAsync<ProductRawRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        var totalCount = rawRows.Count > 0 ? rawRows[0].TotalCount : 0;
        var items = rawRows
            .Select(r => new ProductRow(
                r.HinbanCode,
                r.TanpinCode,
                r.Hinmei,
                r.ShohinKigou,
                r.Kisetsu,
                r.SalesQuantity,
                r.SalesAmount,
                r.GrossProfit,
                r.Stock,
                Ratio(r.CumulativeSales, r.CumulativeDelivery),
                r.AverageStockDays))
            .ToList();

        return new ProductPage(items, totalCount, page, pageSize);
    }

    private static async Task<DateOnly?> QueryLatestWeekAsync(
        NpgsqlConnection connection,
        SalesQueryFilter filter,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT MAX(sw.import_date)
            FROM sales_weekly sw
            {SalesFilterSql.WhereClause(filter, "sw")};
            """;
        return await connection.ExecuteScalarAsync<DateOnly?>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    private static async Task<IReadOnlyList<TrendPoint>> QueryWeeklyTrendAsync(
        NpgsqlConnection connection,
        SalesQueryFilter filter,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT sw.import_date AS date,
                   COALESCE(SUM({WeekQty}), 0)::bigint                   AS quantity,
                   COALESCE(SUM({WeekQty}::bigint * sw.baika), 0)::bigint AS amount,
                   COALESCE(SUM({WeekQty}::bigint * (sw.baika - sw.genka)), 0)::bigint
                       AS gross_profit
            FROM sales_weekly sw
            {SalesFilterSql.WhereClause(filter, "sw")}
            GROUP BY sw.import_date
            ORDER BY sw.import_date;
            """;
        var rows = await connection.QueryAsync<TrendPoint>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private static async Task<IReadOnlyList<TrendPoint>> QueryDailyTrendAsync(
        NpgsqlConnection connection,
        SalesQueryFilter filter,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT vsd.sales_date AS date,
                   COALESCE(SUM(vsd.quantity), 0)::bigint     AS quantity,
                   COALESCE(SUM(vsd.amount), 0)::bigint       AS amount,
                   COALESCE(SUM(vsd.gross_profit), 0)::bigint AS gross_profit
            FROM v_sales_daily vsd
            {SalesFilterSql.WhereClause(filter, "vsd")}
            GROUP BY vsd.sales_date
            ORDER BY vsd.sales_date;
            """;
        var rows = await connection.QueryAsync<TrendPoint>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private static (string GroupBy, string KeyExpr, string LabelExpr) ResolveDimension(
        BreakdownDimension dimension) => dimension switch
    {
        BreakdownDimension.Department =>
            ("sw.department", "sw.department", "sw.department"),
        BreakdownDimension.Customer =>
            ("sw.customer_code", "sw.customer_code", "sw.customer_code"),
        BreakdownDimension.BusinessType =>
            ("sw.gyotai_code", "sw.gyotai_code", "sw.gyotai_code"),
        BreakdownDimension.Season =>
            ("sw.kisetsu", "sw.kisetsu", "sw.kisetsu"),
        BreakdownDimension.Color =>
            ("sw.color", "sw.color", "sw.color"),
        BreakdownDimension.Size =>
            ("sw.size", "sw.size", "sw.size"),
        BreakdownDimension.Product =>
            ("sw.hinban_code, sw.tanpin_code",
             "sw.hinban_code || '-' || sw.tanpin_code",
             "MAX(sw.hinmei)"),
        _ => throw new AppException(ErrorCodes.UnknownDimension, 400),
    };

    private static string MetricColumn(SalesMetric metric) => metric switch
    {
        SalesMetric.Quantity => "quantity",
        SalesMetric.Amount => "amount",
        SalesMetric.GrossProfit => "gross_profit",
        _ => throw new AppException(ErrorCodes.InvalidRequest, 400),
    };

    private static string ProductSortExpression(ProductSortKey sortKey) => sortKey switch
    {
        ProductSortKey.SalesQuantity => "sales_quantity",
        ProductSortKey.SalesAmount => "sales_amount",
        ProductSortKey.GrossProfit => "gross_profit",
        ProductSortKey.Stock => "s.stock",
        ProductSortKey.SellThroughRate =>
            "(s.cumulative_sales::float8 / NULLIF(s.cumulative_delivery, 0))",
        ProductSortKey.StockDays => "s.average_stock_days",
        _ => "sales_amount",
    };

    private static double SharePercent(BreakdownRawRow row, SalesMetric metric) => metric switch
    {
        SalesMetric.Quantity => Ratio(row.Quantity, row.TotalQuantity) * 100.0,
        SalesMetric.Amount => Ratio(row.Amount, row.TotalAmount) * 100.0,
        SalesMetric.GrossProfit => Ratio(row.GrossProfit, row.TotalGrossProfit) * 100.0,
        _ => 0.0,
    };

    // 分母0は0を返す（ゼロ除算の防止）。
    private static double Ratio(long numerator, long denominator)
        => denominator == 0 ? 0.0 : (double)numerator / denominator;

    private sealed record FlowRow(long Quantity, long Amount, long GrossProfit, int ProductCount);

    private sealed record SnapshotRow(long Stock, long CumulativeSales, long CumulativeDelivery);

    private sealed record BreakdownRawRow(
        string Key,
        string Label,
        long Quantity,
        long Amount,
        long GrossProfit,
        long TotalQuantity,
        long TotalAmount,
        long TotalGrossProfit);

    private sealed record InventoryKpiRow(
        long TotalStock,
        decimal TotalOrderQuantity,
        long TotalAdvanceQuantity,
        long CumulativeSales,
        long CumulativeDelivery,
        double AverageStockDays);

    private sealed record InventoryBreakdownRawRow(
        string Key,
        string Label,
        long Stock,
        decimal OrderQuantity,
        long AdvanceQuantity,
        long CumulativeSales,
        long CumulativeDelivery);

    private sealed record ProductRawRow(
        string HinbanCode,
        string TanpinCode,
        string Hinmei,
        string ShohinKigou,
        string Kisetsu,
        long SalesQuantity,
        long SalesAmount,
        long GrossProfit,
        long Stock,
        long CumulativeSales,
        long CumulativeDelivery,
        double AverageStockDays,
        int TotalCount);
}
