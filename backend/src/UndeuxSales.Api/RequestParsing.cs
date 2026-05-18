using UndeuxSales.Core;
using UndeuxSales.Core.Models;

namespace UndeuxSales.Api;

/// <summary>クエリ文字列のenum値を厳密に解析する。不正値は <see cref="AppException"/> を送出する。</summary>
public static class RequestParsing
{
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
