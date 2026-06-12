namespace UndeuxSales.Infrastructure.Queries;

/// <summary>
/// 集計値の共通算術ヘルパー。各リポジトリに重複していた同一実装の集約（SoT）。
/// </summary>
internal static class AggregateMath
{
    /// <summary>
    /// 分子÷分母の比率（<b>0..1</b>、分母0は0＝ゼロ除算の防止）。粗利率・消化率の共通式。
    /// 返却モデル（KPI・商品行など）は sales 系と mart 系で共有され、フロントは比率（0..1）を
    /// 受け取る <c>formatRatioAsPercent</c> で描画する契約のため、ここでは ×100 しない。
    /// 構成比（%）が必要な箇所は呼び出し側で ×100 する。
    /// </summary>
    public static double Ratio(long numerator, long denominator)
        => denominator == 0 ? 0 : (double)numerator / denominator;
}
