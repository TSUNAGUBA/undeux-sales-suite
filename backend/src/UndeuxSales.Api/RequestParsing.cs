using UndeuxSales.Core;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api;

/// <summary>クエリ文字列のenum値を厳密に解析する。不正値は <see cref="AppException"/> を送出する。</summary>
public static class RequestParsing
{
    /// <summary>
    /// クロス集計のディメンション表現（"time:year" / "category:department" 等）を
    /// <see cref="CrosstabDimension"/> に解析する。未指定・不正値は <see cref="AppException"/>（HTTP 400）。
    /// sales 系・mart 系のクロス集計コントローラで共有する。
    /// </summary>
    public static CrosstabDimension ParseCrosstabDimension(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppException(ErrorCodes.UnknownDimension, 400,
                $"{parameterName} が指定されていません。");
        }

        CrosstabDimension? dim = value switch
        {
            "time:year" => CrosstabDimension.TimeYear,
            "time:quarter" => CrosstabDimension.TimeQuarter,
            "time:month" => CrosstabDimension.TimeMonth,
            "category:department" => CrosstabDimension.CategoryDepartment,
            "category:businessType" => CrosstabDimension.CategoryBusinessType,
            "category:season" => CrosstabDimension.CategorySeason,
            "category:hinban" => CrosstabDimension.CategoryHinban,
            "category:product" => CrosstabDimension.CategoryProduct,
            "category:color" => CrosstabDimension.CategoryColor,
            "category:size" => CrosstabDimension.CategorySize,
            "category:chohyoKubun" => CrosstabDimension.CategoryChohyoKubun,
            "category:tanawari1" => CrosstabDimension.CategoryTanawari1,
            "category:tanawari2" => CrosstabDimension.CategoryTanawari2,
            "category:shohinKigo" => CrosstabDimension.CategoryShohinKigo,
            _ => null,
        };

        if (dim.HasValue)
        {
            return dim.Value;
        }

        throw new AppException(ErrorCodes.UnknownDimension, 400,
            $"{parameterName} '{value}' は不正です。");
    }

    /// <summary>集計軸を解析する。</summary>
    public static BreakdownDimension Dimension(string? value)
    {
        if (Enum.TryParse<BreakdownDimension>(value, ignoreCase: true, out var dimension)
            && Enum.IsDefined(dimension))
        {
            return dimension;
        }

        throw new AppException(ErrorCodes.UnknownDimension, 400,
            $"集計軸 '{value}' は不正です。");
    }

    /// <summary>売上指標を解析する（未指定時は既定値）。</summary>
    public static SalesMetric Metric(string? value, SalesMetric fallback = SalesMetric.Amount)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<SalesMetric>(value, ignoreCase: true, out var metric)
            && Enum.IsDefined(metric))
        {
            return metric;
        }

        throw new AppException(ErrorCodes.InvalidRequest, 400,
            $"指標 '{value}' は不正です。");
    }

    /// <summary>トレンド粒度を解析する（未指定時は週次）。</summary>
    public static TrendGranularity Granularity(
        string? value, TrendGranularity fallback = TrendGranularity.Weekly)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<TrendGranularity>(value, ignoreCase: true, out var granularity)
            && Enum.IsDefined(granularity))
        {
            return granularity;
        }

        throw new AppException(ErrorCodes.InvalidRequest, 400,
            $"粒度 '{value}' は不正です（daily / weekly）。");
    }

    /// <summary>商品並び替えキーを解析する（未指定時は売上金額）。</summary>
    public static ProductSortKey ProductSort(
        string? value, ProductSortKey fallback = ProductSortKey.SalesAmount)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (Enum.TryParse<ProductSortKey>(value, ignoreCase: true, out var sortKey)
            && Enum.IsDefined(sortKey))
        {
            return sortKey;
        }

        throw new AppException(ErrorCodes.InvalidRequest, 400,
            $"並び替えキー '{value}' は不正です。");
    }

    /// <summary>並び順を解析する。"asc" のみ昇順、それ以外（未指定含む）は降順。</summary>
    public static bool IsAscending(string? order)
        => string.Equals(order, "asc", StringComparison.OrdinalIgnoreCase);
}
