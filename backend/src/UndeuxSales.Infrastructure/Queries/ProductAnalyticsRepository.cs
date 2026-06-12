using Dapper;
using Npgsql;
using UndeuxSales.Core;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>
/// 商品（商品マスタの product_id）を軸にした包括的な売上分析を提供する。
/// 商品の自然キー（業態・商品記号・品番）と期間/部門/季節等の任意フィルタの AND で sales_weekly を絞り込む。
/// 取引先（customer_code）は本アプリでは常に同じ値（メーカー固有コード）のためフィルタ・集計軸として
/// 提供しない。
/// </summary>
public sealed class ProductAnalyticsRepository
{
    // 商品軸クエリは LATERAL JOIN + window関数 + 大量集計を含むため、
    // m_product_sku / sales_weekly が大きくなった環境でも 30 秒のデフォルトで
    // 打ち切られないよう、参照クエリは明示的に余裕のあるタイムアウトを与える。
    private const int QueryCommandTimeoutSeconds = 120;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ProductMasterRepository _masterRepository;

    public ProductAnalyticsRepository(
        IDbConnectionFactory connectionFactory,
        ProductMasterRepository masterRepository)
    {
        _connectionFactory = connectionFactory;
        _masterRepository = masterRepository;
        DapperConfiguration.Initialize();
    }

    /// <summary>
    /// 指定の商品（product_id）について、期間内 KPI・週次トレンド・SKU 別売上・業態別売上を返す。
    /// 商品が存在しない場合は null。
    /// </summary>
    public async Task<ProductAnalyticsResponse?> GetAnalyticsAsync(
        Guid productId,
        SalesQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();

        var detail = await _masterRepository.GetProductDetailAsync(productId, cancellationToken);
        if (detail is null)
        {
            return null;
        }

        var summary = detail.Summary;

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);
        parameters.Add("businessCategoryCd", summary.BusinessCategoryCd);
        parameters.Add("productSign", summary.ProductSign);
        parameters.Add("productTypeCrd", summary.ProductTypeCrd);
        parameters.Add("productId", productId);

        var andClause = SalesFilterSql.AndClause(filter, "sw");

        // 商品の業務キー（業態・商品記号・品番）で sales_weekly を絞り込む共通 WHERE。
        var baseWhere = $"""
            sw.gyotai_code  = @businessCategoryCd
            AND sw.shohin_kigou = @productSign
            AND sw.hinban_code  = @productTypeCrd
            {andClause}
            """;

        var latestWeek = await QueryLatestWeekAsync(connection, baseWhere, parameters, cancellationToken);
        if (latestWeek.HasValue)
        {
            parameters.Add("latestWeek", latestWeek.Value);
        }

        var weeklyTrend = await QueryWeeklyTrendAsync(connection, baseWhere, parameters, cancellationToken);

        var kpi = await QueryKpiAsync(connection, baseWhere, latestWeek, parameters, cancellationToken);

        var bySku = await QueryBySkuAsync(
            connection, baseWhere, productId, latestWeek, parameters, cancellationToken);

        var byBusinessType = await QueryByBusinessTypeAsync(
            connection, summary, filter, cancellationToken);

