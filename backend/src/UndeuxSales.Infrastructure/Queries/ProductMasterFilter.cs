using Dapper;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>商品マスタ一覧の検索フィルタ。</summary>
public sealed class ProductMasterFilter
{
    /// <summary>商品名・商品記号・品番・ブランドに対するフリーテキスト検索（部分一致）。</summary>
    public string? Search { get; set; }

    /// <summary>業態コード（商品マスタ business_category_cd。複数選択時はいずれかに一致）。旧 UI 互換。</summary>
    public string[]? BusinessCategoryCds { get; set; }

    /// <summary>部門コード（商品マスタ division_cd。複数選択時はいずれかに一致）。旧 UI 互換。</summary>
    public int[]? DivisionCds { get; set; }

    /// <summary>ブランド（複数選択時はいずれかに一致）。</summary>
    public string[]? Brands { get; set; }

    /// <summary>担当者（複数選択時はいずれかに一致）。</summary>
    public string[]? Managers { get; set; }

    // ---- 全社サマリー踏襲フィルター（商品別分析で使用） ----
    // 業態は business_category_cd（＝sales の gyotai_code。同一コード体系）で直接フィルタする。
    // 部門・季節・棚割1・在日・期間は sales_weekly 実績の EXISTS で絞り込む
    // （指定条件・期間で売上のある商品のみ表示する）。商品マスタ画面（/product-master）は
    // これらを送らないため、従来挙動に影響しない（後方互換）。

    /// <summary>開始取込日（含む）。フロントは年度を 1/1〜12/31 に展開して渡す。</summary>
    public DateOnly? From { get; set; }

    /// <summary>終了取込日（含む）。</summary>
    public DateOnly? To { get; set; }

    /// <summary>部門コード（sales_weekly.department。いずれかに一致）。</summary>
    public string[]? Departments { get; set; }

    /// <summary>業態コード（sales の gyotai_code＝商品マスタ business_category_cd。いずれかに一致）。</summary>
    public string[]? BusinessTypes { get; set; }

    /// <summary>季節区分（sales_weekly.kisetsu。いずれかに一致）。</summary>
    public string[]? Seasons { get; set; }

    /// <summary>棚割1（sales_weekly.tanawari1。いずれかに一致）。</summary>
    public string[]? Tanawari1 { get; set; }

    /// <summary>平均在庫日数（在日）バケット（le30/d31to60/ge61。いずれかに一致）。</summary>
    public string[]? StockDaysBuckets { get; set; }
}

/// <summary>
/// <see cref="ProductMasterFilter"/> から SQL の WHERE 条件と Dapper パラメータを組み立てる。
/// パラメータ名は固定。条件のない要素はパラメータも条件も追加しない。
/// 全社サマリー踏襲フィルター（売上実績側）は <see cref="SalesFilterSql"/> を再利用する。
/// </summary>
internal static class ProductMasterFilterSql
{
    /// <summary>全社サマリー踏襲フィルター（売上実績側）を <see cref="SalesQueryFilter"/> として取り出す。</summary>
    private static SalesQueryFilter SalesView(ProductMasterFilter filter) => new()
    {
        From = filter.From,
        To = filter.To,
        Departments = filter.Departments,
        BusinessTypes = filter.BusinessTypes,
        Seasons = filter.Seasons,
        Tanawari1 = filter.Tanawari1,
        StockDaysBuckets = filter.StockDaysBuckets,
    };

    /// <summary>
    /// sales_weekly 実績の EXISTS で絞り込むべき条件があるか（期間・部門・季節・棚割1・在日のいずれか）。
    /// 業態は business_category_cd で直接絞るため含めない（業態のみ指定時は売上ゼロの商品も表示＝従来挙動）。
    /// </summary>
    private static bool HasSalesExistsFilter(ProductMasterFilter filter) =>
        filter.From.HasValue || filter.To.HasValue
        || filter.Departments is { Length: > 0 }
        || filter.Seasons is { Length: > 0 }
        || filter.Tanawari1 is { Length: > 0 }
        || filter.StockDaysBuckets is { Length: > 0 };

