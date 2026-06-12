using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>
/// ランキング分析（単軸ランキング + 期間比較）を提供する。フロー指標は期間内集計、
/// 在庫指標は各期間の最新週スナップショット基準。順位・複合スコア・累積構成比・ABC ランクは
/// フロント側で本レスポンスの集計素材から算出する（対話的な並び替え・重み付けに即応するため）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/ranking")]
public sealed class RankingController : ControllerBase
{
    /// <summary>返却行数の既定上限（フロントは Top-N 表示で更に絞る）。</summary>
    private const int DefaultMaxRows = 500;

    private readonly SalesAnalyticsRepository _analyticsRepository;

    public RankingController(SalesAnalyticsRepository analyticsRepository)
        => _analyticsRepository = analyticsRepository;

    /// <summary>ランキング分析の集計素材を取得する。</summary>
    /// <param name="filter">主期間フィルタ（期間・部門・業態・季節・品番）。</param>
    /// <param name="dimension">集計軸（例 "department", "businessType", "hinban", "product"）。</param>
    /// <param name="compareFrom">比較期間の開始日。<paramref name="compareTo"/> と両方指定時のみ比較有効。</param>
    /// <param name="compareTo">比較期間の終了日。<paramref name="compareFrom"/> と両方指定時のみ比較有効。</param>
    /// <param name="limit">返却最大行数（未指定／0以下は既定値、上限はサーバ側でクランプ）。</param>
    /// <param name="cancellationToken">キャンセル用トークン。</param>
    [HttpGet]
    public Task<RankingResponse> Get(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string? dimension,
        [FromQuery] DateOnly? compareFrom,
        [FromQuery] DateOnly? compareTo,
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        var dim = RequestParsing.Dimension(dimension);

        // 比較期間は compareFrom / compareTo の両方が揃ったときのみ有効。
        // カテゴリ系フィルタ（部門・業態・季節・品番）は主期間と共有し、日付範囲のみ差し替える。
        SalesQueryFilter? comparison = null;
        if (compareFrom.HasValue && compareTo.HasValue)
        {
            comparison = new SalesQueryFilter
            {
                From = compareFrom,
                To = compareTo,
                Departments = filter.Departments,
                BusinessTypes = filter.BusinessTypes,
                Seasons = filter.Seasons,
                Hinbans = filter.Hinbans,
                ShohinKigos = filter.ShohinKigos,
            };
        }

        var maxRows = limit <= 0 ? DefaultMaxRows : limit;
        return _analyticsRepository.GetRankingAsync(filter, comparison, dim, maxRows, cancellationToken);
    }
}
