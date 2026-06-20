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

    /// <summary>
    /// 在庫アクションサマリー（KPI・前週比較・状態別件数・今週のアクション・部門別健全性）を取得する。
    /// 在庫マネジメントのダッシュボードタブと全社サマリーのダイジェストが共用する。
    /// 滞留・不動の判定閾値は <see cref="InventoryHealthRules"/>（SoT）で、適用値はレスポンスに含まれる。
    /// </summary>
    [HttpGet("inventory/actions")]
    public Task<InventoryActionsResponse> InventoryActions(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] bool includeHinban,
        CancellationToken cancellationToken)
        => _martRepository.GetInventoryActionsAsync(filter, includeHinban, cancellationToken);

    /// <summary>特定品番の日次系列（売上＋東京の平均気温）を取得する（週間モニタリングの日次分析タブ）。</summary>
    [HttpGet("daily-series")]
    public Task<DailySeriesResponse> DailySeries(
        [FromQuery] SalesQueryFilter filter, CancellationToken cancellationToken)
        => _martRepository.GetDailySeriesAsync(filter, cancellationToken);

    /// <summary>帳票区分（売発注/情報発注）の前週比較による SKU 遷移を取得する（品番3桁×単品4桁）。</summary>
    [HttpGet("chohyo-transition")]
    public Task<ChohyoTransitionResponse> ChohyoTransition(
        [FromQuery] SalesQueryFilter filter, CancellationToken cancellationToken)
        => _martRepository.GetChohyoTransitionAsync(filter, cancellationToken);

    /// <summary>
    /// 在庫アクション明細（SKU単位・最新週スナップショット基準）をページングで取得する。
    /// statuses（healthy / caution / stagnant / dormant）で状態を絞り込む（未知値は無視＝
    /// 在日バケットと同じグレースフル方式）。並び替え未指定時の既定値は絞込状態に応じて変える
    /// （不動のみ→出荷ゼロ週数、滞留のみ→在庫日数、それ以外→在庫数。いずれも降順）。
    /// </summary>
    /// <remarks>
    /// フラグ絞込（flagTypes / flagStatuses / flagged=any|none）は対応リストタブが使用する。
    /// いずれも未知値は無視（statuses と同じグレースフル方式）。
    /// </remarks>
    [HttpGet("inventory/items")]
    public Task<InventoryItemsPage> InventoryItems(
        [FromQuery] SalesQueryFilter filter,
        [FromQuery] string[]? statuses,
        [FromQuery] string? search,
        [FromQuery] string[]? flagTypes,
        [FromQuery] string[]? flagStatuses,
        [FromQuery] string? flagged,
        [FromQuery] string? sort,
        [FromQuery] string? order,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var normalized = InventoryHealthRules.NormalizeStatuses(statuses);
        return _martRepository.GetInventoryItemsAsync(
            filter,
            normalized,
            search,
            InventoryFlagRules.NormalizeFlagTypes(flagTypes),
            InventoryFlagRules.NormalizeFlagStatuses(flagStatuses),
            InventoryFlagRules.NormalizeFlagged(flagged),
            RequestParsing.InventoryItemSort(sort, DefaultInventoryItemSort(normalized)),
            RequestParsing.IsAscending(order),
            page <= 0 ? DefaultPage : page,
            pageSize <= 0 ? DefaultPageSize : pageSize,
            cancellationToken);
    }

    /// <summary>在庫アクション明細の既定並び替えキー（単一状態への絞込時は「最も見るべき順」）。</summary>
    private static InventoryItemSortKey DefaultInventoryItemSort(IReadOnlyList<string> statuses)
        => statuses.Count == 1
            ? statuses[0] switch
            {
                InventoryHealthRules.StatusDormant => InventoryItemSortKey.ZeroSalesWeeks,
                InventoryHealthRules.StatusStagnant => InventoryItemSortKey.StockDays,
                _ => InventoryItemSortKey.Stock,
            }
            : InventoryItemSortKey.Stock;

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
                ShohinKigos = filter.ShohinKigos,
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
