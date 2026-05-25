using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>フィルタUIの選択肢を提供する。</summary>
[ApiController]
[Authorize]
[Route("api/filters")]
public sealed class FiltersController : ControllerBase
{
    private readonly MasterRepository _masterRepository;

    public FiltersController(MasterRepository masterRepository)
        => _masterRepository = masterRepository;

    /// <summary>部門・業態・季節・取込日（週）の選択肢一式を取得する。</summary>
    [HttpGet]
    public Task<FilterOptions> Get(CancellationToken cancellationToken)
        => _masterRepository.GetFilterOptionsAsync(cancellationToken);
}
