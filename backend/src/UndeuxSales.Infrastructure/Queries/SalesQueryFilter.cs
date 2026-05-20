using Dapper;
using UndeuxSales.Core;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>売上分析クエリの共通フィルタ。期間は取込日（週）基準で適用される。</summary>
public sealed class SalesQueryFilter
{
    /// <summary>開始取込日（含む）。</summary>
    public DateOnly? From { get; set; }

    /// <summary>終了取込日（含む）。</summary>
    public DateOnly? To { get; set; }

    /// <summary>部門コード（いずれかに一致）。</summary>
    public string[]? Departments { get; set; }

    /// <summary>取引先コード（いずれかに一致）。</summary>
    public string[]? Customers { get; set; }

    /// <summary>業態コード（いずれかに一致）。</summary>
    public string[]? BusinessTypes { get; set; }

    /// <summary>季節区分（いずれかに一致）。</summary>
    public string[]? Seasons { get; set; }

    /// <summary>品番コード（いずれかに一致）。ドリルダウン時に設定される。</summary>
    public string[]? Hinbans { get; set; }

    /// <summary>フィルタの妥当性を検証する。不正な場合は <see cref="AppException"/> を送出する。</summary>
    public void EnsureValid()
    {
        if (From.HasValue && To.HasValue && From.Value > To.Value)
        {
            throw new AppException(ErrorCodes.InvalidDateRange, 400);
        }
    }
}

/// <summary>
/// <see cref="SalesQueryFilter"/> から SQL の WHERE 条件と Dapper パラメータを組み立てる。
/// パラメータ名は固定のため、複数の別名で条件文を生成しつつパラメータ登録は1度だけ行う。
/// </summary>
internal static class SalesFilterSql
{
    /// <summary>Dapper パラメータを登録する（1クエリにつき1度だけ呼ぶ）。</summary>
    public static void AddParameters(SalesQueryFilter filter, DynamicParameters parameters)
    {
        if (filter.From.HasValue)
        {
            parameters.Add("from", filter.From.Value);
        }

        if (filter.To.HasValue)
        {
            parameters.Add("to", filter.To.Value);
        }

        if (filter.Departments is { Length: > 0 })
        {
            parameters.Add("departments", filter.Departments);
        }

        if (filter.Customers is { Length: > 0 })
        {
            parameters.Add("customers", filter.Customers);
        }

        if (filter.BusinessTypes is { Length: > 0 })
        {
            parameters.Add("businessTypes", filter.BusinessTypes);
        }

        if (filter.Seasons is { Length: > 0 })
        {
            parameters.Add("seasons", filter.Seasons);
        }

        if (filter.Hinbans is { Length: > 0 })
        {
            parameters.Add("hinbans", filter.Hinbans);
        }
    }

    /// <summary>条件式を <c>AND</c> 連結で返す（条件がなければ空文字）。</summary>
    public static string Conditions(SalesQueryFilter filter, string alias)
    {
        var conditions = new List<string>();

        if (filter.From.HasValue)
        {
            conditions.Add($"{alias}.import_date >= @from");
        }

        if (filter.To.HasValue)
        {
            conditions.Add($"{alias}.import_date <= @to");
        }

        if (filter.Departments is { Length: > 0 })
        {
            conditions.Add($"{alias}.department = ANY(@departments)");
        }

        if (filter.Customers is { Length: > 0 })
        {
            conditions.Add($"{alias}.customer_code = ANY(@customers)");
        }

        if (filter.BusinessTypes is { Length: > 0 })
        {
            conditions.Add($"{alias}.gyotai_code = ANY(@businessTypes)");
        }

        if (filter.Seasons is { Length: > 0 })
        {
            conditions.Add($"{alias}.kisetsu = ANY(@seasons)");
        }

        if (filter.Hinbans is { Length: > 0 })
        {
            conditions.Add($"{alias}.hinban_code = ANY(@hinbans)");
        }

        return string.Join(" AND ", conditions);
    }

    /// <summary><c>WHERE ...</c> 句を返す（条件がなければ空文字）。</summary>
    public static string WhereClause(SalesQueryFilter filter, string alias)
    {
        var conditions = Conditions(filter, alias);
        return conditions.Length == 0 ? string.Empty : "WHERE " + conditions;
    }

    /// <summary>既存の WHERE に続けて連結する <c>AND ...</c> を返す（条件がなければ空文字）。</summary>
    public static string AndClause(SalesQueryFilter filter, string alias)
    {
        var conditions = Conditions(filter, alias);
        return conditions.Length == 0 ? string.Empty : " AND " + conditions;
    }
}
