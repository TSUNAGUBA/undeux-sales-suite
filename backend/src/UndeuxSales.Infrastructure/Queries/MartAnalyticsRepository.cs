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

    /// <summary>
    /// 全再構築のコマンドタイムアウト（秒）。<c>0 = 無制限</c>。
    /// 再構築は HTTP リクエストから切り離したバックグラウンドタスクで実行され（即時応答＋status ポーリング）、
    /// nginx 等のリバースプロキシのタイムアウトとは無関係。処理は有限テーブルに対する有界な集約で必ず終了し、
    /// advisory lock で直列化・<c>build_info.status</c> で状態管理されるため、クライアント側で人工的な
    /// コマンドタイムアウト上限を設ける意味がない。よって 0（無制限）とし、データ量に依らずタイムアウトしない。
    /// （Npgsql は CommandTimeout=0 を無期限として扱う。）
    /// </summary>
    private const int RebuildCommandTimeoutSeconds = 0;

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
    /// 再構築の実行権を原子的に取得する。idle / completed / failed のとき、または 45 分以上
    /// 滞留した running（コンテナ再起動等で取り残された状態）のときに限り running 化する。
    /// しきい値はコマンドタイムアウト（30 分）より大きく取り、正常に長時間実行中の再構築を
    /// stale と誤認しないようにする。取得できたら true、既に実行中なら false を返す。
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
                   OR started_at < now() - interval '45 minutes');
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

        var weeklyTrend = await QueryWeeklyTrendAsync(connection, filter, parameters, cancellationToken);

        // 商品数・SKU数はサロゲートキーの DISTINCT で算出（整数のため高速）。
        var countSql = $"""
            SELECT COUNT(DISTINCT f.product_key)::int AS product_count,
                   COUNT(DISTINCT f.sku_key)::int     AS sku_count
            FROM mart.fact_sales_weekly f
            JOIN mart.dim_date     dd ON dd.date_key     = f.date_key
            JOIN mart.dim_product  dp ON dp.product_key  = f.product_key
            JOIN mart.dim_retailer dr ON dr.retailer_key = f.retailer_key
            {MartFilterSql.WhereClause(filter, "f")};
            """;
        var counts = await connection.QuerySingleAsync<CountRow>(
            new CommandDefinition(countSql, parameters, cancellationToken: cancellationToken));

        var quantity = weeklyTrend.Sum(point => point.Quantity);
        var amount = weeklyTrend.Sum(point => point.Amount);
        var grossProfit = weeklyTrend.Sum(point => point.GrossProfit);
        var latestWeek = weeklyTrend.Count > 0 ? weeklyTrend[^1].Date : (DateOnly?)null;

        // 在庫KPI（最新週スナップショット）。fact_inventory_snapshot から在庫数・消化率を集計する。
        long currentStock = 0;
        double sellThroughRate = 0;
        if (latestWeek.HasValue)
        {
            parameters.Add("latestWeek", latestWeek.Value);
            var snapshot = await connection.QuerySingleAsync<MartInvSnapshotRow>(new CommandDefinition($"""
                SELECT COALESCE(SUM(inv.stock), 0)::bigint        AS stock,
                       COALESCE(SUM(inv.cum_sales), 0)::bigint     AS cumulative_sales,
                       COALESCE(SUM(inv.cum_delivery), 0)::bigint  AS cumulative_delivery
                FROM mart.fact_inventory_snapshot inv
                JOIN mart.dim_date     dd ON dd.date_key     = inv.date_key
                JOIN mart.dim_product  dp ON dp.product_key  = inv.product_key
                JOIN mart.dim_retailer dr ON dr.retailer_key = inv.retailer_key
                WHERE dd.week_monday = @latestWeek{MartFilterSql.AndClause(filter, "inv")};
                """, parameters, cancellationToken: cancellationToken));
            currentStock = snapshot.Stock;
            sellThroughRate = AggregateMath.Ratio(snapshot.CumulativeSales, snapshot.CumulativeDelivery);
        }

        var kpi = new MartKpi(
            quantity, amount, grossProfit, AggregateMath.Ratio(grossProfit, amount),
            counts.ProductCount, counts.SkuCount, currentStock, sellThroughRate, latestWeek);

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
                {MartFilterSql.WhereClause(filter, "f")}
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

    /// <summary>ランキングで返却する最大行数（フロントは Top-N でさらに絞る）。</summary>
    private const int MaxRankingRows = 1000;

    /// <summary>商品一覧の最大ページサイズ。</summary>
    private const int MaxPageSize = 200;

    /// <summary>消化率×値引き率 散布図の最大点数。</summary>
    private const int MaxMarkdownPoints = 500;

    /// <summary>
    /// 実気温（dim_climate）を採用する完全週の日数（月〜日）。これ未満の週は CSV 未カバーとみなし
    /// 標準気候（<see cref="ClimateModel"/>）へフォールバックする。
    /// </summary>
    private const int FullWeekDays = 7;

    /// <summary>在庫・発注分析（最新週スナップショット基準）を mart から取得する。</summary>
    public async Task<InventoryResponse> GetInventoryAsync(
        SalesQueryFilter filter, CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        MartFilterSql.AddParameters(filter, parameters);

        var latestWeek = await QueryLatestWeekAsync(connection, filter, parameters, cancellationToken);
        if (!latestWeek.HasValue)
        {
            return new InventoryResponse(
                new InventoryKpi(0, 0m, 0, 0, 0, 0, 0, null),
                Array.Empty<InventoryBreakdownRow>());
        }

        parameters.Add("latestWeek", latestWeek.Value);
        var andClause = MartFilterSql.AndClause(filter, "inv");

        var kpiSql = $"""
            SELECT COALESCE(SUM(inv.stock), 0)::bigint        AS total_stock,
                   COALESCE(SUM(inv.order_qty), 0)            AS total_order_quantity,
                   COALESCE(SUM(inv.advance_qty), 0)::bigint  AS total_advance_quantity,
                   COALESCE(SUM(inv.cum_sales), 0)::bigint    AS cumulative_sales,
                   COALESCE(SUM(inv.cum_delivery), 0)::bigint AS cumulative_delivery,
                   COALESCE(AVG(inv.stock_days), 0)::float8   AS average_stock_days
            FROM mart.fact_inventory_snapshot inv
            JOIN mart.dim_date     dd ON dd.date_key     = inv.date_key
            JOIN mart.dim_product  dp ON dp.product_key  = inv.product_key
            JOIN mart.dim_retailer dr ON dr.retailer_key = inv.retailer_key
            WHERE dd.week_monday = @latestWeek{andClause};
            """;
        var kpiRow = await connection.QuerySingleAsync<MartInventoryKpiRow>(
            new CommandDefinition(kpiSql, parameters, cancellationToken: cancellationToken));

        // 部門別内訳（部門名は dim_product から。コードのみの場合はコードを表示）。
        var breakdownSql = $"""
            SELECT COALESCE(NULLIF(dp.department_code, ''), '(未設定)') AS key,
                   COALESCE(NULLIF(dp.department_name, ''),
                            NULLIF(dp.department_code, ''), '(未設定)') AS label,
                   COALESCE(SUM(inv.stock), 0)::bigint        AS stock,
                   COALESCE(SUM(inv.order_qty), 0)            AS order_quantity,
                   COALESCE(SUM(inv.advance_qty), 0)::bigint  AS advance_quantity,
                   COALESCE(SUM(inv.cum_sales), 0)::bigint    AS cumulative_sales,
                   COALESCE(SUM(inv.cum_delivery), 0)::bigint AS cumulative_delivery
            FROM mart.fact_inventory_snapshot inv
            JOIN mart.dim_date     dd ON dd.date_key     = inv.date_key
            JOIN mart.dim_product  dp ON dp.product_key  = inv.product_key
            JOIN mart.dim_retailer dr ON dr.retailer_key = inv.retailer_key
            WHERE dd.week_monday = @latestWeek{andClause}
            GROUP BY COALESCE(NULLIF(dp.department_code, ''), '(未設定)'),
                     COALESCE(NULLIF(dp.department_name, ''),
                              NULLIF(dp.department_code, ''), '(未設定)')
            ORDER BY stock DESC, key;
            """;
        var breakdownRaw = await connection.QueryAsync<MartInventoryBreakdownRawRow>(
            new CommandDefinition(breakdownSql, parameters, cancellationToken: cancellationToken));

        var byDepartment = breakdownRaw
            .Select(row => new InventoryBreakdownRow(
                row.Key, row.Label, row.Stock, row.OrderQuantity, row.AdvanceQuantity,
                AggregateMath.Ratio(row.CumulativeSales, row.CumulativeDelivery)))
            .ToList();

        var kpi = new InventoryKpi(
            kpiRow.TotalStock, kpiRow.TotalOrderQuantity, kpiRow.TotalAdvanceQuantity,
            kpiRow.CumulativeSales, kpiRow.CumulativeDelivery,
            AggregateMath.Ratio(kpiRow.CumulativeSales, kpiRow.CumulativeDelivery),
            kpiRow.AverageStockDays, latestWeek);

        return new InventoryResponse(kpi, byDepartment);
    }

    /// <summary>商品別分析の一覧（ページング）を mart から取得する。</summary>
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
        MartFilterSql.AddParameters(filter, parameters);

        var latestWeek = await QueryLatestWeekAsync(connection, filter, parameters, cancellationToken);
        if (!latestWeek.HasValue)
        {
            return new ProductPage(Array.Empty<ProductRow>(), 0, page, pageSize);
        }

        parameters.Add("latestWeek", latestWeek.Value);
        parameters.Add("limit", pageSize);
        // page は外部入力のため long で乗算し、巨大値でも負の OFFSET（int オーバーフロー）にしない。
        parameters.Add("offset", (long)(page - 1) * pageSize);

        var sortExpression = MartProductSortExpression(sortKey);
        var direction = ascending ? "ASC" : "DESC";
        var whereClause = MartFilterSql.WhereClause(filter, "f");
        var andClause = MartFilterSql.AndClause(filter, "inv");

        // SKU（sku_key）単位の行。在庫は最新週スナップショット、フローは期間集計。
        // 代表画像・名称・ブランドは mart 次元から、商品マスタID（詳細遷移用）は自然キーで
        // 重複排除した m_product サブクエリから取得する（行増幅・一意制約の影響を受けない）。
        var sql = $"""
            WITH stock AS (
                SELECT inv.sku_key,
                       COALESCE(SUM(inv.stock), 0)::bigint        AS stock,
                       COALESCE(SUM(inv.cum_sales), 0)::bigint    AS cum_sales,
                       COALESCE(SUM(inv.cum_delivery), 0)::bigint AS cum_delivery,
                       COALESCE(AVG(inv.stock_days), 0)::float8   AS average_stock_days
                FROM mart.fact_inventory_snapshot inv
                JOIN mart.dim_date     dd ON dd.date_key     = inv.date_key
                JOIN mart.dim_product  dp ON dp.product_key  = inv.product_key
                JOIN mart.dim_retailer dr ON dr.retailer_key = inv.retailer_key
                WHERE dd.week_monday = @latestWeek{andClause}
                GROUP BY inv.sku_key
            ),
            flow AS (
                SELECT f.sku_key,
                       COALESCE(SUM(f.quantity), 0)::bigint     AS sales_quantity,
                       COALESCE(SUM(f.amount), 0)::bigint       AS sales_amount,
                       COALESCE(SUM(f.gross_profit), 0)::bigint AS gross_profit
                FROM mart.fact_sales_weekly f
                JOIN mart.dim_date     dd ON dd.date_key     = f.date_key
                JOIN mart.dim_product  dp ON dp.product_key  = f.product_key
                JOIN mart.dim_retailer dr ON dr.retailer_key = f.retailer_key
                {whereClause}
                GROUP BY f.sku_key
            )
            SELECT dp.channel_code AS gyotai_code,
                   dp.product_sign AS shohin_kigou,
                   dp.product_code AS hinban_code,
                   ds.unit_code    AS tanpin_code,
                   COALESCE(dp.product_name, '') AS hinmei,
                   COALESCE(dp.season, '')       AS kisetsu,
                   COALESCE(f.sales_quantity, 0) AS sales_quantity,
                   COALESCE(f.sales_amount, 0)   AS sales_amount,
                   COALESCE(f.gross_profit, 0)   AS gross_profit,
                   s.stock,
                   s.cum_sales      AS cumulative_sales,
                   s.cum_delivery   AS cumulative_delivery,
                   s.average_stock_days,
                   mp.product_id    AS master_product_id,
                   dp.product_name  AS product_name,
                   dp.brand         AS brand,
                   ds.image_url     AS primary_image_url,
                   (COUNT(*) OVER ())::int AS total_count
            FROM stock s
            JOIN mart.dim_sku     ds ON ds.sku_key     = s.sku_key
            JOIN mart.dim_product dp ON dp.product_key = ds.product_key
            LEFT JOIN flow f ON f.sku_key = s.sku_key
            LEFT JOIN (
                SELECT DISTINCT ON (business_category_cd, product_sign, product_type_crd)
                       business_category_cd, product_sign, product_type_crd, product_id
                FROM m_product
                ORDER BY business_category_cd, product_sign, product_type_crd, updated_at DESC, product_id
            ) mp ON mp.business_category_cd = dp.channel_code
                AND mp.product_sign         = dp.product_sign
                AND mp.product_type_crd     = dp.product_code
            ORDER BY {sortExpression} {direction} NULLS LAST, dp.product_code, ds.unit_code
            LIMIT @limit OFFSET @offset;
            """;

        var rawRows = (await connection.QueryAsync<MartProductRawRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        var totalCount = rawRows.Count > 0
            ? rawRows[0].TotalCount
            : await CountMartProductsAsync(connection, filter, parameters, cancellationToken);

        var items = rawRows
            .Select(row => new ProductRow(
                row.GyotaiCode, row.ShohinKigou, row.HinbanCode, row.TanpinCode,
                row.Hinmei, row.Kisetsu, row.SalesQuantity, row.SalesAmount, row.GrossProfit,
                row.Stock, AggregateMath.Ratio(row.CumulativeSales, row.CumulativeDelivery), row.AverageStockDays,
                row.MasterProductId, row.ProductName, row.Brand, row.PrimaryImageUrl))
            .ToList();

        return new ProductPage(items, totalCount, page, pageSize);
    }

    /// <summary>商品行（最新週に在庫がある SKU）の総件数を数える（本体クエリが空集合のフォールバック）。</summary>
    private static async Task<int> CountMartProductsAsync(
        NpgsqlConnection connection,
        SalesQueryFilter filter,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT COUNT(DISTINCT inv.sku_key)::int
            FROM mart.fact_inventory_snapshot inv
            JOIN mart.dim_date     dd ON dd.date_key     = inv.date_key
            JOIN mart.dim_product  dp ON dp.product_key  = inv.product_key
            JOIN mart.dim_retailer dr ON dr.retailer_key = inv.retailer_key
            WHERE dd.week_monday = @latestWeek{MartFilterSql.AndClause(filter, "inv")};
            """;
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    /// <summary>クロス集計マトリクス（行×列の2次元集計）を mart から取得する。</summary>
    /// <remarks>
    /// 帳票区分・棚割は mart の<b>集計軸（ディメンション）としては</b>保持しないため、それらを
    /// 行/列に指定すると 400（未対応）を返す（フロントは対応軸のみ提示する。棚割1は
    /// <see cref="MartFilterSql"/> のフィルタとしては対応済み）。
    /// フロー指標は期間内集計、在庫系は最新週スナップショット基準。
    /// </remarks>
    public async Task<CrosstabMatrixResponse> GetCrosstabMatrixAsync(
        SalesQueryFilter filter,
        CrosstabDimension rowDim,
        CrosstabDimension colDim,
        TemperatureArea? temperatureArea = null,
        CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        if (rowDim == colDim)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                "行と列に同じディメンションを指定することはできません。");
        }

        // 未対応ディメンション（帳票区分・棚割）はここで弾く（fail-fast）。
        var rowExpr = ResolveMartCrosstabDimensionSql(rowDim);
        var colExpr = ResolveMartCrosstabDimensionSql(colDim);
        var rowInfo = CrosstabMatrixBuilder.DimensionInfo(rowDim);
        var colInfo = CrosstabMatrixBuilder.DimensionInfo(colDim);
        var hasTimeAxis = CrosstabMatrixBuilder.IsTimeDimension(rowDim)
            || CrosstabMatrixBuilder.IsTimeDimension(colDim);
        var temperatureActive = hasTimeAxis && temperatureArea.HasValue;
        var availableMetrics =
            CrosstabMatrixBuilder.ResolveAvailableMetrics(hasTimeAxis, temperatureActive);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        MartFilterSql.AddParameters(filter, parameters);
        var whereClause = MartFilterSql.WhereClause(filter, "f");
        var andClause = MartFilterSql.AndClause(filter, "inv");

        var latestWeek = await QueryLatestWeekAsync(connection, filter, parameters, cancellationToken);
        if (!latestWeek.HasValue)
        {
            return CrosstabMatrixBuilder.BuildEmpty(rowInfo, colInfo, availableMetrics);
        }

        var rowKeyExpr = $"COALESCE(NULLIF({rowExpr}, ''), '{CrosstabMatrixBuilder.UnsetLabel}')";
        var colKeyExpr = $"COALESCE(NULLIF({colExpr}, ''), '{CrosstabMatrixBuilder.UnsetLabel}')";

        // 在日（stock_days）は在庫スナップショットを同一グレインで LEFT JOIN して期間平均する。
        // ただし時間軸絡みのセルではビルダーが在庫系を null 化するため、1.6M 行規模の不要な結合を避けて
        // 0 で代替する（フロー指標のみ算出）。
        var stockDaysExpr = hasTimeAxis ? "0::float8" : "inv.stock_days";
        var inventoryJoin = hasTimeAxis
            ? string.Empty
            : "LEFT JOIN mart.fact_inventory_snapshot inv "
              + "ON inv.date_key = f.date_key AND inv.retailer_key = f.retailer_key "
              + "AND inv.product_key = f.product_key AND inv.sku_key = f.sku_key";

        var flowSql = $"""
            WITH labeled AS (
                SELECT {rowKeyExpr} AS row_key,
                       {colKeyExpr} AS col_key,
                       f.quantity     AS quantity,
                       f.amount       AS amount,
                       f.gross_profit AS gross_profit,
                       {stockDaysExpr} AS stock_days
                FROM mart.fact_sales_weekly f
                JOIN mart.dim_date     dd ON dd.date_key     = f.date_key
                JOIN mart.dim_retailer dr ON dr.retailer_key = f.retailer_key
                JOIN mart.dim_product  dp ON dp.product_key  = f.product_key
                JOIN mart.dim_sku      ds ON ds.sku_key      = f.sku_key
                {inventoryJoin}
                {whereClause}
            )
            SELECT row_key,
                   col_key,
                   COALESCE(SUM(quantity), 0)::bigint     AS quantity,
                   COALESCE(SUM(amount), 0)::bigint       AS amount,
                   COALESCE(SUM(gross_profit), 0)::bigint AS gross_profit,
                   COALESCE(AVG(stock_days), 0)::float8   AS stock_days
            FROM labeled
            GROUP BY row_key, col_key;
            """;
        var flowRows = (await connection.QueryAsync<CrosstabFlowRow>(
            new CommandDefinition(flowSql, parameters, cancellationToken: cancellationToken))).ToList();

        // スナップショット集計（時間軸が含まれない場合のみ。最新週基準）。
        Dictionary<(string Row, string Col), CrosstabSnapshotRow> snapshotMap = new();
        if (!hasTimeAxis)
        {
            parameters.Add("latestWeek", latestWeek.Value);
            var snapshotSql = $"""
                WITH labeled AS (
                    SELECT {rowKeyExpr} AS row_key,
                           {colKeyExpr} AS col_key,
                           inv.stock        AS stock,
                           inv.cum_sales    AS cum_sales,
                           inv.cum_delivery AS cum_delivery
                    FROM mart.fact_inventory_snapshot inv
                    JOIN mart.dim_date     dd ON dd.date_key     = inv.date_key
                    JOIN mart.dim_retailer dr ON dr.retailer_key = inv.retailer_key
                    JOIN mart.dim_product  dp ON dp.product_key  = inv.product_key
                    JOIN mart.dim_sku      ds ON ds.sku_key      = inv.sku_key
                    WHERE dd.week_monday = @latestWeek{andClause}
                )
                SELECT row_key,
                       col_key,
                       COALESCE(SUM(stock), 0)::bigint        AS stock,
                       COALESCE(SUM(cum_sales), 0)::bigint    AS cumulative_sales,
                       COALESCE(SUM(cum_delivery), 0)::bigint AS cumulative_delivery
                FROM labeled
                GROUP BY row_key, col_key;
                """;
            var snapshotRows = await connection.QueryAsync<CrosstabSnapshotRow>(
                new CommandDefinition(snapshotSql, parameters, cancellationToken: cancellationToken));
            foreach (var s in snapshotRows)
            {
                snapshotMap[(s.RowKey, s.ColKey)] = s;
            }
        }

        return CrosstabMatrixBuilder.Build(
            flowRows, snapshotMap, rowInfo, colInfo, hasTimeAxis, availableMetrics,
            latestWeek, rowDim, colDim, temperatureActive ? temperatureArea : null);
    }

    /// <summary>単軸ランキング（主期間 + 任意の比較期間）の集計素材を mart から取得する。</summary>
    public async Task<RankingResponse> GetRankingAsync(
        SalesQueryFilter primaryFilter,
        SalesQueryFilter? comparisonFilter,
        BreakdownDimension dimension,
        int maxRows,
        CancellationToken cancellationToken = default)
    {
        primaryFilter.EnsureValid();
        comparisonFilter?.EnsureValid();
        maxRows = Math.Clamp(maxRows, 1, MaxRankingRows);

        var (groupBy, keyExpr, labelExpr) = ResolveMartRankingDimension(dimension);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var primary = await QueryRankingPeriodAsync(
            connection, primaryFilter, groupBy, keyExpr, labelExpr, cancellationToken);

        RankingPeriodResult? comparison = null;
        if (comparisonFilter != null)
        {
            comparison = await QueryRankingPeriodAsync(
                connection, comparisonFilter, groupBy, keyExpr, labelExpr, cancellationToken);
        }

        return RankingBuilder.Build(dimension, primary, comparison, maxRows);
    }

    /// <summary>1期間ぶんのランキング集計（キー別フロー + 最新週スナップショット）を mart から取得する。</summary>
    private async Task<RankingPeriodResult> QueryRankingPeriodAsync(
        NpgsqlConnection connection,
        SalesQueryFilter filter,
        string groupBy,
        string keyExpr,
        string labelExpr,
        CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        MartFilterSql.AddParameters(filter, parameters);

        var byKey = new Dictionary<string, RankingAccumulator>(StringComparer.Ordinal);

        var labelSql = $"COALESCE(NULLIF({labelExpr}, ''), '{CrosstabMatrixBuilder.UnsetLabel}')";
        var flowSql = $"""
            SELECT {keyExpr} AS key,
                   {labelSql} AS label,
                   COALESCE(SUM(f.quantity), 0)::bigint     AS quantity,
                   COALESCE(SUM(f.amount), 0)::bigint       AS amount,
                   COALESCE(SUM(f.gross_profit), 0)::bigint AS gross_profit
            FROM mart.fact_sales_weekly f
            JOIN mart.dim_date     dd ON dd.date_key     = f.date_key
            JOIN mart.dim_retailer dr ON dr.retailer_key = f.retailer_key
            JOIN mart.dim_product  dp ON dp.product_key  = f.product_key
            JOIN mart.dim_sku      ds ON ds.sku_key      = f.sku_key
            {MartFilterSql.WhereClause(filter, "f")}
            GROUP BY {groupBy};
            """;
        var flowRows = await connection.QueryAsync<RankingFlowRow>(
            new CommandDefinition(flowSql, parameters, cancellationToken: cancellationToken));
        foreach (var row in flowRows)
        {
            byKey[row.Key] = new RankingAccumulator
            {
                Label = row.Label,
                Quantity = row.Quantity,
                Amount = row.Amount,
                GrossProfit = row.GrossProfit,
            };
        }

        var latestWeek = await QueryLatestWeekAsync(connection, filter, parameters, cancellationToken);
        if (latestWeek.HasValue)
        {
            parameters.Add("latestWeek", latestWeek.Value);
            var snapshotSql = $"""
                SELECT {keyExpr} AS key,
                       COALESCE(SUM(inv.stock), 0)::bigint        AS stock,
                       COALESCE(SUM(inv.cum_sales), 0)::bigint    AS cumulative_sales,
                       COALESCE(SUM(inv.cum_delivery), 0)::bigint AS cumulative_delivery,
                       COALESCE(AVG(inv.stock_days), 0)::float8   AS stock_days
                FROM mart.fact_inventory_snapshot inv
                JOIN mart.dim_date     dd ON dd.date_key     = inv.date_key
                JOIN mart.dim_retailer dr ON dr.retailer_key = inv.retailer_key
                JOIN mart.dim_product  dp ON dp.product_key  = inv.product_key
                JOIN mart.dim_sku      ds ON ds.sku_key      = inv.sku_key
                WHERE dd.week_monday = @latestWeek{MartFilterSql.AndClause(filter, "inv")}
                GROUP BY {groupBy};
                """;
            var snapshotRows = await connection.QueryAsync<RankingSnapshotRow>(
                new CommandDefinition(snapshotSql, parameters, cancellationToken: cancellationToken));
            foreach (var row in snapshotRows)
            {
                if (!byKey.TryGetValue(row.Key, out var acc))
                {
                    acc = new RankingAccumulator { Label = row.Key };
                    byKey[row.Key] = acc;
                }
                acc.HasSnapshot = true;
                acc.Stock = row.Stock;
                acc.CumulativeSales = row.CumulativeSales;
                acc.CumulativeDelivery = row.CumulativeDelivery;
                acc.StockDays = row.StockDays;
            }
        }

        return new RankingPeriodResult(byKey, latestWeek);
    }

    /// <summary>
    /// 週次系列（売上フロー指標 + その週・エリアの気温）を mart から取得する。
    /// 気温は <c>mart.dim_climate</c>（実測。CSV由来）を週範囲で集計し、CSV 未カバーの週は
    /// 標準気候（<see cref="ClimateModel"/>）へフォールバックする。
    /// </summary>
    public async Task<WeeklySeriesResponse> GetWeeklySeriesAsync(
        SalesQueryFilter filter,
        TemperatureArea area,
        CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        MartFilterSql.AddParameters(filter, parameters);

        var flowSql = $"""
            SELECT dd.week_monday AS week,
                   COALESCE(SUM(f.quantity), 0)::bigint     AS quantity,
                   COALESCE(SUM(f.amount), 0)::bigint       AS amount,
                   COALESCE(SUM(f.gross_profit), 0)::bigint AS gross_profit
            FROM mart.fact_sales_weekly f
            JOIN mart.dim_date     dd ON dd.date_key     = f.date_key
            JOIN mart.dim_product  dp ON dp.product_key  = f.product_key
            JOIN mart.dim_retailer dr ON dr.retailer_key = f.retailer_key
            {MartFilterSql.WhereClause(filter, "f")}
            GROUP BY dd.week_monday
            ORDER BY dd.week_monday;
            """;
        var flowRows = (await connection.QueryAsync<MartWeeklyFlowRow>(
            new CommandDefinition(flowSql, parameters, cancellationToken: cancellationToken))).ToList();

        // 週ごとの在庫スナップショット（店頭在庫・在日・消化率の素材）。
        // 売上ファクトと同一グレインのため、同じフィルタで週単位に集計して合流する。
        var snapshotSql = $"""
            SELECT dd.week_monday AS week,
                   COALESCE(SUM(inv.stock), 0)::bigint        AS stock,
                   COALESCE(SUM(inv.cum_sales), 0)::bigint    AS cum_sales,
                   COALESCE(SUM(inv.cum_delivery), 0)::bigint AS cum_delivery,
                   COALESCE(AVG(inv.stock_days), 0)::float8   AS stock_days
            FROM mart.fact_inventory_snapshot inv
            JOIN mart.dim_date     dd ON dd.date_key     = inv.date_key
            JOIN mart.dim_product  dp ON dp.product_key  = inv.product_key
            JOIN mart.dim_retailer dr ON dr.retailer_key = inv.retailer_key
            {MartFilterSql.WhereClause(filter, "inv")}
            GROUP BY dd.week_monday;
            """;
        var snapshotRows = await connection.QueryAsync<MartWeeklySnapshotRow>(
            new CommandDefinition(snapshotSql, parameters, cancellationToken: cancellationToken));
        var snapshotByWeek = snapshotRows.ToDictionary(r => r.Week);

        // 実気温（週=月曜が表す前週 月〜日の範囲）を集計。完全週（7日）のみ採用する。
        var climateParameters = new DynamicParameters();
        climateParameters.Add("area", ClimateModel.AreaCode(area));
        var climateRows = await connection.QueryAsync<MartWeeklyClimateRow>(
            new CommandDefinition("""
                SELECT dd.week_monday AS week,
                       AVG(c.temp_avg)::float8 AS temp_avg,
                       MAX(c.temp_max)::float8 AS temp_max,
                       MIN(c.temp_min)::float8 AS temp_min,
                       COUNT(*)::int           AS day_count
                FROM mart.dim_date dd
                JOIN mart.dim_climate c
                  ON c.area_code = @area
                 AND c.the_date BETWEEN dd.week_monday - 7 AND dd.week_monday - 1
                GROUP BY dd.week_monday;
                """, climateParameters, cancellationToken: cancellationToken));
        var climateByWeek = climateRows.ToDictionary(r => r.Week);

        var points = flowRows
            .Select(r =>
            {
                TemperatureReading temp;
                if (climateByWeek.TryGetValue(r.Week, out var c) && c.DayCount == FullWeekDays)
                {
                    temp = new TemperatureReading(c.TempAvg, c.TempMax, c.TempMin);
                }
                else
                {
                    temp = ClimateModel.Week(area, r.Week);
                }
                // 同一グレインの派生のため snapshot は通常必ず存在するが、欠損時は 0 で埋める。
                snapshotByWeek.TryGetValue(r.Week, out var snapshot);
                return new WeeklySeriesPoint(
                    r.Week, r.Quantity, r.Amount, r.GrossProfit,
                    Round1(temp.Average), Round1(temp.Maximum), Round1(temp.Minimum),
                    snapshot?.Stock ?? 0,
                    snapshot?.StockDays ?? 0,
                    snapshot is null ? 0 : AggregateMath.Ratio(snapshot.CumSales, snapshot.CumDelivery));
            })
            .ToList();

        return new WeeklySeriesResponse(
            ClimateModel.AreaCode(area), ClimateModel.CityName(area), points);
    }

    /// <summary>
    /// 消化率×値引き率の散布図素材（型番＝業態×記号×品番 単位）を mart から取得する。
    /// 値引き率は定価（dim_sku.list_price）が必要なため、定価未登録の型番は対象外。
    /// </summary>
    public async Task<MarkdownScatterResponse> GetMarkdownScatterAsync(
        SalesQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        MartFilterSql.AddParameters(filter, parameters);

        var latestWeek = await QueryLatestWeekAsync(connection, filter, parameters, cancellationToken);
        if (!latestWeek.HasValue)
        {
            return new MarkdownScatterResponse(null, Array.Empty<MarkdownScatterPoint>());
        }

        parameters.Add("latestWeek", latestWeek.Value);
        parameters.Add("limit", MaxMarkdownPoints);
        var whereClause = MartFilterSql.WhereClause(filter, "f");
        var andClauseFlow = MartFilterSql.AndClause(filter, "f");
        var andClauseInv = MartFilterSql.AndClause(filter, "inv");

        // 期間内フロー数量、最新週の平均売価（売上ファクト）・累計/店頭在庫/在日（在庫スナップショット）、
        // 定価（dim_sku）を型番（product_key）単位で結合する。季節は商品次元の属性。
        var sql = $"""
            WITH flow AS (
                SELECT f.product_key,
                       COALESCE(SUM(f.quantity), 0)::bigint AS quantity
                FROM mart.fact_sales_weekly f
                JOIN mart.dim_date     dd ON dd.date_key     = f.date_key
                JOIN mart.dim_product  dp ON dp.product_key  = f.product_key
                JOIN mart.dim_retailer dr ON dr.retailer_key = f.retailer_key
                {whereClause}
                GROUP BY f.product_key
            ),
            price AS (
                SELECT f.product_key,
                       AVG(NULLIF(f.sale_price, 0))::float8 AS avg_baika
                FROM mart.fact_sales_weekly f
                JOIN mart.dim_date     dd ON dd.date_key     = f.date_key
                JOIN mart.dim_product  dp ON dp.product_key  = f.product_key
                JOIN mart.dim_retailer dr ON dr.retailer_key = f.retailer_key
                WHERE dd.week_monday = @latestWeek{andClauseFlow}
                GROUP BY f.product_key
            ),
            cum AS (
                SELECT inv.product_key,
                       COALESCE(SUM(inv.cum_sales), 0)::bigint    AS cum_sales,
                       COALESCE(SUM(inv.cum_delivery), 0)::bigint AS cum_delivery,
                       COALESCE(SUM(inv.stock), 0)::bigint        AS stock,
                       COALESCE(AVG(inv.stock_days), 0)::float8   AS stock_days
                FROM mart.fact_inventory_snapshot inv
                JOIN mart.dim_date     dd ON dd.date_key     = inv.date_key
                JOIN mart.dim_product  dp ON dp.product_key  = inv.product_key
                JOIN mart.dim_retailer dr ON dr.retailer_key = inv.retailer_key
                WHERE dd.week_monday = @latestWeek{andClauseInv}
                GROUP BY inv.product_key
            ),
            list AS (
                SELECT ds.product_key,
                       AVG(ds.list_price)::float8 AS list_price
                FROM mart.dim_sku ds
                WHERE ds.list_price > 0
                GROUP BY ds.product_key
            )
            SELECT dp.channel_code || '|' || dp.product_sign || '|' || dp.product_code AS key,
                   dp.product_code AS label,
                   dp.channel_code AS business_type,
                   CASE WHEN cum.cum_delivery > 0
                        THEN cum.cum_sales::float8 / cum.cum_delivery * 100.0
                        ELSE 0 END AS sell_through_rate,
                   GREATEST(0, LEAST(100, (1 - price.avg_baika / list.list_price) * 100.0)) AS markdown_rate,
                   COALESCE(flow.quantity, 0)::bigint AS quantity,
                   NULLIF(dp.season, '')              AS season,
                   COALESCE(cum.stock, 0)::bigint     AS stock,
                   COALESCE(cum.stock_days, 0)::float8 AS stock_days
            FROM price
            JOIN mart.dim_product dp ON dp.product_key = price.product_key
            JOIN list ON list.product_key = price.product_key
            LEFT JOIN cum  ON cum.product_key  = price.product_key
            LEFT JOIN flow ON flow.product_key = price.product_key
            WHERE list.list_price > 0
              AND price.avg_baika IS NOT NULL
            ORDER BY quantity DESC, key
            LIMIT @limit;
            """;

        var points = (await connection.QueryAsync<MarkdownScatterPoint>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        return new MarkdownScatterResponse(latestWeek.Value.ToString("yyyy-MM-dd"), points);
    }

    /// <summary>商品導入管理の最大ページサイズ。</summary>
    private const int MaxIntroductionPageSize = 200;

    /// <summary>
    /// 商品導入管理の一覧（ページング）を mart から取得する。
    /// 1行 = 商品（業態×記号×品番）。導入日は配下 SKU の <c>attributes->>'donyu'</c>（YYYYMMDD 文字列）の
    /// 最小値（最初に導入された日）。並び順は導入日（既定: 降順＝新しい導入が先頭、NULL は末尾）。
    /// </summary>
    public async Task<MartIntroductionPage> GetIntroductionsAsync(
        MartIntroductionQuery query,
        bool ascending,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        query.EnsureValid();
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxIntroductionPageSize);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        MartIntroductionFilterSql.AddParameters(query, parameters);
        parameters.Add("limit", pageSize);
        // page は外部入力のため long で乗算し、巨大値でも負の OFFSET（int オーバーフロー）にしない。
        parameters.Add("offset", (long)(page - 1) * pageSize);

        var productWhere = MartIntroductionFilterSql.ProductWhereClause(query);
        var donyuWhere = MartIntroductionFilterSql.DonyuWhereClause(query);
        var direction = ascending ? "ASC" : "DESC";

        // まず商品次元の条件で商品集合（filtered）を絞り、SKU 集約（導入日・SKU数・代表画像）と
        // 全期間フロー（売上数量）は絞り込んだ商品に対してのみ集計する（全表集約の回避）。
        // 導入日条件は SKU 集約後の値に対するため外側で適用する。
        // 業態表示名は dim_retailer の channel 列から重複排除して引く（business_type マスタ由来）。
        var sql = $"""
            WITH filtered AS (
                SELECT dp.product_key, dp.channel_code, dp.department_code, dp.department_name,
                       dp.product_sign, dp.product_code, dp.product_name, dp.brand, dp.manager
                FROM mart.dim_product dp
                {productWhere}
            ),
            sku AS (
                SELECT ds.product_key,
                       COUNT(*)::int                AS sku_count,
                       MIN(ds.attributes->>'donyu') AS donyu,
                       MIN(ds.image_url)            AS image_url
                FROM mart.dim_sku ds
                JOIN filtered fp ON fp.product_key = ds.product_key
                GROUP BY ds.product_key
            ),
            flow AS (
                SELECT f.product_key,
                       COALESCE(SUM(f.quantity), 0)::bigint AS sales_quantity
                FROM mart.fact_sales_weekly f
                JOIN filtered fp ON fp.product_key = f.product_key
                GROUP BY f.product_key
            ),
            channel AS (
                SELECT DISTINCT ON (channel_code) channel_code, channel_name
                FROM mart.dim_retailer
                ORDER BY channel_code, channel_name NULLS LAST
            )
            SELECT dp.channel_code,
                   ch.channel_name,
                   dp.department_code,
                   dp.department_name,
                   dp.product_sign,
                   dp.product_code,
                   dp.product_name,
                   dp.brand,
                   dp.manager,
                   sku.donyu,
                   sku.sku_count,
                   COALESCE(flow.sales_quantity, 0) AS sales_quantity,
                   sku.image_url AS primary_image_url,
                   (COUNT(*) OVER ())::int AS total_count
            FROM filtered dp
            JOIN sku ON sku.product_key = dp.product_key
            LEFT JOIN flow ON flow.product_key = dp.product_key
            LEFT JOIN channel ch ON ch.channel_code = dp.channel_code
            {donyuWhere}
            ORDER BY sku.donyu {direction} NULLS LAST,
                     dp.product_code, dp.product_sign, dp.channel_code
            LIMIT @limit OFFSET @offset;
            """;

        var rawRows = (await connection.QueryAsync<MartIntroductionRawRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        var totalCount = rawRows.Count > 0
            ? rawRows[0].TotalCount
            : await CountIntroductionsAsync(connection, query, parameters, cancellationToken);

        var items = rawRows
            .Select(row => new MartIntroductionRow(
                row.ChannelCode, row.ChannelName, row.DepartmentCode, row.DepartmentName,
                row.ProductSign, row.ProductCode, row.ProductName, row.Brand, row.Manager,
                ParseDonyu(row.Donyu), row.SkuCount, row.SalesQuantity, row.PrimaryImageUrl))
            .ToList();

        return new MartIntroductionPage(items, totalCount, page, pageSize);
    }

    /// <summary>導入管理の対象商品の総件数（ページ範囲外で本体クエリが空のときのフォールバック）。</summary>
    private static async Task<int> CountIntroductionsAsync(
        NpgsqlConnection connection,
        MartIntroductionQuery query,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            WITH filtered AS (
                SELECT dp.product_key
                FROM mart.dim_product dp
                {MartIntroductionFilterSql.ProductWhereClause(query)}
            ),
            sku AS (
                SELECT ds.product_key, MIN(ds.attributes->>'donyu') AS donyu
                FROM mart.dim_sku ds
                JOIN filtered fp ON fp.product_key = ds.product_key
                GROUP BY ds.product_key
            )
            SELECT COUNT(*)::int
            FROM sku
            {MartIntroductionFilterSql.DonyuWhereClause(query)};
            """;
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// 導入日（YYYYMMDD 文字列）を <see cref="DateOnly"/> へ変換する。
    /// 不正値（存在しない日付等）は null（再構築側で 8 桁数字に絞っているが二重に防御する）。
    /// </summary>
    private static DateOnly? ParseDonyu(string? donyu)
        => DateOnly.TryParseExact(donyu, "yyyyMMdd", out var parsed) ? parsed : null;

    /// <summary>商品導入管理のフィルタ選択肢（ブランド・担当者・服種）を mart の商品次元から取得する。</summary>
    public async Task<MartIntroductionOptions> GetIntroductionOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var brands = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT brand FROM mart.dim_product
            WHERE brand IS NOT NULL AND brand <> '' ORDER BY brand;
            """, cancellationToken: cancellationToken))).ToList();

        var managers = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT manager FROM mart.dim_product
            WHERE manager IS NOT NULL AND manager <> '' ORDER BY manager;
            """, cancellationToken: cancellationToken))).ToList();

        var hinbans = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT product_code FROM mart.dim_product
            WHERE product_code <> '' ORDER BY product_code;
            """, cancellationToken: cancellationToken))).ToList();

        return new MartIntroductionOptions(brands, managers, hinbans);
    }

    /// <summary>週次トレンド（取込日昇順）を mart から取得する（サマリー・売上分析で共有）。</summary>
    private static async Task<IReadOnlyList<TrendPoint>> QueryWeeklyTrendAsync(
        NpgsqlConnection connection,
        SalesQueryFilter filter,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        // フロー指標はファクトに事前計算済みのため SUM するだけ（設計の狙い）。
        var sql = $"""
            SELECT dd.week_monday AS date,
                   COALESCE(SUM(f.quantity), 0)::bigint     AS quantity,
                   COALESCE(SUM(f.amount), 0)::bigint       AS amount,
                   COALESCE(SUM(f.gross_profit), 0)::bigint AS gross_profit
            FROM mart.fact_sales_weekly f
            JOIN mart.dim_date     dd ON dd.date_key     = f.date_key
            JOIN mart.dim_product  dp ON dp.product_key  = f.product_key
            JOIN mart.dim_retailer dr ON dr.retailer_key = f.retailer_key
            {MartFilterSql.WhereClause(filter, "f")}
            GROUP BY dd.week_monday
            ORDER BY dd.week_monday;
            """;
        var rows = await connection.QueryAsync<TrendPoint>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <summary>フィルタ範囲内の最新取込週（MAX week_monday）を取得する。データなしは null。</summary>
    private static async Task<DateOnly?> QueryLatestWeekAsync(
        NpgsqlConnection connection,
        SalesQueryFilter filter,
        DynamicParameters parameters,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT MAX(dd.week_monday)
            FROM mart.fact_sales_weekly f
            JOIN mart.dim_date     dd ON dd.date_key     = f.date_key
            JOIN mart.dim_product  dp ON dp.product_key  = f.product_key
            JOIN mart.dim_retailer dr ON dr.retailer_key = f.retailer_key
            {MartFilterSql.WhereClause(filter, "f")};
            """;
        return await connection.ExecuteScalarAsync<DateOnly?>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    /// <summary>商品一覧の並び替えキーを mart クエリの式に解決する。</summary>
    private static string MartProductSortExpression(ProductSortKey sortKey) => sortKey switch
    {
        ProductSortKey.SalesQuantity => "sales_quantity",
        ProductSortKey.SalesAmount => "sales_amount",
        ProductSortKey.GrossProfit => "gross_profit",
        ProductSortKey.Stock => "s.stock",
        ProductSortKey.SellThroughRate =>
            "(s.cum_sales::float8 / NULLIF(s.cum_delivery, 0))",
        ProductSortKey.StockDays => "s.average_stock_days",
        _ => "sales_amount",
    };

    /// <summary>
    /// クロス集計の <see cref="CrosstabDimension"/> を mart の SQL 式（text を返す式）に解決する。
    /// 帳票区分・棚割は mart の集計軸としては保持しないため未対応（400。棚割1のフィルタは対応済み）。
    /// エイリアスは dd/dr/dp/ds 固定。
    /// </summary>
    private static string ResolveMartCrosstabDimensionSql(CrosstabDimension dim) => dim switch
    {
        CrosstabDimension.TimeYear => "dd.year::text",
        CrosstabDimension.TimeQuarter => "dd.year::text || '-Q' || dd.quarter::text",
        CrosstabDimension.TimeMonth => "to_char(dd.week_monday, 'YYYY-MM')",
        CrosstabDimension.CategoryDepartment => "dp.department_code",
        CrosstabDimension.CategoryBusinessType => "dr.channel_code",
        CrosstabDimension.CategorySeason => "dp.season",
        CrosstabDimension.CategoryHinban => "dp.product_code",
        CrosstabDimension.CategoryProduct => "(dp.product_code || '-' || ds.unit_code)",
        CrosstabDimension.CategoryColor => "ds.variant_axis1_value",
        CrosstabDimension.CategorySize => "ds.variant_axis2_value",
        CrosstabDimension.CategoryShohinKigo => "dp.product_sign",
        CrosstabDimension.CategoryChohyoKubun or CrosstabDimension.CategoryTanawari1
            or CrosstabDimension.CategoryTanawari2 => throw new AppException(
            ErrorCodes.UnknownDimension, 400,
            "帳票区分・棚割はスタースキーマ分析の集計軸では未対応です（棚割1はフィルタで指定できます）。"),
        _ => throw new AppException(ErrorCodes.UnknownDimension, 400),
    };

    /// <summary>
    /// ランキングの <see cref="BreakdownDimension"/> を mart の (GroupBy, KeyExpr, LabelExpr) に解決する。
    /// 帳票区分・棚割は mart の集計軸としては保持しないため未対応（400。棚割1のフィルタは対応済み）。
    /// エイリアスは dr/dp/ds 固定。
    /// </summary>
    private static (string GroupBy, string KeyExpr, string LabelExpr) ResolveMartRankingDimension(
        BreakdownDimension dimension) => dimension switch
    {
        BreakdownDimension.Department => ("dp.department_code", "dp.department_code", "dp.department_code"),
        BreakdownDimension.BusinessType => ("dr.channel_code", "dr.channel_code", "dr.channel_code"),
        BreakdownDimension.Season => ("dp.season", "dp.season", "dp.season"),
        BreakdownDimension.Hinban => ("dp.product_code", "dp.product_code", "dp.product_code"),
        BreakdownDimension.ShohinKigo => ("dp.product_sign", "dp.product_sign", "dp.product_sign"),
        BreakdownDimension.Color => ("ds.variant_axis1_value", "ds.variant_axis1_value", "ds.variant_axis1_value"),
        BreakdownDimension.Size => ("ds.variant_axis2_value", "ds.variant_axis2_value", "ds.variant_axis2_value"),
        BreakdownDimension.Product => (
            "dp.channel_code, dp.product_sign, dp.product_code, ds.unit_code",
            "dp.channel_code || '|' || dp.product_sign || '|' || dp.product_code || '|' || ds.unit_code",
            "dp.product_code || '-' || ds.unit_code"),
        BreakdownDimension.ChohyoKubun or BreakdownDimension.Tanawari1
            or BreakdownDimension.Tanawari2 => throw new AppException(
            ErrorCodes.UnknownDimension, 400,
            "帳票区分・棚割はスタースキーマ分析の集計軸では未対応です（棚割1はフィルタで指定できます）。"),
        _ => throw new AppException(ErrorCodes.UnknownDimension, 400),
    };

    private static double Round1(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

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

    /// <summary>
    /// 集計軸の文字列を mart 次元のSQL式（キー・ラベル・正規名）に解決する。ホワイトリスト照合。
    /// breakdown 専用（売上分析・全社サマリーの集計軸別ランキング）。<c>brand</c> はここだけが対応し、
    /// ランキング/クロス集計（<see cref="ResolveMartRankingDimension"/>/<see cref="ResolveMartCrosstabDimensionSql"/>）
    /// の軸ではない。
    /// </summary>
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

}