        return new ProductAnalyticsResponse(
            summary,
            kpi,
            weeklyTrend,
            bySku,
            byBusinessType);
    }

    private static async Task<DateOnly?> QueryLatestWeekAsync(
        NpgsqlConnection connection,
        string baseWhere,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT MAX(sw.import_date)
            FROM sales_weekly sw
            WHERE {baseWhere};
            """;

        return await connection.ExecuteScalarAsync<DateOnly?>(
            new CommandDefinition(sql, parameters, commandTimeout: QueryCommandTimeoutSeconds, cancellationToken: cancellationToken));
    }

    private static async Task<IReadOnlyList<TrendPoint>> QueryWeeklyTrendAsync(
        NpgsqlConnection connection,
        string baseWhere,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT sw.import_date AS date,
                   COALESCE(SUM({SalesMetricSql.WeekQuantity}), 0)::bigint    AS quantity,
                   COALESCE(SUM({SalesMetricSql.WeekAmount}), 0)::bigint      AS amount,
                   COALESCE(SUM({SalesMetricSql.WeekGrossProfit}), 0)::bigint AS gross_profit
            FROM sales_weekly sw
            WHERE {baseWhere}
            GROUP BY sw.import_date
            ORDER BY sw.import_date;
            """;

        var rows = await connection.QueryAsync<TrendPoint>(
            new CommandDefinition(sql, parameters, commandTimeout: QueryCommandTimeoutSeconds, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private static async Task<ProductAnalyticsKpi> QueryKpiAsync(
        NpgsqlConnection connection,
        string baseWhere,
        DateOnly? latestWeek,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        var flowSql = $"""
            SELECT COALESCE(SUM({SalesMetricSql.WeekQuantity}), 0)::bigint    AS quantity,
                   COALESCE(SUM({SalesMetricSql.WeekAmount}), 0)::bigint      AS amount,
                   COALESCE(SUM({SalesMetricSql.WeekGrossProfit}), 0)::bigint AS gross_profit,
                   COALESCE(AVG(sw.zainiti), 0)::float8                       AS average_stock_days
            FROM sales_weekly sw
            WHERE {baseWhere};
            """;
        var flow = await connection.QuerySingleAsync<FlowKpiRow>(
            new CommandDefinition(flowSql, parameters, commandTimeout: QueryCommandTimeoutSeconds, cancellationToken: cancellationToken));

        if (!latestWeek.HasValue)
        {
            return new ProductAnalyticsKpi(
                flow.Quantity,
                flow.Amount,
                flow.GrossProfit,
                AggregateMath.Ratio(flow.GrossProfit, flow.Amount),
                0,
                0.0,
                flow.AverageStockDays,
                null);
        }

        var snapshotSql = $"""
            SELECT COALESCE(SUM(sw.zaikosu), 0)::bigint             AS stock,
                   COALESCE(SUM(sw.ruikei_uriage_count), 0)::bigint AS cumulative_sales,
                   COALESCE(SUM(sw.ruikei_nohin_count), 0)::bigint  AS cumulative_delivery
            FROM sales_weekly sw
            WHERE sw.import_date = @latestWeek AND {baseWhere};
            """;

        var snapshot = await connection.QuerySingleAsync<SnapshotKpiRow>(
            new CommandDefinition(snapshotSql, parameters, commandTimeout: QueryCommandTimeoutSeconds, cancellationToken: cancellationToken));

        return new ProductAnalyticsKpi(
            flow.Quantity,
            flow.Amount,
            flow.GrossProfit,
            AggregateMath.Ratio(flow.GrossProfit, flow.Amount),
            snapshot.Stock,
            AggregateMath.Ratio(snapshot.CumulativeSales, snapshot.CumulativeDelivery),
            flow.AverageStockDays,
            latestWeek);
    }

    private static async Task<IReadOnlyList<ProductSkuPerformance>> QueryBySkuAsync(
        NpgsqlConnection connection,
        string baseWhere,
        Guid productId,
        DateOnly? latestWeek,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        // SKU 集計は tanpin_code で集約する。商品マスタの SKU 情報（色・サイズ・売価・代表画像）を
        // unit_cd 経由で結合し、画像は image_index 最小の 1 枚を採用。
        // 在庫は商品自然キー（業態×記号×品番）のみで集計し、ユーザーフィルタ
        // （部門・季節）には引きずられない物理在庫を反映する。
        // latestWeek が無ければ 0（売上のない期間ではスナップショットも不在）。
        var stockCte = latestWeek.HasValue
            ? """
              stock AS (
                  SELECT sw.tanpin_code,
                         COALESCE(SUM(sw.zaikosu), 0)::bigint AS stock
                  FROM sales_weekly sw
                  WHERE sw.import_date = @latestWeek
                    AND sw.gyotai_code  = @businessCategoryCd
                    AND sw.shohin_kigou = @productSign
                    AND sw.hinban_code  = @productTypeCrd
                  GROUP BY sw.tanpin_code
              )
              """
            : "stock AS (SELECT NULL::text AS tanpin_code, 0::bigint AS stock WHERE false)";

        var sql = $"""
            WITH sales AS (
                SELECT sw.tanpin_code,
                       COALESCE(SUM({SalesMetricSql.WeekQuantity}), 0)::bigint    AS quantity,
                       COALESCE(SUM({SalesMetricSql.WeekAmount}), 0)::bigint      AS amount,
                       COALESCE(SUM({SalesMetricSql.WeekGrossProfit}), 0)::bigint AS gross_profit
                FROM sales_weekly sw
                WHERE {baseWhere}
                GROUP BY sw.tanpin_code
            ),
            sku_meta AS (
                SELECT unit_cd,
                       MIN(color_name)  AS color_name,
                       MIN(size_name)   AS size_name,
                       MIN(sales_price) AS sales_price
                FROM m_product_sku
                WHERE product_id = @productId
                GROUP BY unit_cd
            ),
            {stockCte}
            SELECT COALESCE(sa.tanpin_code, sm.unit_cd) AS unit_cd,
                   COALESCE(sm.color_name, '')          AS color_name,
                   COALESCE(sm.size_name, '')           AS size_name,
                   COALESCE(sm.sales_price, 0)::int     AS sales_price,
                   img.image_url                        AS primary_image_url,
                   COALESCE(sa.quantity, 0)             AS quantity,
                   COALESCE(sa.amount, 0)               AS amount,
                   COALESCE(sa.gross_profit, 0)         AS gross_profit,
                   COALESCE(st.stock, 0)                AS stock,
                   (SUM(COALESCE(sa.amount, 0)) OVER ())::bigint AS total_amount
            FROM sales sa
            FULL OUTER JOIN sku_meta sm ON sm.unit_cd = sa.tanpin_code
            LEFT JOIN stock st ON st.tanpin_code = COALESCE(sa.tanpin_code, sm.unit_cd)
            LEFT JOIN LATERAL (
                SELECT image_url
                FROM m_product_sku
                WHERE product_id = @productId
                  AND unit_cd    = COALESCE(sa.tanpin_code, sm.unit_cd)
                ORDER BY image_index, sku_item_id
                LIMIT 1
            ) AS img ON true
            ORDER BY amount DESC, unit_cd;
            """;

        var rows = (await connection.QueryAsync<SkuPerformanceRow>(
            new CommandDefinition(sql, parameters, commandTimeout: QueryCommandTimeoutSeconds, cancellationToken: cancellationToken))).ToList();

        return rows
            .Select(r => new ProductSkuPerformance(
                r.UnitCd,
                r.ColorName,
                r.SizeName,
                r.SalesPrice,
                r.PrimaryImageUrl,
                r.Quantity,
                r.Amount,
                r.GrossProfit,
                r.Stock,
                SharePercent(r.Amount, r.TotalAmount)))
            .ToList();
    }

    /// <summary>
    /// 同一の商品記号・品番（業態のみ異なる）を別業態で販売しているケースの売上比較。
    /// 業態間の比較が目的のため、ユーザーの BusinessTypes フィルタは意図的に除外する
    /// （指定された業態のみに絞り込んでしまうと比較が成立しないため）。
    /// 期間／部門／季節／品番のフィルタは引き続き適用する。
    /// </summary>
    private async Task<IReadOnlyList<ProductBusinessTypePerformance>> QueryByBusinessTypeAsync(
        NpgsqlConnection connection,
        MasterProductSummary summary,
        SalesQueryFilter filter,
        CancellationToken cancellationToken)
    {
        // BusinessTypes だけ除外したフィルタを作る。元の filter は変更しない。
        var crossBusinessFilter = new SalesQueryFilter
        {
            From = filter.From,
            To = filter.To,
            Departments = filter.Departments,
            BusinessTypes = null,
            Seasons = filter.Seasons,
            Hinbans = filter.Hinbans,
            ShohinKigos = filter.ShohinKigos,
        };

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(crossBusinessFilter, parameters);
        parameters.Add("productSign", summary.ProductSign);
        parameters.Add("productTypeCrd", summary.ProductTypeCrd);

        var andClause = SalesFilterSql.AndClause(crossBusinessFilter, "sw");
        var sql = $"""
            WITH sales AS (
                SELECT sw.gyotai_code,
                       COALESCE(SUM({SalesMetricSql.WeekQuantity}), 0)::bigint    AS quantity,
                       COALESCE(SUM({SalesMetricSql.WeekAmount}), 0)::bigint      AS amount,
                       COALESCE(SUM({SalesMetricSql.WeekGrossProfit}), 0)::bigint AS gross_profit
                FROM sales_weekly sw
                WHERE sw.shohin_kigou = @productSign
                  AND sw.hinban_code  = @productTypeCrd
                  {andClause}
                GROUP BY sw.gyotai_code
            )
            SELECT sa.gyotai_code AS business_category_cd,
                   bt.display_name AS display_name,
                   bt.short_name   AS short_name,
                   sa.quantity,
                   sa.amount,
                   sa.gross_profit,
                   (SUM(sa.amount) OVER ())::bigint AS total_amount
            FROM sales sa
            LEFT JOIN business_type bt ON bt.code = sa.gyotai_code
            ORDER BY sa.amount DESC, sa.gyotai_code;
            """;

        var rows = (await connection.QueryAsync<BusinessTypePerformanceRow>(
            new CommandDefinition(sql, parameters, commandTimeout: QueryCommandTimeoutSeconds, cancellationToken: cancellationToken))).ToList();

        return rows
            .Select(r => new ProductBusinessTypePerformance(
                r.BusinessCategoryCd,
                r.DisplayName,
                r.ShortName,
                r.Quantity,
                r.Amount,
                r.GrossProfit,
                SharePercent(r.Amount, r.TotalAmount)))
            .ToList();
    }

    private static double SharePercent(long amount, long totalAmount)
        => totalAmount == 0 ? 0.0 : (double)amount / totalAmount * 100.0;

    private sealed record FlowKpiRow(
        long Quantity, long Amount, long GrossProfit, double AverageStockDays);

    private sealed record SnapshotKpiRow(
        long Stock, long CumulativeSales, long CumulativeDelivery);

    private sealed record SkuPerformanceRow(
        string UnitCd,
        string ColorName,
        string SizeName,
        int SalesPrice,
        string? PrimaryImageUrl,
        long Quantity,
        long Amount,
        long GrossProfit,
        long Stock,
        long TotalAmount);

    private sealed record BusinessTypePerformanceRow(
        string BusinessCategoryCd,
        string? DisplayName,
        string? ShortName,
        long Quantity,
        long Amount,
        long GrossProfit,
        long TotalAmount);
}
