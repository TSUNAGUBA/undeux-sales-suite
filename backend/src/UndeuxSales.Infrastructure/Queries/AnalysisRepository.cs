using Dapper;
using UndeuxSales.Core;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>
/// 散布図・単回帰／重回帰シミュレーションの集計素材を提供するリポジトリ。
/// <para>
/// 回帰係数・予測・象限分類はユーザーが対話的に変える「表示射影」であるため、ここでは
/// 集計素材（週次系列・型番別の消化率/値引き率）のみを返し、回帰・予測はフロント側で算出する
/// （ランキングの順位・複合スコアと同じ思想。SoT は <c>sales_weekly</c> と標準気候モデル）。
/// </para>
/// </summary>
public sealed class AnalysisRepository
{
    /// <summary>消化率×値引き率 散布図の最大点数（読みやすさのための上限）。</summary>
    private const int MaxMarkdownPoints = 500;

    private readonly IDbConnectionFactory _connectionFactory;

    public AnalysisRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        DapperConfiguration.Initialize();
    }

    /// <summary>
    /// 週次系列（売上フロー指標 + その週・エリアの標準気温）を取得する。
    /// 散布図（気温×売上数量）と重回帰シミュレーションの素材。気温は <see cref="ClimateModel"/> 由来。
    /// </summary>
    public async Task<WeeklySeriesResponse> GetWeeklySeriesAsync(
        SalesQueryFilter filter,
        TemperatureArea area,
        CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);

        var sql = $"""
            SELECT sw.import_date AS week,
                   COALESCE(SUM({SalesMetricSql.WeekQuantity}), 0)::bigint    AS quantity,
                   COALESCE(SUM({SalesMetricSql.WeekAmount}), 0)::bigint      AS amount,
                   COALESCE(SUM({SalesMetricSql.WeekGrossProfit}), 0)::bigint AS gross_profit
            FROM sales_weekly sw
            {SalesFilterSql.WhereClause(filter, "sw")}
            GROUP BY sw.import_date
            ORDER BY sw.import_date;
            """;

        var rows = (await connection.QueryAsync<WeeklyFlowRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        var points = rows
            .Select(r =>
            {
                var temp = ClimateModel.Week(area, r.Week);
                return new WeeklySeriesPoint(
                    r.Week, r.Quantity, r.Amount, r.GrossProfit,
                    Round1(temp.Average), Round1(temp.Maximum), Round1(temp.Minimum));
            })
            .ToList();

        return new WeeklySeriesResponse(
            area.ToString().ToLowerInvariant(),
            ClimateModel.CityName(area),
            points);
    }

    /// <summary>
    /// 消化率×値引き率の散布図素材（型番＝業態×記号×品番 単位）を取得する。
    /// 値引き率はマスタ定価（m_product_sku.sales_price）が必要なため、マスタ未登録の型番は対象外。
    /// </summary>
    public async Task<MarkdownScatterResponse> GetMarkdownScatterAsync(
        SalesQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        filter.EnsureValid();
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        SalesFilterSql.AddParameters(filter, parameters);
        parameters.Add("limit", MaxMarkdownPoints);

        // 期間内フロー数量、最新週スナップショット（平均売価・累計）、マスタ定価を結合する。
        // 値引き率 = (1 − 平均売価 ÷ 定価) ×100 を 0..100 にクランプ。消化率 = 累計売上数 ÷ 累計納品数。
        var sql = $"""
            WITH latest AS (
                SELECT MAX(sw.import_date) AS w
                FROM sales_weekly sw
                {SalesFilterSql.WhereClause(filter, "sw")}
            ),
            flow AS (
                SELECT sw.gyotai_code, sw.shohin_kigou, sw.hinban_code,
                       COALESCE(SUM({SalesMetricSql.WeekQuantity}), 0)::bigint AS quantity
                FROM sales_weekly sw
                {SalesFilterSql.WhereClause(filter, "sw")}
                GROUP BY sw.gyotai_code, sw.shohin_kigou, sw.hinban_code
            ),
            snap AS (
                SELECT sw.gyotai_code, sw.shohin_kigou, sw.hinban_code,
                       AVG(NULLIF(sw.baika, 0))::float8                 AS avg_baika,
                       COALESCE(SUM(sw.ruikei_uriage_count), 0)::bigint AS cum_sales,
                       COALESCE(SUM(sw.ruikei_nohin_count), 0)::bigint  AS cum_delivery
                FROM sales_weekly sw
                WHERE sw.import_date = (SELECT w FROM latest){SalesFilterSql.AndClause(filter, "sw")}
                GROUP BY sw.gyotai_code, sw.shohin_kigou, sw.hinban_code
            ),
            list AS (
                SELECT mp.business_category_cd, mp.product_sign, mp.product_type_crd,
                       AVG(msk.sales_price)::float8 AS list_price
                FROM m_product mp
                JOIN m_product_sku msk ON msk.product_id = mp.product_id
                WHERE msk.sales_price > 0
                GROUP BY mp.business_category_cd, mp.product_sign, mp.product_type_crd
            )
            SELECT snap.gyotai_code || '|' || snap.shohin_kigou || '|' || snap.hinban_code AS key,
                   snap.hinban_code  AS label,
                   snap.gyotai_code  AS business_type,
                   CASE WHEN snap.cum_delivery > 0
                        THEN snap.cum_sales::float8 / snap.cum_delivery * 100.0
                        ELSE 0 END   AS sell_through_rate,
                   GREATEST(0, LEAST(100, (1 - snap.avg_baika / list.list_price) * 100.0)) AS markdown_rate,
                   COALESCE(flow.quantity, 0)::bigint AS quantity
            FROM snap
            JOIN list
                ON list.business_category_cd = snap.gyotai_code
               AND list.product_sign         = snap.shohin_kigou
               AND list.product_type_crd      = snap.hinban_code
            LEFT JOIN flow
                ON flow.gyotai_code  = snap.gyotai_code
               AND flow.shohin_kigou = snap.shohin_kigou
               AND flow.hinban_code  = snap.hinban_code
            WHERE list.list_price > 0
              AND snap.avg_baika IS NOT NULL
            ORDER BY quantity DESC, key
            LIMIT @limit;
            """;

        var latestWeek = await connection.ExecuteScalarAsync<DateOnly?>(
            new CommandDefinition(
                $"SELECT MAX(sw.import_date) FROM sales_weekly sw {SalesFilterSql.WhereClause(filter, "sw")};",
                parameters, cancellationToken: cancellationToken));

        var points = (await connection.QueryAsync<MarkdownScatterPoint>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).ToList();

        return new MarkdownScatterResponse(
            latestWeek?.ToString("yyyy-MM-dd"),
            points);
    }

    private static double Round1(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private sealed record WeeklyFlowRow(DateOnly Week, long Quantity, long Amount, long GrossProfit);
}
