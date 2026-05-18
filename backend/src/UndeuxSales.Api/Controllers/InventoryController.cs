using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>在庫・発注分析（最新週スナップショット基準）を提供する。</summary>
[ApiController]
[Authorize]
[Route("api/inventory")]
public sealed class InventoryController : ControllerBase
{
    private readonly SalesAnalyticsRepository _analyticsRepository;

    public InventoryController(SalesAnalyticsRepository analyticsRepository)
        => _analyticsRepository = analyticsRepository;

    /// <summary>在庫・発注の主要KPIと部門別内訳を取得する。</summary>
    [HttpGet]
    public Task<InventoryResponse> Get(
        [FromQuery] SalesQueryFilter filter, CancellationToken cancellationToken)
        => _analyticsRepository.GetInventoryAsync(filter, cancellationToken);
}
