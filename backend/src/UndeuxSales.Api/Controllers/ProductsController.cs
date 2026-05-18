using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>商品別分析（売れ筋／死に筋ランキング）を提供する。</summary>
[ApiController]
[Authorize]
[Route("api/products")]
public sealed class ProductsController : ControllerBase
{
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 50;

    private readonly SalesAnalyticsRepository _analyticsRepository;

    public ProductsController(SalesAnalyticsRepository analyticsRepository)
        => _analyticsRepository = analyticsRepository;

    /// <summary>商品（品番・単品）別の売上・在庫一覧をページングで取得する。</summary>
    [HttpGet]
    public Task<ProductPage> Get(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string? sort,
        [FromQuery] string? order,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
        => _analyticsRepository.GetProductsAsync(
            filter,
            RequestParsing.ProductSort(sort),
            RequestParsing.IsAscending(order),
            page <= 0 ? DefaultPage : page,
            pageSize <= 0 ? DefaultPageSize : pageSize,
            cancellationToken);
}