/// <summary><see cref="SalesQueryFilter"/> から mart 次元に対する WHERE 条件を組み立てる。</summary>
/// <remarks>
/// <para>
/// 棚割1 は商品次元の拡張属性（<c>dim_product.attributes->>'tanawari1'</c>。SCD1＝最新取込週の値）に
/// 対して適用する。平均在庫日数（在日）バケットは、ファクトと同一グレイン
/// （週×小売×SKU）の在庫スナップショット <c>fact_inventory_snapshot.stock_days</c> を EXISTS で
/// 参照して適用する（sales 系の週次行 <c>zainiti</c> フィルタと同一意味論）。
/// </para>
/// <para>
/// 次元エイリアスは dd/dp/dr 固定。ファクト（売上 <c>fact_sales_weekly</c> または在庫
/// <c>fact_inventory_snapshot</c>）のエイリアスはクエリごとに異なるため引数で受け取る。
/// </para>
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
        if (filter.Tanawari1 is { Length: > 0 }) parameters.Add("tanawari1", filter.Tanawari1);
    }

    /// <summary>
    /// 条件式を <c>AND</c> 連結で返す（条件がなければ空文字）。
    /// </summary>
    /// <param name="filter">共通フィルタ。</param>
    /// <param name="factAlias">
    /// クエリ対象ファクトのエイリアス（在日バケットの EXISTS が結合キーとして参照する。
    /// date_key / retailer_key / product_key / sku_key を持つこと）。
    /// </param>
    public static string Conditions(SalesQueryFilter filter, string factAlias)
    {
        var conditions = new List<string>();

        if (filter.From.HasValue) conditions.Add("dd.week_monday >= @from");
        if (filter.To.HasValue) conditions.Add("dd.week_monday <= @to");
        if (filter.Departments is { Length: > 0 }) conditions.Add("dp.department_code = ANY(@departments)");
        if (filter.BusinessTypes is { Length: > 0 }) conditions.Add("dr.channel_code = ANY(@businessTypes)");
        if (filter.Seasons is { Length: > 0 }) conditions.Add("dp.season = ANY(@seasons)");
        if (filter.Hinbans is { Length: > 0 }) conditions.Add("dp.product_code = ANY(@hinbans)");
        if (filter.Tanawari1 is { Length: > 0 })
        {
            conditions.Add("COALESCE(dp.attributes->>'tanawari1', '') = ANY(@tanawari1)");
        }

        // 在日バケット: 同一グレイン（PK 完全一致）の在庫スナップショット行の stock_days を参照する。
        // 在庫クエリ（factAlias が fact_inventory_snapshot）では自己の PK 参照となり同値条件に縮退する。
        var stockDays = StockDaysSql.OrCondition(filter.StockDaysBuckets, "z.stock_days");
        if (stockDays is not null)
        {
            conditions.Add(
                $"EXISTS (SELECT 1 FROM mart.fact_inventory_snapshot z "
                + $"WHERE z.date_key = {factAlias}.date_key AND z.retailer_key = {factAlias}.retailer_key "
                + $"AND z.product_key = {factAlias}.product_key AND z.sku_key = {factAlias}.sku_key "
                + $"AND {stockDays})");
        }

        return string.Join(" AND ", conditions);
    }

    /// <summary><c>WHERE ...</c> 句を返す（条件がなければ空文字）。</summary>
    public static string WhereClause(SalesQueryFilter filter, string factAlias)
    {
        var conditions = Conditions(filter, factAlias);
        return conditions.Length == 0 ? string.Empty : "WHERE " + conditions;
    }

    /// <summary>既存の WHERE に続けて連結する <c>AND ...</c> を返す（条件がなければ空文字）。</summary>
    public static string AndClause(SalesQueryFilter filter, string factAlias)
    {
        var conditions = Conditions(filter, factAlias);
        return conditions.Length == 0 ? string.Empty : " AND " + conditions;
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

/// <summary>Dapper マッピング用の内部行（サマリーの最新週在庫スナップショット）。</summary>
internal sealed record MartInvSnapshotRow(long Stock, long CumulativeSales, long CumulativeDelivery);

/// <summary>Dapper マッピング用の内部行（在庫・発注 KPI）。</summary>
internal sealed record MartInventoryKpiRow(
    long TotalStock,
    decimal TotalOrderQuantity,
    long TotalAdvanceQuantity,
    long CumulativeSales,
    long CumulativeDelivery,
    double AverageStockDays);

/// <summary>Dapper マッピング用の内部行（在庫・発注の部門別内訳）。</summary>
internal sealed record MartInventoryBreakdownRawRow(
    string Key,
    string Label,
    long Stock,
    decimal OrderQuantity,
    long AdvanceQuantity,
    long CumulativeSales,
    long CumulativeDelivery);

/// <summary>Dapper マッピング用の内部行（商品別分析の1行 + 総件数）。</summary>
internal sealed record MartProductRawRow(
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

/// <summary>Dapper マッピング用の内部行（商品導入管理の1行 + 総件数）。donyu は YYYYMMDD 文字列。</summary>
internal sealed record MartIntroductionRawRow(
    string ChannelCode,
    string? ChannelName,
    string? DepartmentCode,
    string? DepartmentName,
    string ProductSign,
    string ProductCode,
    string? ProductName,
    string? Brand,
    string? Manager,
    string? Donyu,
    int SkuCount,
    long SalesQuantity,
    string? PrimaryImageUrl,
    int TotalCount);

/// <summary>Dapper マッピング用の内部行（週次系列のフロー指標）。</summary>
internal sealed record MartWeeklyFlowRow(DateOnly Week, long Quantity, long Amount, long GrossProfit);

/// <summary>Dapper マッピング用の内部行（週次系列の在庫スナップショット集計）。</summary>
internal sealed record MartWeeklySnapshotRow(
    DateOnly Week, long Stock, long CumSales, long CumDelivery, double StockDays);

/// <summary>Dapper マッピング用の内部行（週範囲で集計した実気温と対象日数）。</summary>
internal sealed record MartWeeklyClimateRow(
    DateOnly Week, double TempAvg, double TempMax, double TempMin, int DayCount);
