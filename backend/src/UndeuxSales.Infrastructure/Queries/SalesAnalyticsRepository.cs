using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>クロス集計クエリの遅さを警告するしきい値（ms）。</summary>
    private const int CrosstabSlowQueryWarningThresholdMs = 1000;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<SalesAnalyticsRepository> _logger;

    public SalesAnalyticsRepository(
        IDbConnectionFactory connectionFactory,
        ILogger<SalesAnalyticsRepository>? logger = null)
    {
        _connectionFactory = connectionFactory;
        // DI 経由では ILogger<T> が自動注入される。テストや旧呼び出しのために null 許容で
        // NullLogger を fallback として用意する（ロガー注入を必須としない）。
        _logger = logger ?? NullLogger<SalesAnalyticsRepository>.Instance;
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
            AggregateMath.Ratio(grossProfit, amount),
            productCount,
            snapshot.Stock,
            AggregateMath.Ratio(snapshot.CumulativeSales, snapshot.CumulativeDelivery),
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

    // ------------------------------------------------------------
    // ランキング分析（単軸ランキング + 期間比較。順位・複合スコア・ABC はフロント射影）
    // ------------------------------------------------------------

    /// <summary>ランキングで返却する最大行数。単軸のためクロス集計より大きめに取る。</summary>
    private const int MaxRankingRows = 1000;

    /// <summary>ランキング分析クエリの遅さを警告するしきい値（ms）。比較ありは最大2期間ぶん走る。</summary>
    private const int RankingSlowQueryWarningThresholdMs = 1500;

    /// <summary>
    /// 単軸ランキングの集計素材を取得する。主期間と任意の比較期間について、ディメンション別の
    /// フロー指標（数量・金額・粗利）と最新週スナップショット指標（在庫・消化率・在日）を集計する。
    /// </summary>
    /// <remarks>
    /// 順位・複合スコア・累積構成比・ABC ランクは、ユーザーが対話的に選ぶ並び替え指標・重みに依存する
    /// 表示射影であるため、フロント側で本素材から算出する（クロス集計の表示モード切替と同じ思想）。
    /// 集計の SoT は <c>sales_weekly</c>。返却順は顕著性（主／比較の売上金額の大きい方）降順で、
    /// フロントはそこから選択指標・複合スコアで再ソートして順位を確定する。
    /// </remarks>
    /// <param name="primaryFilter">主期間フィルタ。</param>
    /// <param name="comparisonFilter">比較期間フィルタ。<c>null</c> のとき比較なし。</param>
    /// <param name="dimension">集計軸。</param>
    /// <param name="maxRows">返却する最大行数（1..<see cref="MaxRankingRows"/> にクランプ）。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
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

        // 実行時間計測（しきい値超過時に警告ログ）。try/finally で例外パスでも必ず計測する。
        var stopwatch = Stopwatch.StartNew();
        RankingResponse? response = null;
        try
        {
            var (groupBy, keyExpr, labelExpr) = ResolveDimension(dimension);

            // 主期間・比較期間を1接続で逐次集計する（接続プール消費を1本に抑える）。
            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

            var primary = await QueryRankingPeriodAsync(
                connection, primaryFilter, groupBy, keyExpr, labelExpr, cancellationToken);

            RankingPeriodResult? comparison = null;
            if (comparisonFilter != null)
            {
                comparison = await QueryRankingPeriodAsync(
                    connection, comparisonFilter, groupBy, keyExpr, labelExpr, cancellationToken);
            }

            response = RankingBuilder.Build(dimension, primary, comparison, maxRows);
            return response;
        }
        finally
        {
            LogIfRankingSlow(stopwatch, dimension, primaryFilter,
                hasComparison: comparisonFilter != null,
                rowsCount: response?.Rows.Count ?? -1);
        }
    }

    /// <summary>1期間ぶんのランキング集計（キー別のフロー + 最新週スナップショット）を取得する。</summary>
    private static async Task<RankingPeriodResult> QueryRankingPeriodAsync(
        NpgsqlConnection connection,
        SalesQueryFilter filter,
        string groupBy,
        string keyExpr,
        string labelExpr,
        CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);

        var byKey = new Dictionary<string, RankingAccumulator>(StringComparer.Ordinal);

        // フロー集計（期間内）。null/空ラベルは未設定として置換（クロス集計と同一表記）。
        var labelSql = $"COALESCE(NULLIF({labelExpr}, ''), '{CrosstabMatrixBuilder.UnsetLabel}')";
        var flowSql = $"""
            SELECT {keyExpr} AS key,
                   {labelSql} AS label,
                   COALESCE(SUM({SalesMetricSql.WeekQuantity}), 0)::bigint    AS quantity,
                   COALESCE(SUM({SalesMetricSql.WeekAmount}), 0)::bigint      AS amount,
                   COALESCE(SUM({SalesMetricSql.WeekGrossProfit}), 0)::bigint AS gross_profit
            FROM sales_weekly sw
            {SalesFilterSql.WhereClause(filter, "sw")}
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

        // 在庫スナップショット（その期間の最新取込週基準）。最新週が無ければスナップショットは付与しない。
        var latestWeek = await QueryLatestWeekAsync(connection, filter, parameters, cancellationToken);
        if (latestWeek.HasValue)
        {
            parameters.Add("latestWeek", latestWeek.Value);
            var snapshotSql = $"""
                SELECT {keyExpr} AS key,
                       COALESCE(SUM(sw.zaikosu), 0)::bigint             AS stock,
                       COALESCE(SUM(sw.ruikei_uriage_count), 0)::bigint AS cumulative_sales,
                       COALESCE(SUM(sw.ruikei_nohin_count), 0)::bigint  AS cumulative_delivery,
                       COALESCE(AVG(sw.zainiti), 0)::float8             AS stock_days
                FROM sales_weekly sw
                WHERE sw.import_date = @latestWeek{SalesFilterSql.AndClause(filter, "sw")}
                GROUP BY {groupBy};
                """;
            var snapshotRows = await connection.QueryAsync<RankingSnapshotRow>(
                new CommandDefinition(snapshotSql, parameters, cancellationToken: cancellationToken));
            foreach (var row in snapshotRows)
            {
                // スナップショットは同一フィルタ・同一期間内の最新週なのでフロー集合の部分集合。
                // 理論上フロー側に必ず存在するが、防御的に欠落時はキーをラベル代替で生成する。
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

    /// <summary>ランキング分析クエリの実行時間がしきい値を超えたら警告ログを出す。</summary>
    private void LogIfRankingSlow(
        Stopwatch stopwatch,
        BreakdownDimension dimension,
        SalesQueryFilter filter,
        bool hasComparison,
        int rowsCount)
    {
        stopwatch.Stop();
        var elapsedMs = stopwatch.ElapsedMilliseconds;
        if (elapsedMs <= RankingSlowQueryWarningThresholdMs)
        {
            return;
        }
        _logger.LogWarning(
            "Ranking query slow: {ElapsedMs}ms dimension={Dimension} comparison={HasComparison} rows={Rows} "
            + "from={From} to={To} depts={DeptCount} biz={BizCount} seasons={SeasonCount} hinbans={HinbanCount}",
            elapsedMs,
            dimension,
            hasComparison,
            rowsCount,
            filter.From,
            filter.To,
            filter.Departments?.Length ?? 0,
            filter.BusinessTypes?.Length ?? 0,
            filter.Seasons?.Length ?? 0,
            filter.Hinbans?.Length ?? 0);
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
                AggregateMath.Ratio(row.CumulativeSales, row.CumulativeDelivery)))
            .ToList();

        var kpi = new InventoryKpi(
            kpiRow.TotalStock,
            kpiRow.TotalOrderQuantity,
            kpiRow.TotalAdvanceQuantity,
            kpiRow.CumulativeSales,
            kpiRow.CumulativeDelivery,
            AggregateMath.Ratio(kpiRow.CumulativeSales, kpiRow.CumulativeDelivery),
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
        // page は外部入力のため long で乗算し、巨大値でも負の OFFSET（int オーバーフロー）にしない。
        parameters.Add("offset", (long)(page - 1) * pageSize);

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
                AggregateMath.Ratio(row.CumulativeSales, row.CumulativeDelivery),
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

    // ------------------------------------------------------------
    // クロス集計（マトリクス: 行×列ディメンション、7メトリクスのセル値）
    // ------------------------------------------------------------

    /// <summary>
    /// カテゴリ系ディメンションの SQL 式（sw エイリアス前提）。
    /// クロス集計（<see cref="ResolveCrosstabDimensionSql"/>）とブレイクダウン
    /// （<see cref="ResolveDimension"/>）の両方から参照され、SQL 式の二重メンテを防ぐ。
    /// 時間軸ディメンションはクロス集計固有のため別扱い（<see cref="ResolveCrosstabDimensionSql"/> 内）。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> CategoryDimensionSqlMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["department"] = "sw.department",
            ["businessType"] = "sw.gyotai_code",
            ["season"] = "sw.kisetsu",
            ["hinban"] = "sw.hinban_code",
            ["color"] = "sw.color",
            ["size"] = "sw.size",
            ["chohyoKubun"] = "sw.chohyo_kubun_name",
            ["tanawari1"] = "COALESCE(sw.tanawari1, '')",
            ["tanawari2"] = "COALESCE(sw.tanawari2, '')",
            ["shohinKigo"] = "sw.shohin_kigou",
            // 単品（品番-単品）は表示用ラベルの式（クロス集計用）。
            // ブレイクダウンは GroupBy/KeyExpr/LabelExpr が分離する別仕様のため利用しない。
            ["product"] = "(sw.hinban_code || '-' || sw.tanpin_code)",
        };

    /// <summary>クロス集計マトリクス（行×列の2次元集計）を取得する。</summary>
    /// <remarks>
    /// フロー指標（数量・金額・粗利・在日）は期間内集計、在庫スナップショット指標（在庫・消化率）は
    /// 最新取込週のみ。時間軸（年/四半期/月）が行か列のいずれかに含まれる場合、最新週スナップショットの
    /// 概念が時間バケットに揃わないため在庫系メトリクスは null を返し、AvailableMetrics からも除外する。
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

        // 実行時間計測（しきい値超過時に警告ログを出す）。クエリ単体ではなく
        // GetCrosstabMatrixAsync 全体（フロー + スナップショット + 後段の集計構築）を計測する。
        // try/finally で例外パス（DB 失敗・OperationCanceled 等）でも必ず計測ログが出るようにする。
        var stopwatch = Stopwatch.StartNew();
        CrosstabMatrixResponse? response = null;
        try
        {
            var rowInfo = CrosstabMatrixBuilder.DimensionInfo(rowDim);
            var colInfo = CrosstabMatrixBuilder.DimensionInfo(colDim);
            var hasTimeAxis = CrosstabMatrixBuilder.IsTimeDimension(rowDim)
                || CrosstabMatrixBuilder.IsTimeDimension(colDim);

            // メトリクス可否（時間軸絡みは在庫系を除外、時間軸＋エリアなら気温系を追加）は共有ビルダーに委譲。
            var temperatureActive = hasTimeAxis && temperatureArea.HasValue;
            var availableMetrics =
                CrosstabMatrixBuilder.ResolveAvailableMetrics(hasTimeAxis, temperatureActive);

            await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

            var parameters = new DynamicParameters();
            SalesFilterSql.AddParameters(filter, parameters);

            var rowExpr = ResolveCrosstabDimensionSql(rowDim);
            var colExpr = ResolveCrosstabDimensionSql(colDim);
            var whereClause = SalesFilterSql.WhereClause(filter, "sw");
            var andClause = SalesFilterSql.AndClause(filter, "sw");

            // 期間内データが空の場合（latestWeek=null）は空マトリクスを返す（500ではなく200で空）
            var latestWeek = await QueryLatestWeekAsync(connection, filter, parameters, cancellationToken);
            if (!latestWeek.HasValue)
            {
                response = CrosstabMatrixBuilder.BuildEmpty(rowInfo, colInfo, availableMetrics);
                return response;
            }

            // フロー集計（期間内）。null/空ラベルは未設定として置換。
            // 注: GROUP BY は SELECT のエイリアス（row_key / col_key）を参照しても PostgreSQL では
            // 動作するが、CTE で前段に式を畳むことで読みやすさと SQL 実行計画の安定性を確保する。
            var rowKeyExpr = $"COALESCE(NULLIF({rowExpr}, ''), '{CrosstabMatrixBuilder.UnsetLabel}')";
            var colKeyExpr = $"COALESCE(NULLIF({colExpr}, ''), '{CrosstabMatrixBuilder.UnsetLabel}')";
            var flowSql = $"""
                WITH labeled AS (
                    SELECT {rowKeyExpr} AS row_key,
                           {colKeyExpr} AS col_key,
                           {SalesMetricSql.WeekQuantity}    AS week_quantity,
                           {SalesMetricSql.WeekAmount}      AS week_amount,
                           {SalesMetricSql.WeekGrossProfit} AS week_gross_profit,
                           sw.zainiti                       AS zainiti
                    FROM sales_weekly sw
                    {whereClause}
                )
                SELECT row_key,
                       col_key,
                       COALESCE(SUM(week_quantity), 0)::bigint    AS quantity,
                       COALESCE(SUM(week_amount), 0)::bigint      AS amount,
                       COALESCE(SUM(week_gross_profit), 0)::bigint AS gross_profit,
                       COALESCE(AVG(zainiti), 0)::float8          AS stock_days
                FROM labeled
                GROUP BY row_key, col_key;
                """;
            var flowRows = (await connection.QueryAsync<CrosstabFlowRow>(
                new CommandDefinition(flowSql, parameters, cancellationToken: cancellationToken))).ToList();

            // スナップショット集計（時間軸が含まれない場合のみ取得）。
            Dictionary<(string Row, string Col), CrosstabSnapshotRow> snapshotMap = new();
            if (!hasTimeAxis)
            {
                parameters.Add("latestWeek", latestWeek.Value);
                var snapshotSql = $"""
                    WITH labeled AS (
                        SELECT {rowKeyExpr} AS row_key,
                               {colKeyExpr} AS col_key,
                               sw.zaikosu               AS zaikosu,
                               sw.ruikei_uriage_count   AS ruikei_uriage_count,
                               sw.ruikei_nohin_count    AS ruikei_nohin_count
                        FROM sales_weekly sw
                        WHERE sw.import_date = @latestWeek{andClause}
                    )
                    SELECT row_key,
                           col_key,
                           COALESCE(SUM(zaikosu), 0)::bigint             AS stock,
                           COALESCE(SUM(ruikei_uriage_count), 0)::bigint AS cumulative_sales,
                           COALESCE(SUM(ruikei_nohin_count), 0)::bigint  AS cumulative_delivery
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

            response = CrosstabMatrixBuilder.Build(
                flowRows,
                snapshotMap,
                rowInfo,
                colInfo,
                hasTimeAxis,
                availableMetrics,
                latestWeek,
                rowDim,
                colDim,
                temperatureActive ? temperatureArea : null);

            return response;
        }
        finally
        {
            LogIfSlow(stopwatch, rowDim, colDim, filter,
                rowsCount: response?.RowLabels.Count ?? -1,
                colsCount: response?.ColumnLabels.Count ?? -1);
        }
    }

    /// <summary>
    /// クロス集計クエリの実行時間がしきい値を超えたら警告ログを出す。
    /// 本番ではクエリ最適化判断の入力情報として使う（フィルタ・行列ディメンションも含めて記録）。
    /// </summary>
    private void LogIfSlow(
        Stopwatch stopwatch,
        CrosstabDimension rowDim,
        CrosstabDimension colDim,
        SalesQueryFilter filter,
        int rowsCount,
        int colsCount)
    {
        stopwatch.Stop();
        var elapsedMs = stopwatch.ElapsedMilliseconds;
        if (elapsedMs <= CrosstabSlowQueryWarningThresholdMs)
        {
            return;
        }
        _logger.LogWarning(
            "Crosstab query slow: {ElapsedMs}ms row={Row} col={Col} rows={Rows} cols={Cols} "
            + "from={From} to={To} depts={DeptCount} biz={BizCount} seasons={SeasonCount} hinbans={HinbanCount}",
            elapsedMs,
            rowDim,
            colDim,
            rowsCount,
            colsCount,
            filter.From,
            filter.To,
            filter.Departments?.Length ?? 0,
            filter.BusinessTypes?.Length ?? 0,
            filter.Seasons?.Length ?? 0,
            filter.Hinbans?.Length ?? 0);
    }

    /// <summary>
    /// CrosstabDimension の SQL 式（GROUP BY と SELECT に使う1つの式）を解決する。
    /// 時間軸はクロス集計固有なのでここで直接定義し、カテゴリ系は
    /// <see cref="CategoryDimensionSqlMap"/> を参照する（SQL 式の二重メンテ防止）。
    /// </summary>
    private static string ResolveCrosstabDimensionSql(CrosstabDimension dim) => dim switch
    {
        CrosstabDimension.TimeYear =>
            "EXTRACT(YEAR FROM sw.import_date)::int::text",
        CrosstabDimension.TimeQuarter =>
            "EXTRACT(YEAR FROM sw.import_date)::int::text "
            + "|| '-Q' || EXTRACT(QUARTER FROM sw.import_date)::int::text",
        CrosstabDimension.TimeMonth =>
            "to_char(sw.import_date, 'YYYY-MM')",
        CrosstabDimension.CategoryDepartment => CategoryDimensionSqlMap["department"],
        CrosstabDimension.CategoryBusinessType => CategoryDimensionSqlMap["businessType"],
        CrosstabDimension.CategorySeason => CategoryDimensionSqlMap["season"],
        CrosstabDimension.CategoryHinban => CategoryDimensionSqlMap["hinban"],
        CrosstabDimension.CategoryProduct => CategoryDimensionSqlMap["product"],
        CrosstabDimension.CategoryColor => CategoryDimensionSqlMap["color"],
        CrosstabDimension.CategorySize => CategoryDimensionSqlMap["size"],
        CrosstabDimension.CategoryChohyoKubun => CategoryDimensionSqlMap["chohyoKubun"],
        CrosstabDimension.CategoryTanawari1 => CategoryDimensionSqlMap["tanawari1"],
        CrosstabDimension.CategoryTanawari2 => CategoryDimensionSqlMap["tanawari2"],
        CrosstabDimension.CategoryShohinKigo => CategoryDimensionSqlMap["shohinKigo"],
        _ => throw new AppException(ErrorCodes.UnknownDimension, 400),
    };

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

    /// <summary>
    /// BreakdownDimension の SQL 式（GroupBy / KeyExpr / LabelExpr）を解決する。
    /// 単品（Product）以外は <see cref="CategoryDimensionSqlMap"/> の式を3列とも同じに使う。
    /// </summary>
    private static (string GroupBy, string KeyExpr, string LabelExpr) ResolveDimension(
        BreakdownDimension dimension)
    {
        // 商品マスタの自然キー (gyotai × shohin_kigou × hinban) を含めて一意化することで
        // 同一 (hinban, tanpin) が複数業態で売られているケースの行衝突を防ぎ、
        // m_product との JOIN を 1 対 1 に保つ。表示ラベルは品番-単品（label 列）。
        if (dimension == BreakdownDimension.Product)
        {
            return (
                "sw.gyotai_code, sw.shohin_kigou, sw.hinban_code, sw.tanpin_code",
                "sw.gyotai_code || '|' || sw.shohin_kigou || '|' || sw.hinban_code || '|' || sw.tanpin_code",
                "sw.hinban_code || '-' || sw.tanpin_code");
        }

        // 共通の SQL 式マップを参照して GroupBy/KeyExpr/LabelExpr 3列とも同じ式を割り当てる。
        var key = dimension switch
        {
            BreakdownDimension.Department => "department",
            BreakdownDimension.BusinessType => "businessType",
            BreakdownDimension.Season => "season",
            BreakdownDimension.Color => "color",
            BreakdownDimension.Size => "size",
            BreakdownDimension.Hinban => "hinban",
            BreakdownDimension.ChohyoKubun => "chohyoKubun",
            BreakdownDimension.Tanawari1 => "tanawari1",
            BreakdownDimension.Tanawari2 => "tanawari2",
            BreakdownDimension.ShohinKigo => "shohinKigo",
            _ => throw new AppException(ErrorCodes.UnknownDimension, 400),
        };
        var expr = CategoryDimensionSqlMap[key];
        return (expr, expr, expr);
    }

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
        SalesMetric.Quantity => AggregateMath.Ratio(row.Quantity, row.TotalQuantity) * 100.0,
        SalesMetric.Amount => AggregateMath.Ratio(row.Amount, row.TotalAmount) * 100.0,
        SalesMetric.GrossProfit => AggregateMath.Ratio(row.GrossProfit, row.TotalGrossProfit) * 100.0,
        _ => 0.0,
    };

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

}
