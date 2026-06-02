using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Core;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>
/// クロス集計（行×列マトリクス）を提供する。フロー指標は期間内集計、在庫指標は最新週スナップショット基準。
/// 時間軸が行・列のいずれかに含まれる場合、在庫系メトリクスは null となる。
/// </summary>
[ApiController]
[Authorize]
[Route("api/crosstab")]
public sealed class CrosstabController : ControllerBase
{
    private readonly SalesAnalyticsRepository _analyticsRepository;

    public CrosstabController(SalesAnalyticsRepository analyticsRepository)
        => _analyticsRepository = analyticsRepository;

    /// <summary>クロス集計マトリクスを取得する。</summary>
    /// <param name="filter">期間・部門・業態・季節・品番・棚割1・平均在庫日数フィルタ。</param>
    /// <param name="rowDimension">行ディメンション（例 "time:year", "category:businessType"）。</param>
    /// <param name="columnDimension">列ディメンション（同上）。</param>
    /// <param name="temperatureArea">
    /// 気温メトリクスのエリア種別（"standard"=東京 / "cold"=札幌 / "warm"=那覇）。
    /// 指定があり、かつ行・列のいずれかが時間軸のとき、気温系メトリクスを利用可能にする。
    /// 不正値は無視する（気温メトリクスを提供しない）。
    /// </param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    [HttpGet]
    public Task<CrosstabMatrixResponse> Get(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string? rowDimension,
        [FromQuery] string? columnDimension,
        [FromQuery] string? temperatureArea,
        CancellationToken cancellationToken)
    {
        var rowDim = ParseCrosstabDimension(rowDimension, "rowDimension");
        var colDim = ParseCrosstabDimension(columnDimension, "columnDimension");
        var area = ClimateModel.ParseArea(temperatureArea);
        return _analyticsRepository.GetCrosstabMatrixAsync(filter, rowDim, colDim, area, cancellationToken);
    }

    /// <summary>
    /// フロント表現の文字列（"time:year" / "category:department" 等）を <see cref="CrosstabDimension"/> に解釈する。
    /// 解析できない場合は <see cref="AppException"/>（HTTP 400）を送出する。
    /// </summary>
    private static CrosstabDimension ParseCrosstabDimension(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppException(ErrorCodes.UnknownDimension, 400,
                $"{parameterName} が指定されていません。");
        }

        var dim = value switch
        {
            "time:year" => CrosstabDimension.TimeYear,
            "time:quarter" => CrosstabDimension.TimeQuarter,
            "time:month" => CrosstabDimension.TimeMonth,
            "category:department" => CrosstabDimension.CategoryDepartment,
            "category:businessType" => CrosstabDimension.CategoryBusinessType,
            "category:season" => CrosstabDimension.CategorySeason,
            "category:hinban" => CrosstabDimension.CategoryHinban,
            "category:product" => CrosstabDimension.CategoryProduct,
            "category:color" => CrosstabDimension.CategoryColor,
            "category:size" => CrosstabDimension.CategorySize,
            "category:chohyoKubun" => CrosstabDimension.CategoryChohyoKubun,
            "category:tanawari1" => CrosstabDimension.CategoryTanawari1,
            "category:tanawari2" => CrosstabDimension.CategoryTanawari2,
            "category:shohinKigo" => CrosstabDimension.CategoryShohinKigo,
            _ => (CrosstabDimension?)null,
        };

        if (dim.HasValue)
        {
            return dim.Value;
        }

        throw new AppException(ErrorCodes.UnknownDimension, 400,
            $"{parameterName} '{value}' は不正です。");
    }
}
