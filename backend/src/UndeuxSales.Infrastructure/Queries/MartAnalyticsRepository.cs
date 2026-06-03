using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using UndeuxSales.Core;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>
/// 分析 mart（スタースキーマ）に対する集計クエリと再構築を提供するリポジトリ。
/// 集計の SoT は <c>sales_weekly</c>。mart はその派生（キャッシュ）であり、
/// <see cref="RebuildAsync"/> で <c>mart.rebuild()</c> を呼んで全再構築する。
/// </summary>
public sealed class MartAnalyticsRepository
{
    private const int MaxBreakdownLimit = 1000;

    /// <summary>全再構築は 160 万行規模を集約するため十分なタイムアウトを与える。</summary>
    private const int RebuildCommandTimeoutSeconds = 600;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<MartAnalyticsRepository> _logger;

    public MartAnalyticsRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<MartAnalyticsRepository>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger ?? NullLogger<MartAnalyticsRepository>.Instance;
        DapperConfiguration.Initialize();
    }

    /// <summary>
    /// 再構築の実行権を原子的に取得する。idle / completed / failed のとき、または 30 分以上
    /// 滞留した running（コンテナ再起動等で取り残された状態）のときに限り running 化する。
    /// 取得できたら true、既に実行中なら false を返す。
    /// </summary>
    public async Task<bool> TryStartRebuildAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE mart.build_info
            SET status = 'running', started_at = now(), error = NULL
            WHERE id = 1
              AND (status <> 'running'
                   OR started_at IS NULL
                   OR started_at < now() - interval '30 minutes');
            """, cancellationToken: cancellationToken));
        return rows > 0;
    }

    /// <summary>
    /// mart 全再構築の本体（バックグラウンドで実行）。<c>mart.rebuild()</c> を呼び、
    /// 成否を <c>build_info.status</c> に completed / failed として記録する。
    /// 事前に <see cref="TryStartRebuildAsync"/> で running 化されている前提。
    /// </summary>
    public async Task RunRebuildCoreAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT mart.rebuild();",
                commandTimeout: RebuildCommandTimeoutSeconds,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE mart.build_info SET status = 'completed', error = NULL WHERE id = 1;",
                cancellationToken: cancellationToken));
            stopwatch.Stop();
            _logger.LogInformation("mart を再構築しました（{ElapsedMs} ms）。", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "mart の再構築が失敗しました（{ElapsedMs} ms）。", stopwatch.ElapsedMilliseconds);
            // 失敗状態は別接続で記録する（実行中の接続が壊れている可能性に備える）。
            try
            {
                await using var failConnection =
                    await _connectionFactory.OpenConnectionAsync(CancellationToken.None);
                await failConnection.ExecuteAsync(new CommandDefinition(
                    "UPDATE mart.build_info SET status = 'failed', error = @error WHERE id = 1;",
                    new { error = Truncate(ex.Message, 1000) }));
            }
            catch (Exception recordEx)
            {
                _logger.LogError(recordEx, "mart の失敗状態の記録に失敗しました。");
            }
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>mart の構築状態（最終再構築時刻・行数・対象週範囲）を取得する。</summary>
    public async Task<MartStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await ReadStatusAsync(connection, cancellationToken);
    }

    /// <summary>全社サマリー（KPI＋週次トレンド）を mart から取得する。</summary>
    public async Task<MartSummaryResponse> GetSummaryAsync(
        SalesQueryFilter filter, CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        MartFilterSql.AddParameters(filter, parameters);

        // フロー指標はファクトに事前計算済みのため SUM するだけ（設計の狙い）。
        var trendSql = $"""
            SELECT dd.week_monday AS date,
                   COALESCE(SUM(f.quantity), 0)::bigint     AS quantity,
                   COALESCE(SUM(f.amount), 0)::bigint       AS amount,
                   COALESCE(SUM(f.gross_profit), 0)::bigint AS gross_profit
            FROM mart.fact_sales_weekly f
            JOIN mart.dim_date     dd ON dd.date_key     = f.date_key
            JOIN mart.dim_product  dp ON dp.product_key  = f.product_key
            JOIN mart.dim_retailer dr ON dr.retailer_key = f.retailer_key
            {MartFilterSql.WhereClause(filter)}
            GROUP BY dd.week_monday
            ORDER BY dd.week_monday;
            """;
        var weeklyTrend = (await connection.QueryAsync<TrendPoint>(
            new CommandDefinition(trendSql, parameters, cancellationToken: cancellationToken))).ToList();

        // 商品数・SKU数はサロゲートキーの DISTINCT で算出（整数のため高速）。
        var countSql = $"""
            SELECT COUNT(DISTINCT f.product_key)::int AS product_count,
                   COUNT(DISTINCT f.sku_key)::int     AS sku_count
            FROM mart.fact_sales_weekly f
            JOIN mart.dim_date     dd ON dd.date_key     = f.date_key
            JOIN mart.dim_product  dp ON dp.product_key  = f.product_key
            JOIN mart.dim_retailer dr ON dr.retailer_key = f.retailer_key
            {MartFilterSql.WhereClause(filter)};
            """;
        var counts = await connection.QuerySingleAsync<CountRow>(
            new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));

        var quantity = weeklyTrend.Sum(point => point.Quantity);
        var amount = weeklyTrend.Sum(point => point.Amount);
        var grossProfit = weeklyTrend.Sum(point => point.GrossProfit);
        var latestWeek = weeklyTrend.Count > 0 ? weeklyTrend[^1].Date : (DateOnly?)null;

        var kpi = new MartKpi(
            quantity, amount, grossProfit, Ratio(grossProfit, amount),
            counts.ProductCount, counts.SkuCount, latestWeek);

        return new MartSummaryResponse(kpi, weeklyTrend);
    }

    /// <summary>集計軸（部門・業態・季節・品番・ブランド）別の売上ランキングを mart から取得する。</summary>
    public async Task<MartBreakdownResponse> GetBreakdownAsync(
        SalesQueryFilter filter,
        string? dimension,
        SalesMetric metric,
        bool ascending,
        int limit,
        CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        limit = Math.Clamp(limit, 1, MaxBreakdownLimit);

        var (keyExpr, labelExpr, name) = ResolveMartDimension(dimension);
        var metricColumn = metric switch
        {
            SalesMetric.Quantity => "quantity",
            SalesMetric.GrossProfit => "gross_profit",
            _ => "amount",
        };
        var direction = ascending ? "ASC" : "DESC";

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        MartFilterSql.AddParameters(filter, parameters);
        parameters.Add("limit", limit);

        // ラベルはキー単位で一意なため MIN で集約し、GROUP BY はキー式のみとする。
        var sql = $"""
            SELECT key, label, quantity, amount, gross_profit,
                   (SUM(quantity) OVER ())::bigint     AS total_quantity,
                   (SUM(amount) OVER ())::bigint       AS total_amount,
                   (SUM(gross_profit) OVER ())::bigint AS total_gross_profit
            FROM (
                SELECT {keyExpr} AS key,
                       MIN({labelExpr}) AS label,
                       COALESCE(SUM(f.quantity), 0)::bigint     AS quantity,
                       COALESCE(SUM(f.amount), 0)::bigint       AS amount,
                       COALESCE(SUM(f.gross_profit), 0)::bigint AS gross_profit
                FROM mart.fact_sales_weekly f
                JOIN mart.dim_date     dd ON dd.date_key     = f.date_key
                JOIN mart.dim_product  dp ON dp.product_key  = f.product_key
                JOIN mart.dim_retailer dr ON dr.retailer_key = f.retailer_key
                {MartFilterSql.WhereClause(filter)}
                GROUP BY {keyExpr}
            ) g
            ORDER BY {metricColumn} {direction}, key
            LIMIT @limit;
            """;

        var rawRows = (await connection.QueryAsync<MartBreakdownRawRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        var rows = rawRows
            .Select(row => new BreakdownRow(
                row.Key, row.Label, row.Quantity, row.Amount, row.GrossProfit,
                SharePercentOf(row, metric)))
            .ToList();

        return new MartBreakdownResponse(name, rows);
    }

    private static async Task<MartStatus> ReadStatusAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var info = await connection.QuerySingleOrDefaultAsync<BuildInfoRow>(new CommandDefinition("""
            SELECT bi.status, bi.error, bi.started_at, bi.rebuilt_at, bi.source_rows, bi.fact_rows,
                   (SELECT MIN(week_monday) FROM mart.dim_date) AS earliest_week,
                   (SELECT MAX(week_monday) FROM mart.dim_date) AS latest_week
            FROM mart.build_info bi
            WHERE bi.id = 1;
            """, cancellationToken: cancellationToken));

        if (info is null)
        {
            return new MartStatus(false, "idle", null, null, null, 0, 0, null, null);
        }

        return new MartStatus(
            info.FactRows > 0, info.Status, info.Error, info.StartedAt, info.RebuiltAt,
            info.SourceRows, info.FactRows, info.EarliestWeek, info.LatestWeek);
    }

    /// <summary>集計軸の文字列を mart 次元のSQL式（キー・ラベル・正規名）に解決する。ホワイトリスト照合。</summary>
    private static (string KeyExpr, string LabelExpr, string Name) ResolveMartDimension(string? dimension)
    {
        var key = (dimension ?? "department").Trim().ToLowerInvariant();
        return key switch
        {
            "department" => (
                "COALESCE(NULLIF(dp.department_code, ''), '(未設定)')",
                "COALESCE(NULLIF(dp.department_name, ''), NULLIF(dp.department_code, ''), '(未設定)')",
                "Department"),
            "businesstype" => (
                "COALESCE(NULLIF(dr.channel_code, ''), '(未設定)')",
                "COALESCE(NULLIF(dr.channel_name, ''), NULLIF(dr.channel_code, ''), '(未設定)')",
                "BusinessType"),
            "season" => (
                "COALESCE(NULLIF(dp.season, ''), '(未設定)')",
                "COALESCE(NULLIF(dp.season, ''), '(未設定)')",
                "Season"),
            "product" => (
                "COALESCE(NULLIF(dp.product_code, ''), '(未設定)')",
                "COALESCE(NULLIF(dp.product_name, ''), NULLIF(dp.product_code, ''), '(未設定)')",
                "Product"),
            "brand" => (
                "COALESCE(NULLIF(dp.brand, ''), '(未設定)')",
                "COALESCE(NULLIF(dp.brand, ''), '(未設定)')",
                "Brand"),
            _ => throw new AppException(ErrorCodes.UnknownDimension, 400,
                $"集計軸 '{dimension}' は不正です（department / businessType / season / product / brand）。"),
        };
    }

    private static double SharePercentOf(MartBreakdownRawRow row, SalesMetric metric)
    {
        var (value, total) = metric switch
        {
            SalesMetric.Quantity => (row.Quantity, row.TotalQuantity),
            SalesMetric.GrossProfit => (row.GrossProfit, row.TotalGrossProfit),
            _ => (row.Amount, row.TotalAmount),
        };
        return total == 0 ? 0 : (double)value / total * 100.0;
    }

    /// <summary>分子÷分母×100（分母0は0）。粗利率・消化率の共通式。</summary>
    private static double Ratio(long numerator, long denominator)
        => denominator == 0 ? 0 : (double)numerator / denominator * 100.0;
}

/// <summary><see cref="SalesQueryFilter"/> から mart 次元に対する WHERE 条件を組み立てる。</summary>
/// <remarks>
/// 在庫日数バケット・棚割1は本イテレーションの mart では未対応のため無視する
/// （グレースフルデグラデーション。後続で在庫スナップショット導入時に追加）。
/// </remarks>
internal static class MartFilterSql
{
    public static void AddParameters(SalesQueryFilter filter, DynamicParameters parameters)
    {
        if (filter.From.HasValue) parameters.Add("from", filter.From.Value);
        if (filter.To.HasValue) parameters.Add("to", filter.To.Value);
        if (filter.Departments is { Length: > 0 }) parameters.Add("departments", filter.Departments);
        if (filter.BusinessTypes is { Length: > 0 }) parameters.Add("businessTypes", filter.BusinessTypes);
        if (filter.Seasons is { Length: > 0 }) parameters.Add("seasons", filter.Seasons);
        if (filter.Hinbans is { Length: > 0 }) parameters.Add("hinbans", filter.Hinbans);
    }

    public static string WhereClause(SalesQueryFilter filter)
    {
        var conditions = new List<string>();

        if (filter.From.HasValue) conditions.Add("dd.week_monday >= @from");
        if (filter.To.HasValue) conditions.Add("dd.week_monday <= @to");
        if (filter.Departments is { Length: > 0 }) conditions.Add("dp.department_code = ANY(@departments)");
        if (filter.BusinessTypes is { Length: > 0 }) conditions.Add("dr.channel_code = ANY(@businessTypes)");
        if (filter.Seasons is { Length: > 0 }) conditions.Add("dp.season = ANY(@seasons)");
        if (filter.Hinbans is { Length: > 0 }) conditions.Add("dp.product_code = ANY(@hinbans)");

        return conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
    }
}

/// <summary>Dapper マッピング用の内部行（breakdown の集計＋全体合計）。</summary>
internal sealed record MartBreakdownRawRow(
    string Key, string Label, long Quantity, long Amount, long GrossProfit,
    long TotalQuantity, long TotalAmount, long TotalGrossProfit);

/// <summary>Dapper マッピング用の内部行（商品数・SKU数）。</summary>
internal sealed record CountRow(int ProductCount, int SkuCount);

/// <summary>Dapper マッピング用の内部行（mart.build_info）。</summary>
internal sealed record BuildInfoRow(
    string Status, string? Error, DateTime? StartedAt,
    DateTime? RebuiltAt, long SourceRows, long FactRows,
    DateOnly? EarliestWeek, DateOnly? LatestWeek);