    /// <summary>期間レンジの妥当性を検証する（From &gt; To は 400）。</summary>
    public static void EnsureValid(ProductMasterFilter filter) => SalesView(filter).EnsureValid();

    public static void AddParameters(ProductMasterFilter filter, DynamicParameters parameters)
    {
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // LIKE 用にエスケープし、両端にワイルドカードを付与する。
            var escaped = filter.Search
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
            parameters.Add("searchPattern", $"%{escaped}%");
        }

        if (filter.BusinessCategoryCds is { Length: > 0 })
        {
            parameters.Add("businessCategoryCds", filter.BusinessCategoryCds);
        }

        if (filter.DivisionCds is { Length: > 0 })
        {
            parameters.Add("divisionCds", filter.DivisionCds);
        }

        if (filter.Brands is { Length: > 0 })
        {
            parameters.Add("brands", filter.Brands);
        }

        if (filter.Managers is { Length: > 0 })
        {
            parameters.Add("managers", filter.Managers);
        }

        // 全社サマリー踏襲フィルター（from/to/departments/businessTypes/seasons/tanawari1）。
        // StockDaysBuckets は述語に直接埋め込むためパラメータ化されない。
        SalesFilterSql.AddParameters(SalesView(filter), parameters);
    }

    public static string Conditions(ProductMasterFilter filter, string alias)
    {
        var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            conditions.Add(
                $"({alias}.product_name        ILIKE @searchPattern ESCAPE '\\' "
                + $"OR {alias}.product_sign     ILIKE @searchPattern ESCAPE '\\' "
                + $"OR {alias}.product_type_crd ILIKE @searchPattern ESCAPE '\\' "
                + $"OR COALESCE({alias}.brand, '') ILIKE @searchPattern ESCAPE '\\')");
        }

        if (filter.BusinessCategoryCds is { Length: > 0 })
        {
            conditions.Add($"{alias}.business_category_cd = ANY(@businessCategoryCds)");
        }

        // 業態（全社サマリー踏襲）。business_category_cd は sales の gyotai_code と同一コード体系。
        if (filter.BusinessTypes is { Length: > 0 })
        {
            conditions.Add($"{alias}.business_category_cd = ANY(@businessTypes)");
        }

        if (filter.DivisionCds is { Length: > 0 })
        {
            conditions.Add($"{alias}.division_cd = ANY(@divisionCds)");
        }

        if (filter.Brands is { Length: > 0 })
        {
            conditions.Add($"{alias}.brand = ANY(@brands)");
        }

        if (filter.Managers is { Length: > 0 })
        {
            conditions.Add($"{alias}.manager = ANY(@managers)");
        }

        // 期間・部門・季節・棚割1・在日は、自然キー（業態×記号×品番）で結合した
        // sales_weekly 実績の EXISTS で絞り込む（指定条件で売上のある商品のみ）。
        if (HasSalesExistsFilter(filter))
        {
            var salesConditions = SalesFilterSql.Conditions(SalesView(filter), "swf");
            var salesAnd = salesConditions.Length == 0 ? string.Empty : "AND " + salesConditions;
            conditions.Add($"""
                EXISTS (
                    SELECT 1
                    FROM sales_weekly swf
                    WHERE swf.gyotai_code  = {alias}.business_category_cd
                      AND swf.shohin_kigou = {alias}.product_sign
                      AND swf.hinban_code  = {alias}.product_type_crd
                      {salesAnd}
                )
                """);
        }

        return string.Join(" AND ", conditions);
    }

    public static string WhereClause(ProductMasterFilter filter, string alias)
    {
        var conditions = Conditions(filter, alias);
        return conditions.Length == 0 ? string.Empty : "WHERE " + conditions;
    }
}
