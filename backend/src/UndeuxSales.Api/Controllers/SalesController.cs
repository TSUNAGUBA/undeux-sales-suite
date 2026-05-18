using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>売上トレンド・集計軸別分析を提供する。</summary>
[ApiController]
[Authorize]
[Route("api/sales")]
public sealed class SalesController : ControllerBase
{
    private const int DefaultBreakdownLimit = 20;

    private readonly SalesAnalyticsRepository _analyticsRepository;

    public SalesController(SalesAnalyticsRepository analyticsRepository)
        => _analyticsRepository = analyticsRepository;

    /// <summary>売上トレンド（日次／週次）を取得する。</summary>
    [HttpGet("trend")]
    public Task<TrendResponse> Trend(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string? granularity,
        CancellationToken cancellationToken)
        => _analyticsRepository.GetTrendAsync(
            filter, RequestParsing.Granularity(granularity), cancellationToken);

    /// <summary>集計軸（部門・取引先・業態・季節・商品・カラー・サイズ）別の売上ランキングを取得する。</summary>
    [HttpGet("breakdown")]
    public Task<BreakdownResponse> Breakdown(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string? dimension,
        [FromQuery] string? metric,
        [FromQuery] string? order,
        [FromQuery] int limit,
        CancellationToken cancellationToken)
        => _analyticsRepository.GetBreakdownAsync(
            filter,
            RequestParsing.Dimension(dimension),
            RequestParsing.Metric(metric),
            RequestParsing.IsAscending(order),
            limit <= 0 ? DefaultBreakdownLimit : limit,
            cancellationToken);
}
