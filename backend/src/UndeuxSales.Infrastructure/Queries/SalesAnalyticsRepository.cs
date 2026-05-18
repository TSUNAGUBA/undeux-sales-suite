using Dapper;
using Npgsql;
using UndeuxSales.Core;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>売上参照データに対する分析クエリを提供するリポジトリ。</summary>
public sealed class SalesAnalyticsRepository
{
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

        // 相互に依存しない3クエリを並行実行する（各クエリは個別の接続を使用）。
        var trendTask = QueryWeeklyTrendAsync(filter, cancellationToken);
        var productCountTask = QueryProductCountAsync(filter, cancellationToken);
        var snapshotTask = QuerySnapshotAsync(filter, cancellationToken);
        await Task.WhenAll(trendTask, productCountTask, snapshotTask);

        var weeklyTrend = await trendTask;
        var productCount = await productCountTask;
        var snapshot = await snapshotTask;

        // フローKPI（数量・金額・粗利）は週次トレンドの合算で算出する（専用クエリ不要）。
        var quantity = weeklyTrend.Sum(point => point.Quantity);
        var amount = weeklyTrend.Sum(point => point.Amount);
        var grossProfit = weeklyTrend.Sum(point => point.GrossProfit);

        var kpi = new SalesKpi(
            quantity,
            amount,
            grossProfit,
            Ratio(grossProfit, amount),
            productCount,
            snapshot.Stock,
            Ratio(snapshot.CumulativeSales, snapshot.CumulativeDelivery),
            snapshot.LatestWeek);

