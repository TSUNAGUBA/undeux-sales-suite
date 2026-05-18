using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>全社サマリー（KPI＋週次トレンド）を提供する。</summary>
[ApiController]
[Authorize]
[Route("api/summary")]
public sealed class SummaryController : ControllerBase
{
    private readonly SalesAnalyticsRepository _analyticsRepository;

    public SummaryController(SalesAnalyticsRepository analyticsRepository)
        => _analyticsRepository = analyticsRepository;

    /// <summary>主要KPIと週次売上トレンドを取得する。</summary>
    [HttpGet]
    public Task<SummaryResponse> Get(
        [FromQuery] SalesQueryFilter filter, CancellationToken cancellationToken)
        => _analyticsRepository.GetSummaryAsync(filter, cancellationToken);
}
