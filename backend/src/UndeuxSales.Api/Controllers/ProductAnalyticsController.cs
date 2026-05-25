using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Core;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>
/// 商品（商品マスタの product_id）を軸にした包括的な売上分析 API。
/// 期間内 KPI / 週次トレンド / SKU 別 / 取引先別 / 業態別の各観点を返す。
/// </summary>
[ApiController]
[Authorize]
[Route("api/product-analytics")]
public sealed class ProductAnalyticsController : ControllerBase
{
    private readonly ProductAnalyticsRepository _repository;

    public ProductAnalyticsController(ProductAnalyticsRepository repository)
        => _repository = repository;

    /// <summary>指定商品の包括的な売上分析を返す。商品が存在しない場合は 404。</summary>
    [HttpGet("{productId:guid}")]
    public async Task<ActionResult<ProductAnalyticsResponse>> Get(
        Guid productId,
        [FromQuery] SalesQueryFilter filter,
        CancellationToken cancellationToken)
    {
        var response = await _repository.GetAnalyticsAsync(productId, filter, cancellationToken);
        if (response is null)
        {
            throw new AppException(ErrorCodes.ProductNotFound, 404);
        }
        return response;
    }
}