        return new SummaryResponse(kpi, weeklyTrend);
    }

    /// <summary>売上トレンド（日次／週次）を取得する。</summary>
    public async Task<TrendResponse> GetTrendAsync(
        SalesQueryFilter filter,
        TrendGranularity granularity,
        CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();

        var points = granularity == TrendGranularity.Daily
            ? await QueryDailyTrendAsync(filter, cancellationToken)
            : await QueryWeeklyTrendAsync(filter, cancellationToken);

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
                       COALESCE(SUM({SalesMetricSql.WeekQuantity}), 0)::bigint    AS quantity,
                       COALESCE(SUM({SalesMetricSql.WeekAmount}), 0)::bigint      AS amount,
                       COALESCE(SUM({SalesMetricSql.WeekGrossProfit}), 0)::bigint AS gross_profit
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
            .Select(row => new BreakdownRow(
                row.Key,
                row.Label,
                row.Quantity,
                row.Amount,
                row.GrossProfit,
                SharePercent(row, metric)))
            .ToList();

        return new BreakdownResponse(dimension.ToString(), metric.ToString(), rows);
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
            .Select(row => new InventoryBreakdownRow(
                row.Key,
                row.Label,
                row.Stock,
                row.OrderQuantity,
                row.AdvanceQuantity,
                Ratio(row.CumulativeSales, row.CumulativeDelivery)))
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

        // 最新週の特定は MAX(import_date)（インデックス利用、サブミリ秒）。
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
                       COALESCE(SUM({SalesMetricSql.WeekQuantity}), 0)::bigint    AS sales_quantity,
                       COALESCE(SUM({SalesMetricSql.WeekAmount}), 0)::bigint      AS sales_amount,
                       COALESCE(SUM({SalesMetricSql.WeekGrossProfit}), 0)::bigint AS gross_profit
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
            .Select(row => new ProductRow(
                row.HinbanCode,
                row.TanpinCode,
                row.Hinmei,
                row.ShohinKigou,
                row.Kisetsu,
                row.SalesQuantity,
                row.SalesAmount,
                row.GrossProfit,
                row.Stock,
                Ratio(row.CumulativeSales, row.CumulativeDelivery),
                row.AverageStockDays))
            .ToList();

        return new ProductPage(items, totalCount, page, pageSize);
    }

    // ------------------------------------------------------------
    // 内部クエリ（サマリー用は各々が個別の接続を開き並行実行可能）
    // ------------------------------------------------------------

    private async Task<IReadOnlyList<TrendPoint>> QueryWeeklyTrendAsync(
        SalesQueryFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);

        var sql = $"""
            SELECT sw.import_date AS date,
                   COALESCE(SUM({SalesMetricSql.WeekQuantity}), 0)::bigint    AS quantity,
                   COALESCE(SUM({SalesMetricSql.WeekAmount}), 0)::bigint      AS amount,
                   COALESCE(SUM({SalesMetricSql.WeekGrossProfit}), 0)::bigint AS gross_profit
            FROM sales_weekly sw
            {SalesFilterSql.WhereClause(filter, "sw")}
            GROUP BY sw.import_date
            ORDER BY sw.import_date;
            """;

        var rows = await connection.QueryAsync<TrendPoint>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private async Task<IReadOnlyList<TrendPoint>> QueryDailyTrendAsync(
        SalesQueryFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);

        // 取込日単位で先に集計してから日次7列を展開する（縦展開前に集約し高速化）。
        var weeklySums = new List<string>();
        var dailyValues = new List<string>();
        for (var day = 1; day <= WeekCalendar.DaysInWeek; day++)
        {
            weeklySums.Add($"SUM(sw.toshu_uriage_count{day})::bigint AS q{day}");
            weeklySums.Add(
                $"SUM(sw.toshu_uriage_count{day}::bigint * sw.baika)::bigint AS a{day}");
            weeklySums.Add(
                $"SUM(sw.toshu_uriage_count{day}::bigint * (sw.baika - sw.genka))::bigint AS g{day}");
            dailyValues.Add($"({day}, q{day}, a{day}, g{day})");
        }

        var sql = $"""
            WITH per_week AS (
                SELECT sw.import_date,
                       {string.Join(",\n                       ", weeklySums)}
                FROM sales_weekly sw
                {SalesFilterSql.WhereClause(filter, "sw")}
                GROUP BY sw.import_date
            )
            SELECT (per_week.import_date - 8 + d.day_index)::date AS date,
                   d.quantity, d.amount, d.gross_profit
            FROM per_week
            CROSS JOIN LATERAL (VALUES
                {string.Join(",\n                ", dailyValues)}
            ) AS d(day_index, quantity, amount, gross_profit)
            ORDER BY date;
            """;

        var rows = await connection.QueryAsync<TrendPoint>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private async Task<int> QueryProductCountAsync(
        SalesQueryFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);

        // COUNT(DISTINCT ...) は遅いため、DISTINCT サブクエリの行数を数える。
        var sql = $"""
            SELECT COUNT(*)::int
            FROM (
                SELECT DISTINCT sw.hinban_code, sw.tanpin_code
                FROM sales_weekly sw
                {SalesFilterSql.WhereClause(filter, "sw")}
            ) d;
            """;

        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    private async Task<SnapshotResult> QuerySnapshotAsync(
        SalesQueryFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);

        var latestWeek = await QueryLatestWeekAsync(connection, filter, parameters, cancellationToken);
        if (!latestWeek.HasValue)
        {
            return new SnapshotResult(0, 0, 0, null);
        }

        parameters.Add("latestWeek", latestWeek.Value);
        var sql = $"""
            SELECT COALESCE(SUM(sw.zaikosu), 0)::bigint             AS stock,
                   COALESCE(SUM(sw.ruikei_uriage_count), 0)::bigint AS cumulative_sales,
                   COALESCE(SUM(sw.ruikei_nohin_count), 0)::bigint  AS cumulative_delivery
            FROM sales_weekly sw
            WHERE sw.import_date = @latestWeek{SalesFilterSql.AndClause(filter, "sw")};
            """;
        var row = await connection.QuerySingleAsync<SnapshotRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));

        return new SnapshotResult(
            row.Stock, row.CumulativeSales, row.CumulativeDelivery, latestWeek);
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

    private sealed record SnapshotRow(long Stock, long CumulativeSales, long CumulativeDelivery);

    private sealed record SnapshotResult(
        long Stock, long CumulativeSales, long CumulativeDelivery, DateOnly? LatestWeek);

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
