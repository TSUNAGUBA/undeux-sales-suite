using Dapper;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>商品マスタ一覧の検索フィルタ。</summary>
public sealed class ProductMasterFilter
{
    /// <summary>商品名・商品記号・品番・ブランドに対するフリーテキスト検索（部分一致）。</summary>
    public string? Search { get; set; }

    /// <summary>業態コード（複数選択時はいずれかに一致）。</summary>
    public string[]? BusinessCategoryCds { get; set; }

    /// <summary>部門コード（複数選択時はいずれかに一致）。</summary>
    public int[]? DivisionCds { get; set; }

    /// <summary>ブランド（複数選択時はいずれかに一致）。</summary>
    public string[]? Brands { get; set; }

    /// <summary>担当者（複数選択時はいずれかに一致）。</summary>
    public string[]? Managers { get; set; }
}

/// <summary>
/// <see cref="ProductMasterFilter"/> から SQL の WHERE 条件と Dapper パラメータを組み立てる。
/// パラメータ名は固定。条件のない要素はパラメータも条件も追加しない。
/// </summary>
internal static class ProductMasterFilterSql
{
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

        return string.Join(" AND ", conditions);
    }

    public static string WhereClause(ProductMasterFilter filter, string alias)
    {
        var conditions = Conditions(filter, alias);
        return conditions.Length == 0 ? string.Empty : "WHERE " + conditions;
    }
}
