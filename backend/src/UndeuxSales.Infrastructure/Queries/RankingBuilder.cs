using UndeuxSales.Core.Models;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>Dapper マッピング用のランキングフロー行（キー別の数量・金額・粗利）。</summary>
internal sealed record RankingFlowRow(
    string Key, string Label, long Quantity, long Amount, long GrossProfit);

/// <summary>Dapper マッピング用のランキングスナップショット行（キー別の在庫・累計・在日・在庫金額）。</summary>
internal sealed record RankingSnapshotRow(
    string Key, long Stock, long CumulativeSales, long CumulativeDelivery, double StockDays,
    long StockValueCost);

/// <summary>1期間ぶんのランキング集計結果（キー別の集計累計 + 最新取込週）。</summary>
internal sealed record RankingPeriodResult(
    IReadOnlyDictionary<string, RankingAccumulator> ByKey,
    DateOnly? LatestWeek);

/// <summary>
/// ランキング1キーの可変集計。フロー（数量・金額・粗利）に加え、最新週スナップショット
/// （在庫・累計売上数・累計納品数・在日）を保持する。<see cref="HasSnapshot"/> が false の場合、
/// 在庫系指標は未取得（時間外／在庫行なし）として API では null に変換される。
/// </summary>
internal sealed class RankingAccumulator
{
    public string Label { get; set; } = string.Empty;
    public long Quantity { get; set; }
    public long Amount { get; set; }
    public long GrossProfit { get; set; }
    public bool HasSnapshot { get; set; }
    public long Stock { get; set; }
    public long CumulativeSales { get; set; }
    public long CumulativeDelivery { get; set; }
    public double StockDays { get; set; }
    /// <summary>最新週スナップショットの在庫金額（原価ベース＝在庫数 × 原価の合計）。残在庫金額。</summary>
    public long StockValueCost { get; set; }
}

/// <summary>
/// ランキングレスポンスの「組み立て」を担う共有ビルダー。SQL 生成・ディメンション解決は各リポジトリ
/// （<see cref="SalesAnalyticsRepository"/> / <see cref="MartAnalyticsRepository"/>）が担い、主期間・比較期間の
/// 集計（<see cref="RankingPeriodResult"/>）から、和集合・顕著性ソート・切り詰め・利用可能指標の決定を行う
/// （順位・複合スコア・ABC はフロント側の表示射影。SoT は集計値）。
/// </summary>
internal static class RankingBuilder
{
    /// <summary>主期間・比較期間の集計からレスポンスを構築する（和集合・顕著性ソート・切り詰め）。</summary>
    public static RankingResponse Build(
        BreakdownDimension dimension,
        RankingPeriodResult primary,
        RankingPeriodResult? comparison,
        int maxRows)
    {
        // 行キーの和集合（比較なしのときは主期間のキーのみ。比較ありは圏外転落キーも含める）。
        var keys = new HashSet<string>(primary.ByKey.Keys, StringComparer.Ordinal);
        if (comparison != null)
        {
            foreach (var k in comparison.ByKey.Keys)
            {
                keys.Add(k);
            }
        }

        // 顕著性 = 主期間と比較期間の売上金額の大きい方。切り詰め時に重要キーを優先的に残す。
        long Salience(string key)
        {
            long salience = 0;
            if (primary.ByKey.TryGetValue(key, out var p))
            {
                salience = p.Amount;
            }
            if (comparison != null
                && comparison.ByKey.TryGetValue(key, out var c)
                && c.Amount > salience)
            {
                salience = c.Amount;
            }
            return salience;
        }

        var orderedKeys = keys
            .OrderByDescending(Salience)
            .ThenBy(k => k, StringComparer.Ordinal)
            .ToList();
        var truncated = orderedKeys.Count > maxRows;
        if (truncated)
        {
            orderedKeys = orderedKeys.Take(maxRows).ToList();
        }

        var rows = new List<RankingRow>(orderedKeys.Count);
        foreach (var key in orderedKeys)
        {
            primary.ByKey.TryGetValue(key, out var p);
            RankingAccumulator? c = null;
            comparison?.ByKey.TryGetValue(key, out c);

            var label = p?.Label ?? c?.Label ?? key;
            rows.Add(new RankingRow(
                key,
                label,
                p != null ? ToValues(p) : null,
                c != null ? ToValues(c) : null));
        }

        // 並び替え・複合スコアに使える指標。スナップショット系は主期間に最新週がある場合のみ。
        var availableMetrics = new List<string> { "amount", "quantity", "grossProfit", "grossProfitRate" };
        if (primary.LatestWeek.HasValue)
        {
            availableMetrics.Add("sellThroughRate");
            availableMetrics.Add("stockDays");
            availableMetrics.Add("stock");
        }

        return new RankingResponse(
            dimension.ToString(),
            rows,
            primary.LatestWeek.HasValue ? primary.LatestWeek.Value.ToString("yyyy-MM-dd") : null,
            comparison?.LatestWeek is { } cw ? cw.ToString("yyyy-MM-dd") : null,
            availableMetrics,
            truncated);
    }

    /// <summary>集計累計を API 返却値に変換する（スナップショット無しは在庫系を null）。</summary>
    private static RankingMetricValues ToValues(RankingAccumulator a)
    {
        long? stock = a.HasSnapshot ? a.Stock : (long?)null;
        double? sellThroughRate = a.HasSnapshot && a.CumulativeDelivery != 0
            ? (double)a.CumulativeSales / a.CumulativeDelivery * 100.0
            : (double?)null;
        double? stockDays = a.HasSnapshot ? a.StockDays : (double?)null;
        long? stockValueCost = a.HasSnapshot ? a.StockValueCost : (long?)null;
        return new RankingMetricValues(
            a.Quantity, a.Amount, a.GrossProfit, stock, sellThroughRate, stockDays, stockValueCost);
    }
}
