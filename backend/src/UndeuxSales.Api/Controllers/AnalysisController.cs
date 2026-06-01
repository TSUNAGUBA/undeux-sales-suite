using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Core;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>
/// 散布図・単回帰／重回帰シミュレーションの集計素材を提供する。
/// 回帰係数・予測・象限分類はフロント側の表示射影とし、本 API は素材のみ返す。
/// </summary>
[ApiController]
[Authorize]
[Route("api/analysis")]
public sealed class AnalysisController : ControllerBase
{
    private readonly AnalysisRepository _repository;

    public AnalysisController(AnalysisRepository repository)
        => _repository = repository;

    /// <summary>
    /// 週次系列（売上フロー指標 + その週・エリアの標準気温）を返す。
    /// 散布図（気温×売上数量）と重回帰シミュレーションの素材。
    /// </summary>
    /// <param name="filter">期間・部門・業態・季節・品番・棚割1・平均在庫日数フィルタ。</param>
    /// <param name="area">エリア種別（"standard"=東京 / "cold"=札幌 / "warm"=那覇。未指定/不正は標準）。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    [HttpGet("weekly-series")]
    public Task<WeeklySeriesResponse> GetWeeklySeries(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string? area,
        CancellationToken cancellationToken)
    {
        var resolved = ClimateModel.ParseArea(area) ?? TemperatureArea.Standard;
        return _repository.GetWeeklySeriesAsync(filter, resolved, cancellationToken);
    }

    /// <summary>
    /// 消化率×値引き率の散布図素材（型番単位）を返す。値引き率はマスタ定価が必要なため、
    /// マスタ未登録の型番は対象外。
    /// </summary>
    /// <param name="filter">共通フィルタ。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    [HttpGet("markdown")]
    public Task<MarkdownScatterResponse> GetMarkdown(
        [FromQuery] SalesQueryFilter filter,
        CancellationToken cancellationToken)
        => _repository.GetMarkdownScatterAsync(filter, cancellationToken);
}
