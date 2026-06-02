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

    /// <summary>業態コード（いずれかに一致）。</summary>
    public string[]? BusinessTypes { get; set; }

    /// <summary>季節区分（いずれかに一致）。</summary>
    public string[]? Seasons { get; set; }

    /// <summary>品番コード（いずれかに一致）。ドリルダウン時に設定される。</summary>
    public string[]? Hinbans { get; set; }

    /// <summary>棚割1（いずれかに一致）。NULL/空は <c>''</c> に正規化して比較する。</summary>
    public string[]? Tanawari1 { get; set; }

    /// <summary>
    /// 平均在庫日数（在日）のバケット（いずれかに一致＝OR）。
    /// 値: <c>le30</c>（30日以内）/ <c>d31to60</c>（31〜60日）/ <c>ge61</c>（61日以上）。
    /// 未知の値は無視する（古いクライアント互換のためのグレースフルデグラデーション）。
    /// </summary>
    public string[]? StockDaysBuckets { get; set; }

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

        if (filter.Tanawari1 is { Length: > 0 })
        {
            parameters.Add("tanawari1", filter.Tanawari1);
        }
    }

    /// <summary>
    /// 平均在庫日数（在日）バケットの SQL 述語。値はホワイトリスト照合（外部入力を SQL へ直接埋め込まない）。
    /// 未知のバケットは <c>null</c> を返し、条件から除外する。
    /// </summary>
    private static string? StockDaysBucketPredicate(string bucket, string alias) => bucket switch
    {
        "le30" => $"{alias}.zainiti <= 30",
        "d31to60" => $"{alias}.zainiti BETWEEN 31 AND 60",
        "ge61" => $"{alias}.zainiti >= 61",
        _ => null,
    };

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

        if (filter.Tanawari1 is { Length: > 0 })
        {
            conditions.Add($"COALESCE({alias}.tanawari1, '') = ANY(@tanawari1)");
        }

        // 平均在庫日数（在日）バケットは OR 連結。複数選択時はいずれかに該当すれば対象。
        if (filter.StockDaysBuckets is { Length: > 0 })
        {
            var predicates = filter.StockDaysBuckets
                .Select(b => StockDaysBucketPredicate(b, alias))
                .Where(p => p is not null)
                .Distinct()
                .ToList();
            if (predicates.Count > 0)
            {
                conditions.Add("(" + string.Join(" OR ", predicates) + ")");
            }
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
