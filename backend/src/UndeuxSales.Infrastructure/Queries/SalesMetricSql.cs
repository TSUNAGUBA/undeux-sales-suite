namespace UndeuxSales.Infrastructure.Queries;

/// <summary>
/// 売上指標を算出する SQL 式断片を一元管理する。集計クエリ間での式の重複を排除し、
/// 売価・粗利の定義を1箇所に閉じる。テーブル別名は <c>sw</c> 固定。
/// </summary>
internal static class SalesMetricSql
{
    /// <summary>当週（月〜日）の売上数量。</summary>
    public const string WeekQuantity =
        "(sw.toshu_uriage_count1 + sw.toshu_uriage_count2 + sw.toshu_uriage_count3 "
        + "+ sw.toshu_uriage_count4 + sw.toshu_uriage_count5 + sw.toshu_uriage_count6 "
        + "+ sw.toshu_uriage_count7)";

    /// <summary>数量式から売上金額式を作る（数量 × 売価。桁あふれ防止のため bigint 計算）。</summary>
    public static string Amount(string quantityExpression)
        => $"{quantityExpression}::bigint * sw.baika";

    /// <summary>数量式から粗利式を作る（数量 × (売価 − 原価)）。</summary>
    public static string GrossProfit(string quantityExpression)
        => $"{quantityExpression}::bigint * (sw.baika - sw.genka)";

    /// <summary>指定の日次列（1〜7）の売上数量式。</summary>
    public static string DailyQuantity(int dayIndex)
        => $"sw.toshu_uriage_count{dayIndex}";

    /// <summary>当週の売上金額式。</summary>
    public static readonly string WeekAmount = Amount(WeekQuantity);

    /// <summary>当週の粗利式。</summary>
    public static readonly string WeekGrossProfit = GrossProfit(WeekQuantity);
}
