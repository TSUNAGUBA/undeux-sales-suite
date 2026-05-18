namespace UndeuxSales.Infrastructure.Queries;

/// <summary>
/// 売上指標を算出する SQL 式断片を一元管理する。集計クエリ間での式の重複を排除し、
/// 売価・粗利の定義変更を1箇所に閉じる。テーブル別名は <c>sw</c> 固定。
/// </summary>
internal static class SalesMetricSql
{
    /// <summary>当週（月〜日）の売上数量。</summary>
    public const string WeekQuantity =
        "(sw.toshu_uriage_count1 + sw.toshu_uriage_count2 + sw.toshu_uriage_count3 "
        + "+ sw.toshu_uriage_count4 + sw.toshu_uriage_count5 + sw.toshu_uriage_count6 "
        + "+ sw.toshu_uriage_count7)";

    /// <summary>当週の売上金額（数量 × 売価）。桁あふれ防止のため bigint で計算する。</summary>
    public const string WeekAmount = WeekQuantity + "::bigint * sw.baika";

    /// <summary>当週の粗利（数量 × (売価 − 原価)）。</summary>
    public const string WeekGrossProfit = WeekQuantity + "::bigint * (sw.baika - sw.genka)";
}
