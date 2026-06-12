using Dapper;
using UndeuxSales.Core;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>
/// 商品導入管理（mart）の検索条件。期間は導入日（<c>dim_sku.attributes->>'donyu'</c>）基準で、
/// 売上の取込週ではない点が <see cref="SalesQueryFilter"/> と異なる。
/// </summary>
public sealed class MartIntroductionQuery
{
    /// <summary>業態コード（いずれかに一致）。タブUIからは単一値が渡る。</summary>
    public string[]? BusinessTypes { get; set; }

    /// <summary>部門コード（いずれかに一致）。</summary>
    public string[]? Departments { get; set; }

    /// <summary>ブランド（いずれかに一致）。</summary>
    public string[]? Brands { get; set; }

    /// <summary>担当者（いずれかに一致）。</summary>
    public string[]? Managers { get; set; }

    /// <summary>服種＝品番CD（いずれかに一致）。</summary>
    public string[]? Hinbans { get; set; }

    /// <summary>キーワード（品番・記号・商品名・ブランド・担当者の部分一致）。</summary>
    public string? Keyword { get; set; }

    /// <summary>導入日の開始（含む）。指定時、導入日未設定の商品は対象外。</summary>
    public DateOnly? DonyuFrom { get; set; }

    /// <summary>導入日の終了（含む）。指定時、導入日未設定の商品は対象外。</summary>
    public DateOnly? DonyuTo { get; set; }

    /// <summary>フィルタの妥当性を検証する。不正な場合は <see cref="AppException"/> を送出する。</summary>
    public void EnsureValid()
    {
        if (DonyuFrom.HasValue && DonyuTo.HasValue && DonyuFrom.Value > DonyuTo.Value)
        {
            throw new AppException(ErrorCodes.InvalidDateRange, 400);
        }
    }
}

/// <summary>
/// <see cref="MartIntroductionQuery"/> から SQL の WHERE 条件と Dapper パラメータを組み立てる。
/// 商品次元のエイリアスは <c>dp</c>、SKU 集約（導入日 <c>donyu</c> を持つ）のエイリアスは <c>sku</c> 固定。
/// 導入日は YYYYMMDD 文字列のまま比較する（辞書順＝日付順。<see cref="SalesQueryFilter"/> の
/// 取込週 from/to とは別概念）。
/// </summary>
internal static class MartIntroductionFilterSql
{
    public static void AddParameters(MartIntroductionQuery query, DynamicParameters parameters)
    {
        if (query.BusinessTypes is { Length: > 0 }) parameters.Add("businessTypes", query.BusinessTypes);
        if (query.Departments is { Length: > 0 }) parameters.Add("departments", query.Departments);
        if (query.Brands is { Length: > 0 }) parameters.Add("brands", query.Brands);
        if (query.Managers is { Length: > 0 }) parameters.Add("managers", query.Managers);
        if (query.Hinbans is { Length: > 0 }) parameters.Add("hinbans", query.Hinbans);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            // LIKE 用にエスケープし、両端にワイルドカードを付与する（ProductMasterFilterSql と同手順）。
            var escaped = query.Keyword
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
            parameters.Add("keywordPattern", $"%{escaped}%");
        }

        if (query.DonyuFrom.HasValue) parameters.Add("donyuFrom", query.DonyuFrom.Value.ToString("yyyyMMdd"));
        if (query.DonyuTo.HasValue) parameters.Add("donyuTo", query.DonyuTo.Value.ToString("yyyyMMdd"));
    }

    /// <summary>
    /// 商品次元（dp）に対する条件のみを返す。集約（SKU・フロー）の<b>前</b>に商品集合を
    /// 絞り込むための条件で、全表集約を避ける（導入日条件は集約後の <see cref="DonyuConditions"/>）。
    /// </summary>
    public static string ProductConditions(MartIntroductionQuery query)
    {
        var conditions = new List<string>();

        if (query.BusinessTypes is { Length: > 0 }) conditions.Add("dp.channel_code = ANY(@businessTypes)");
        if (query.Departments is { Length: > 0 }) conditions.Add("dp.department_code = ANY(@departments)");
        if (query.Brands is { Length: > 0 }) conditions.Add("dp.brand = ANY(@brands)");
        if (query.Managers is { Length: > 0 }) conditions.Add("dp.manager = ANY(@managers)");
        if (query.Hinbans is { Length: > 0 }) conditions.Add("dp.product_code = ANY(@hinbans)");

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            conditions.Add(
                "(dp.product_code ILIKE @keywordPattern ESCAPE '\\' "
                + "OR dp.product_sign ILIKE @keywordPattern ESCAPE '\\' "
                + "OR COALESCE(dp.product_name, '') ILIKE @keywordPattern ESCAPE '\\' "
                + "OR COALESCE(dp.brand, '') ILIKE @keywordPattern ESCAPE '\\' "
                + "OR COALESCE(dp.manager, '') ILIKE @keywordPattern ESCAPE '\\')");
        }

        return string.Join(" AND ", conditions);
    }

    /// <summary>商品次元（dp）条件の <c>WHERE ...</c> 句（条件がなければ空文字）。</summary>
    public static string ProductWhereClause(MartIntroductionQuery query)
    {
        var conditions = ProductConditions(query);
        return conditions.Length == 0 ? string.Empty : "WHERE " + conditions;
    }

    /// <summary>
    /// 導入日（SKU 集約後の <c>sku.donyu</c>）に対する条件のみを返す。
    /// 導入日未設定（NULL）の商品は範囲指定時に対象外となる（比較結果が NULL）。
    /// </summary>
    public static string DonyuConditions(MartIntroductionQuery query)
    {
        var conditions = new List<string>();
        if (query.DonyuFrom.HasValue) conditions.Add("sku.donyu >= @donyuFrom");
        if (query.DonyuTo.HasValue) conditions.Add("sku.donyu <= @donyuTo");
        return string.Join(" AND ", conditions);
    }

    /// <summary>導入日条件の <c>WHERE ...</c> 句（条件がなければ空文字）。</summary>
    public static string DonyuWhereClause(MartIntroductionQuery query)
    {
        var conditions = DonyuConditions(query);
        return conditions.Length == 0 ? string.Empty : "WHERE " + conditions;
    }
}
