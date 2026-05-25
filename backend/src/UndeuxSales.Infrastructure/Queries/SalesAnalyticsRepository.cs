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

        // 単一接続で逐次実行する。各クエリは索引で最適化済みのため合計でも実用速度に収まり、
        // 1リクエストあたりの接続消費を1本に抑える（接続プール枯渇を回避）。
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var weeklyTrend = await QueryWeeklyTrendAsync(connection, filter, cancellationToken);
        var productCount = await QueryProductCountAsync(connection, filter, cancellationToken);
        var snapshot = await QuerySnapshotAsync(connection, filter, cancellationToken);

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
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var points = granularity == TrendGranularity.Daily
            ? await QueryDailyTrendAsync(connection, filter, cancellationToken)
            : await QueryWeeklyTrendAsync(connection, filter, cancellationToken);

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

        // 商品マスタは (gyotai_code × shohin_kigou × hinban_code) で一意。stock 集計の
        // 1行は単品単位だが、商品マスタとの結合は品番までで一意になる（単品差は SKU 側）。
        // 代表画像は対象商品 × tanpin の SKU 行から image_index 最小のものを採用する。
        var sql = $"""
            WITH stock AS (
                SELECT sw.gyotai_code,
                       sw.shohin_kigou,
                       sw.hinban_code,
                       sw.tanpin_code,
                       MAX(sw.hinmei)       AS hinmei,
                       MAX(sw.kisetsu)      AS kisetsu,
                       COALESCE(SUM(sw.zaikosu), 0)::bigint             AS stock,
                       COALESCE(SUM(sw.ruikei_uriage_count), 0)::bigint AS cumulative_sales,
                       COALESCE(SUM(sw.ruikei_nohin_count), 0)::bigint  AS cumulative_delivery,
                       COALESCE(AVG(sw.zainiti), 0)::float8             AS average_stock_days
                FROM sales_weekly sw
                WHERE sw.import_date = @latestWeek{SalesFilterSql.AndClause(filter, "sw")}
                GROUP BY sw.gyotai_code, sw.shohin_kigou, sw.hinban_code, sw.tanpin_code
            ),
            flow AS (
                SELECT sw.gyotai_code,
                       sw.shohin_kigou,
                       sw.hinban_code,
                       sw.tanpin_code,
                       COALESCE(SUM({SalesMetricSql.WeekQuantity}), 0)::bigint    AS sales_quantity,
                       COALESCE(SUM({SalesMetricSql.WeekAmount}), 0)::bigint      AS sales_amount,
                       COALESCE(SUM({SalesMetricSql.WeekGrossProfit}), 0)::bigint AS gross_profit
                FROM sales_weekly sw
                {SalesFilterSql.WhereClause(filter, "sw")}
                GROUP BY sw.gyotai_code, sw.shohin_kigou, sw.hinban_code, sw.tanpin_code
            )
            SELECT s.gyotai_code,
                   s.shohin_kigou,
                   s.hinban_code,
                   s.tanpin_code,
                   s.hinmei,
                   s.kisetsu,
                   COALESCE(f.sales_quantity, 0) AS sales_quantity,
                   COALESCE(f.sales_amount, 0)   AS sales_amount,
                   COALESCE(f.gross_profit, 0)   AS gross_profit,
                   s.stock,
                   s.cumulative_sales,
                   s.cumulative_delivery,
                   s.average_stock_days,
                   mp.product_id   AS master_product_id,
                   mp.product_name AS product_name,
                   mp.brand        AS brand,
                   img.image_url   AS primary_image_url,
                   (COUNT(*) OVER ())::int       AS total_count
            FROM stock s
            LEFT JOIN flow f
                ON f.gyotai_code  = s.gyotai_code
               AND f.shohin_kigou = s.shohin_kigou
               AND f.hinban_code  = s.hinban_code
               AND f.tanpin_code  = s.tanpin_code
            LEFT JOIN m_product mp
                ON mp.business_category_cd = s.gyotai_code
               AND mp.product_sign         = s.shohin_kigou
               AND mp.product_type_crd     = s.hinban_code
            LEFT JOIN LATERAL (
                SELECT msi.image_url
                FROM m_product_sku msi
                WHERE msi.product_id = mp.product_id
                  AND msi.unit_cd    = s.tanpin_code
                ORDER BY msi.image_index, msi.sku_item_id
                LIMIT 1
            ) AS img ON true
            ORDER BY {sortExpression} {direction} NULLS LAST, s.hinban_code, s.tanpin_code
            LIMIT @limit OFFSET @offset;
            """;

        var rawRows = (await connection.QueryAsync<ProductRawRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        // OFFSET overshoot 時 (rawRows が空) でも実件数を返すため、件数は別クエリで取得する。
        // window関数 COUNT(*) OVER () は LIMIT が空集合だと値も返らないため。
        var totalCount = rawRows.Count > 0
            ? rawRows[0].TotalCount
            : await CountProductsAsync(connection, filter, parameters, cancellationToken);
        var items = rawRows
            .Select(row => new ProductRow(
                row.GyotaiCode,
                row.ShohinKigou,
                row.HinbanCode,
                row.TanpinCode,
                row.Hinmei,
                row.Kisetsu,
                row.SalesQuantity,
                row.SalesAmount,
                row.GrossProfit,
                row.Stock,
                Ratio(row.CumulativeSales, row.CumulativeDelivery),
                row.AverageStockDays,
                row.MasterProductId,
                row.ProductName,
                row.Brand,
                row.PrimaryImageUrl))
            .ToList();

        return new ProductPage(items, totalCount, page, pageSize);
    }

    /// <summary>
    /// 商品行（gyotai × shohin_kigou × hinban × tanpin）の総件数を最新週基準で数える。
    /// LIMIT/OFFSET の overshoot 等で本体クエリが空集合を返した場合のフォールバック。
    /// </summary>
    private static async Task<int> CountProductsAsync(
        NpgsqlConnection connection,
        SalesQueryFilter filter,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT COUNT(*)::int
            FROM (
                SELECT DISTINCT sw.gyotai_code, sw.shohin_kigou, sw.hinban_code, sw.tanpin_code
                FROM sales_weekly sw
                WHERE sw.import_date = @latestWeek{SalesFilterSql.AndClause(filter, "sw")}
            ) d;
            """;
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    /// <summary>クロス集計（指定の集計単位での複数メトリクス集計）を取得する。</summary>
    /// <remarks>
    /// フロー指標（数量・金額・粗利）は期間内合算、在日は平均、在庫・累計は最新取込週スナップショット基準。
    /// 単品 (Product) のみ基本項目（品番・単品・商品記号・カラー・サイズ・季節）を返す。
    /// </remarks>
    public async Task<CrosstabResponse> GetCrosstabAsync(
        SalesQueryFilter filter,
        BreakdownDimension dimension,
        CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);

        var latestWeek = await QueryLatestWeekAsync(connection, filter, parameters, cancellationToken);
        if (!latestWeek.HasValue)
        {
            return new CrosstabResponse(dimension.ToString(), Array.Empty<CrosstabRow>(), null);
        }

        parameters.Add("latestWeek", latestWeek.Value);
        parameters.Add("limit", MaxBreakdownLimit);

        var (groupBy, keyExpr, labelExpr) = ResolveDimension(dimension);
        var isProduct = dimension == BreakdownDimension.Product;

        // 単品 (Product) は GROUP BY に gyotai_code/shohin_kigou/hinban_code/tanpin_code が
        // 含まれるため、4 つの基本項目はそのまま列参照可能（MAX 不要）。color/size/kisetsu は
        // GROUP BY 外なので MAX で代表値を採用する。
        var basicItemsSelect = isProduct
            ? "sw.hinban_code  AS hinban, sw.tanpin_code   AS tanpin, "
              + "sw.shohin_kigou AS shohin_kigou, "
              + "MAX(sw.color) AS color, MAX(sw.size) AS size, MAX(sw.kisetsu) AS kisetsu, "
              + "sw.gyotai_code  AS gyotai_code,"
            : "NULL::text AS hinban, NULL::text AS tanpin, NULL::text AS shohin_kigou, "
              + "NULL::text AS color, NULL::text AS size, NULL::text AS kisetsu, "
              + "NULL::text AS gyotai_code,";

        // 単品集計のみ、商品マスタ・代表画像（image_index 最小の SKU 画像）を JOIN する。
        var masterJoin = isProduct
            ? """
              LEFT JOIN m_product mp
                  ON mp.business_category_cd = f.gyotai_code
                 AND mp.product_sign         = f.shohin_kigou
                 AND mp.product_type_crd     = f.hinban
              LEFT JOIN LATERAL (
                  SELECT msi.image_url
                  FROM m_product_sku msi
                  WHERE msi.product_id = mp.product_id
                    AND msi.unit_cd    = f.tanpin
                  ORDER BY msi.image_index, msi.sku_item_id
                  LIMIT 1
              ) AS img ON true
              """
            : string.Empty;

        var masterSelect = isProduct
            ? "mp.product_id   AS master_product_id, "
              + "mp.product_name AS product_name, "
              + "mp.brand        AS brand, "
              + "img.image_url   AS primary_image_url,"
            : "NULL::uuid AS master_product_id, NULL::text AS product_name, "
              + "NULL::text AS brand, NULL::text AS primary_image_url,";

        var whereClause = SalesFilterSql.WhereClause(filter, "sw");
        var andClause = SalesFilterSql.AndClause(filter, "sw");

        var sql = $"""
            WITH flow AS (
                SELECT {keyExpr} AS key,
                       {labelExpr} AS label,
                       {basicItemsSelect}
                       COALESCE(SUM({SalesMetricSql.WeekQuantity}), 0)::bigint    AS quantity,
                       COALESCE(SUM({SalesMetricSql.WeekAmount}), 0)::bigint      AS amount,
                       COALESCE(SUM({SalesMetricSql.WeekGrossProfit}), 0)::bigint AS gross_profit,
                       COALESCE(AVG(sw.zainiti), 0)::float8                       AS stock_days
                FROM sales_weekly sw
                {whereClause}
                GROUP BY {groupBy}
            ),
            snapshot AS (
                SELECT {keyExpr} AS key,
                       COALESCE(SUM(sw.zaikosu), 0)::bigint             AS stock,
                       COALESCE(SUM(sw.ruikei_uriage_count), 0)::bigint AS cumulative_sales,
                       COALESCE(SUM(sw.ruikei_nohin_count), 0)::bigint  AS cumulative_delivery
                FROM sales_weekly sw
                WHERE sw.import_date = @latestWeek{andClause}
                GROUP BY {groupBy}
            )
            SELECT f.key,
                   f.label,
                   f.hinban, f.tanpin, f.shohin_kigou, f.color, f.size, f.kisetsu,
                   {masterSelect}
                   f.quantity, f.amount, f.gross_profit, f.stock_days,
                   COALESCE(s.stock, 0)::bigint               AS stock,
                   COALESCE(s.cumulative_sales, 0)::bigint    AS cumulative_sales,
                   COALESCE(s.cumulative_delivery, 0)::bigint AS cumulative_delivery,
                   (SUM(f.amount) OVER ())::bigint            AS total_amount
            FROM flow f
            {masterJoin}
            LEFT JOIN snapshot s ON s.key = f.key
            ORDER BY f.amount DESC, f.key
            LIMIT @limit;
            """;

        var rawRows = (await connection.QueryAsync<CrosstabRawRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        var rows = rawRows.Select(r => new CrosstabRow(
            r.Key,
            r.Label,
            isProduct
                ? new CrosstabBasicItems(
                    r.Hinban ?? string.Empty,
                    r.Tanpin ?? string.Empty,
                    r.Label,
                    r.ShohinKigou ?? string.Empty,
                    r.Color ?? string.Empty,
                    r.Size ?? string.Empty,
                    r.Kisetsu ?? string.Empty,
                    r.MasterProductId,
                    r.ProductName,
                    r.Brand,
                    r.PrimaryImageUrl)
                : null,
            r.Quantity,
            r.Amount,
            r.GrossProfit,
            SharePercentByAmount(r.Amount, r.TotalAmount),
            r.Stock,
            r.StockDays,
            Ratio(r.CumulativeSales, r.CumulativeDelivery) * 100.0))
            .ToList();

        return new CrosstabResponse(dimension.ToString(), rows, latestWeek);
    }

    // ------------------------------------------------------------
    // 内部クエリ（呼び出し側が開いた接続を共有して逐次実行する）
    // ------------------------------------------------------------

    private static async Task<IReadOnlyList<TrendPoint>> QueryWeeklyTrendAsync(
        NpgsqlConnection connection, SalesQueryFilter filter, CancellationToken cancellationToken)
    {
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

    private static async Task<IReadOnlyList<TrendPoint>> QueryDailyTrendAsync(
        NpgsqlConnection connection, SalesQueryFilter filter, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);

        // 取込日単位で先に集計してから日次7列を展開する（縦展開前に集約し高速化）。
        var weeklySums = new List<string>();
        var dailyValues = new List<string>();
        for (var day = 1; day <= WeekCalendar.DaysInWeek; day++)
        {
            var quantity = SalesMetricSql.DailyQuantity(day);
            weeklySums.Add($"SUM({quantity})::bigint AS q{day}");
            weeklySums.Add($"SUM({SalesMetricSql.Amount(quantity)})::bigint AS a{day}");
            weeklySums.Add($"SUM({SalesMetricSql.GrossProfit(quantity)})::bigint AS g{day}");
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

    private static async Task<int> QueryProductCountAsync(
        NpgsqlConnection connection, SalesQueryFilter filter, CancellationToken cancellationToken)
    {
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

    private static async Task<SnapshotResult> QuerySnapshotAsync(
        NpgsqlConnection connection, SalesQueryFilter filter, CancellationToken cancellationToken)
    {
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
            // 商品マスタの自然キー (gyotai × shohin_kigou × hinban) を含めて一意化することで
            // 同一 (hinban, tanpin) が複数業態で売られているケースの行衝突を防ぎ、
            // m_product との JOIN を 1 対 1 に保つ。表示ラベルは品番-単品（label 列）。
            ("sw.gyotai_code, sw.shohin_kigou, sw.hinban_code, sw.tanpin_code",
             "sw.gyotai_code || '|' || sw.shohin_kigou || '|' || sw.hinban_code || '|' || sw.tanpin_code",
             "sw.hinban_code || '-' || sw.tanpin_code"),
        BreakdownDimension.Hinban =>
            ("sw.hinban_code", "sw.hinban_code", "sw.hinban_code"),
        BreakdownDimension.ChohyoKubun =>
            ("sw.chohyo_kubun_name", "sw.chohyo_kubun_name", "sw.chohyo_kubun_name"),
        BreakdownDimension.Tanawari1 =>
            ("COALESCE(sw.tanawari1, '')",
             "COALESCE(sw.tanawari1, '')",
             "COALESCE(sw.tanawari1, '')"),
        BreakdownDimension.Tanawari2 =>
            ("COALESCE(sw.tanawari2, '')",
             "COALESCE(sw.tanawari2, '')",
             "COALESCE(sw.tanawari2, '')"),
        BreakdownDimension.ShohinKigo =>
            ("sw.shohin_kigou", "sw.shohin_kigou", "sw.shohin_kigou"),
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

    // 売上金額ベースの構成比率（%）。分母0は0を返す。
    private static double SharePercentByAmount(long amount, long totalAmount)
        => totalAmount == 0 ? 0.0 : (double)amount / totalAmount * 100.0;

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
        long CumulativeSales,
        long CumulativeDelivery,
        double AverageStockDays,
        Guid? MasterProductId,
        string? ProductName,
        string? Brand,
        string? PrimaryImageUrl,
        int TotalCount);

    private sealed record CrosstabRawRow(
        string Key,
        string Label,
        string? Hinban,
        string? Tanpin,
        string? ShohinKigou,
        string? Color,
        string? Size,
        string? Kisetsu,
        Guid? MasterProductId,
        string? ProductName,
        string? Brand,
        string? PrimaryImageUrl,
        long Quantity,
        long Amount,
        long GrossProfit,
        double StockDays,
        long Stock,
        long CumulativeSales,
        long CumulativeDelivery,
        long TotalAmount);
}
