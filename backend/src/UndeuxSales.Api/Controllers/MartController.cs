using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MartController> _logger;

    public MartController(
        MartAnalyticsRepository martRepository,
        IServiceScopeFactory scopeFactory,
        ILogger<MartController> logger)
    {
        _martRepository = martRepository;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>mart の構築状態（再構築の進捗・鮮度・行数・対象週範囲）を取得する。</summary>
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
    /// mart の全再構築を「バックグラウンドで開始」する（public → mart のデータ移行）。
    /// 約160万行の集約は数十秒〜数分かかり、同期実行ではリバースプロキシのタイムアウトを
    /// 超えるため、本エンドポイントは即時に現在の状態（running）を返し、実処理は
    /// バックグラウンドで実行する。フロントは GET /api/mart/status を
    /// running / completed / failed でポーリングする。
    /// mart は派生キャッシュで元データを壊さないため、認証済みユーザーであれば実行できる。
    /// </summary>
    [HttpPost("rebuild")]
    public async Task<MartStatus> Rebuild(CancellationToken cancellationToken)
    {
        var started = await _martRepository.TryStartRebuildAsync(cancellationToken);
        if (started)
        {
            // リクエストのライフサイクルから切り離してバックグラウンド実行する。
            // scoped 依存（リポジトリ）を安全に使うため、専用の DI スコープを作成する。
            _ = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<MartAnalyticsRepository>();
                await repository.RunRebuildCoreAsync(CancellationToken.None);
            });
            _logger.LogInformation("mart 再構築をバックグラウンドで開始しました。");
        }
        else
        {
            _logger.LogInformation("mart 再構築は既に実行中のため、新規開始をスキップしました。");
        }

        // running を含む現在の状態を返す。フロントはこの後 status をポーリングする。
        return await _martRepository.GetStatusAsync(cancellationToken);
    }
}
