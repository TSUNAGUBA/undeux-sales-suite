using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UndeuxSales.Core;
using UndeuxSales.Core.Models;
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
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 50;
    private const int DefaultRankingMaxRows = 500;

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

    /// <summary>在庫・発注の主要KPIと部門別内訳を mart（最新週スナップショット基準）から取得する。</summary>
    [HttpGet("inventory")]
    public Task<InventoryResponse> Inventory(
        [FromQuery] SalesQueryFilter filter, CancellationToken cancellationToken)
        => _martRepository.GetInventoryAsync(filter, cancellationToken);

    /// <summary>商品（SKU）別の売上・在庫一覧をページングで mart から取得する。</summary>
    [HttpGet("products")]
    public Task<ProductPage> Products(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string? sort,
        [FromQuery] string? order,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
        => _martRepository.GetProductsAsync(
            filter,
            RequestParsing.ProductSort(sort),
            RequestParsing.IsAscending(order),
            page <= 0 ? DefaultPage : page,
            pageSize <= 0 ? DefaultPageSize : pageSize,
            cancellationToken);

    /// <summary>クロス集計マトリクスを mart から取得する（帳票区分・棚割は未対応）。</summary>
    [HttpGet("crosstab")]
    public Task<CrosstabMatrixResponse> Crosstab(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string? rowDimension,
        [FromQuery] string? columnDimension,
        [FromQuery] string? temperatureArea,
        CancellationToken cancellationToken)
    {
        var rowDim = RequestParsing.ParseCrosstabDimension(rowDimension, "rowDimension");
        var colDim = RequestParsing.ParseCrosstabDimension(columnDimension, "columnDimension");
        var area = ClimateModel.ParseArea(temperatureArea);
        return _martRepository.GetCrosstabMatrixAsync(filter, rowDim, colDim, area, cancellationToken);
    }

    /// <summary>ランキング分析の集計素材を mart から取得する（順位・複合スコアはフロント射影）。</summary>
    [HttpGet("ranking")]
    public Task<RankingResponse> Ranking(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string? dimension,
        [FromQuery] DateOnly? compareFrom,
        [FromQuery] DateOnly? compareTo,
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        var dim = RequestParsing.Dimension(dimension);

        // 比較期間は compareFrom / compareTo の両方が揃ったときのみ有効。
        // カテゴリ系フィルタは主期間と共有し、日付範囲のみ差し替える（RankingController と同一方針）。
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
            };
        }

        var maxRows = limit <= 0 ? DefaultRankingMaxRows : limit;
        return _martRepository.GetRankingAsync(filter, comparison, dim, maxRows, cancellationToken);
    }

    /// <summary>週次系列（売上フロー指標 + その週・エリアの気温）を mart から取得する。</summary>
    [HttpGet("weekly-series")]
    public Task<WeeklySeriesResponse> WeeklySeries(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string? area,
        CancellationToken cancellationToken)
    {
        var resolved = ClimateModel.ParseArea(area) ?? TemperatureArea.Standard;
        return _martRepository.GetWeeklySeriesAsync(filter, resolved, cancellationToken);
    }

    /// <summary>消化率×値引き率の散布図素材（型番単位）を mart から取得する。</summary>
    [HttpGet("markdown")]
    public Task<MarkdownScatterResponse> Markdown(
        [FromQuery] SalesQueryFilter filter, CancellationToken cancellationToken)
        => _martRepository.GetMarkdownScatterAsync(filter, cancellationToken);

    /// <summary>
    /// 商品導入管理の一覧（商品単位・ページング）を mart から取得する。
    /// 期間は導入日（dim_sku.attributes->>'donyu'）基準。並びは導入日（既定: 降順）。
    /// </summary>
    [HttpGet("introductions")]
    public Task<MartIntroductionPage> Introductions(
        [FromQuery] MartIntroductionQuery query,
        [FromQuery] string? order,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
        => _martRepository.GetIntroductionsAsync(
            query,
            RequestParsing.IsAscending(order),
            page <= 0 ? DefaultPage : page,
            pageSize <= 0 ? DefaultPageSize : pageSize,
            cancellationToken);

    /// <summary>商品導入管理のフィルタ選択肢（ブランド・担当者・服種）を mart から取得する。</summary>
    [HttpGet("introduction-options")]
    public Task<MartIntroductionOptions> IntroductionOptions(CancellationToken cancellationToken)
        => _martRepository.GetIntroductionOptionsAsync(cancellationToken);

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
