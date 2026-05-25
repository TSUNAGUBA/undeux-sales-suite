using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Core;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>
/// 商品マスタ（m_product / m_product_sku）の参照 API。
/// カード型UI 用の一覧、フィルタ選択肢、商品詳細を提供する。
/// </summary>
[ApiController]
[Authorize]
[Route("api/product-master")]
public sealed class ProductMasterController : ControllerBase
{
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 24;

    private readonly ProductMasterRepository _repository;

    public ProductMasterController(ProductMasterRepository repository)
        => _repository = repository;

    /// <summary>商品マスタ専用のフィルタ選択肢（業態・部門・ブランド・担当者）。</summary>
    [HttpGet("options")]
    public Task<MasterFilterOptions> GetOptions(CancellationToken cancellationToken)
        => _repository.GetFilterOptionsAsync(cancellationToken);

    /// <summary>商品マスタの一覧（カード表示向けの集計済みサマリ）をページングで返す。</summary>
    [HttpGet]
    public Task<MasterProductPage> Get(
        [FromQuery] ProductMasterFilter filter,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
        => _repository.GetProductsAsync(
            filter,
            page <= 0 ? DefaultPage : page,
            pageSize <= 0 ? DefaultPageSize : pageSize,
            cancellationToken);

    /// <summary>商品マスタの詳細（親 + SKU 一覧 + 画像）を返す。</summary>
    [HttpGet("{productId:guid}")]
    public async Task<ActionResult<MasterProductDetail>> GetById(
        Guid productId, CancellationToken cancellationToken)
    {
        var detail = await _repository.GetProductDetailAsync(productId, cancellationToken);
        if (detail is null)
        {
            throw new AppException(ErrorCodes.ProductNotFound, 404);
        }
        return detail;
    }
}
