using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>クロス集計（指定の集計単位での複数メトリクス）を提供する。</summary>
[ApiController]
[Authorize]
[Route("api/crosstab")]
public sealed class CrosstabController : ControllerBase
{
    private readonly SalesAnalyticsRepository _analyticsRepository;

    public CrosstabController(SalesAnalyticsRepository analyticsRepository)
        => _analyticsRepository = analyticsRepository;

    /// <summary>集計単位ごとに、売上数量・金額・粗利・構成比率・在日・在庫・消化率をまとめて返す。</summary>
    [HttpGet]
    public Task<CrosstabResponse> Get(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string? dimension,
        CancellationToken cancellationToken)
        => _analyticsRepository.GetCrosstabAsync(
            filter, RequestParsing.Dimension(dimension), cancellationToken);
}
