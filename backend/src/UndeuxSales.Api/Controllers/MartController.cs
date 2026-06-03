using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>
/// 分析 mart（スタースキーマ）の集計・再構築APIを提供する。
/// 既存の sales 系API（sales_weekly 直参照）に対し、別スキーマ mart を参照する追加系統。
/// docs/star-schema-design.md。
/// </summary>
[ApiController]
[Authorize]
[Route("api/mart")]
public sealed class MartController : ControllerBase
{
    private const int DefaultBreakdownLimit = 20;

    private readonly MartAnalyticsRepository _martRepository;

    public MartController(MartAnalyticsRepository martRepository)
        => _martRepository = martRepository;

    /// <summary>mart の構築状態（鮮度・行数・対象週範囲）を取得する。</summary>
    [HttpGet("status")]
    public Task<MartStatus> Status(CancellationToken cancellationToken)
        => _martRepository.GetStatusAsync(cancellationToken);

    /// <summary>全社サマリー（KPI＋週次トレンド）を mart から取得する。</summary>
    [HttpGet("summary")]
    public Task<MartSummaryResponse> Summary(
        [FromQuery] SalesQueryFilter filter, CancellationToken cancellationToken)
        => _martRepository.GetSummaryAsync(filter, cancellationToken);

    /// <summary>集計軸別の売上ランキングを mart から取得する。</summary>
    [HttpGet("breakdown")]
    public Task<MartBreakdownResponse> Breakdown(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string? dimension,
        [FromQuery] string? metric,
        [FromQuery] string? order,
        [FromQuery] int limit,
        CancellationToken cancellationToken)
        => _martRepository.GetBreakdownAsync(
            filter,
            dimension,
            RequestParsing.Metric(metric),
            RequestParsing.IsAscending(order),
            limit <= 0 ? DefaultBreakdownLimit : limit,
            cancellationToken);

    /// <summary>
    /// mart を sales_weekly + 商品マスタから全再構築する（public → mart のデータ移行）。
    /// mart は派生キャッシュであり、元データ（sales_weekly）も既存機能も壊さない冪等処理
    /// （DB側で advisory lock により直列化）のため、認証済みユーザーであれば実行できる。
    /// クラスの [Authorize] により認証（ログイン）は必須。
    /// </summary>
    [HttpPost("rebuild")]
    public Task<MartStatus> Rebuild(CancellationToken cancellationToken)
        => _martRepository.RebuildAsync(cancellationToken);
}
