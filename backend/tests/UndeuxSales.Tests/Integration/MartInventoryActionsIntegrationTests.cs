using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using UndeuxSales.Core;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Database;
using UndeuxSales.Infrastructure.Import;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Tests.Integration;

/// <summary>
/// 在庫アクション API（/api/mart/inventory/actions・/api/mart/inventory/items）の統合テスト。
/// 既存シード（2026-05-04 / 2026-05-11）に対する「全件健全」の検証と、本クラス専用の
/// 隔離レンジ（2026-01-05〜2026-03-02。他テストの週範囲と干渉しない）に滞留・不動・注意・健全の
/// 4シナリオをシードした分類・推奨・絞込の検証を行う。
/// </summary>
[Collection("Api")]
public sealed class MartInventoryActionsIntegrationTests
{
    /// <summary>既存シード（全行 zainiti=30・最新週に販売あり）に限定するフィルタ。</summary>
    private const string SeedRange = "from=2026-05-04&to=2026-05-11";

    /// <summary>本クラス専用シナリオの週範囲（不動判定の 8 週ちょうどを構成する）。</summary>
    private const string ScenarioRange = "from=2026-01-05&to=2026-03-02";

    private readonly DatabaseFixture _fixture;

    public MartInventoryActionsIntegrationTests(DatabaseFixture fixture) => _fixture = fixture;

    private HttpClient CreateAuthedClient()
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuthHandler.AdminToken);
        return client;
    }

    private async Task RebuildMartAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition("SELECT mart.rebuild();", commandTimeout: 120));
    }

    /// <summary>シナリオ行を取込サービス経由でシードする（UPSERT のため再実行は冪等）。</summary>
    private async Task SeedScenarioAsync()
    {
        var connectionFactory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        await using (connectionFactory)
        {
            var importService = new SalesImportService(
                connectionFactory, NullLogger<SalesImportService>.Instance);
            await importService.ImportAsync(
                ScenarioRecords(), ImportSourceType.InitialDump, "inventory-actions.test");
        }
    }

    /// <summary>
    /// 隔離レンジのシナリオ（部門90・最新週 2026-03-02 アンカー）:
    /// <list type="bullet">
    /// <item>品番900: 2026-01-05 に販売後、最新週まで出荷ゼロ（8週ちょうど）＝不動。発注残なし→売場再点検</item>
    /// <item>品番901: 最新週に販売あり・在日70×消化率0.3＝滞留。発注残あり→発注抑制</item>
    /// <item>品番902: 在日50（45超60以下）×消化率0.9＝注意</item>
    /// <item>品番903: 在日10＝健全</item>
    /// <item>品番904〜906: SQL 側判定（StatusCaseExpression）の境界回帰網。C# 側 Classify と
    /// 同一論理であることを「ちょうど境界値」の実データで固定する（904: 在日45ちょうど＝健全、
    /// 905: 在日60ちょうど×消化率0.74＝注意（滞留でない）、906: 在日61×消化率0.75ちょうど＝注意）</item>
    /// </list>
    /// </summary>
    private static IReadOnlyList<SalesRecord> ScenarioRecords() => new[]
    {
        // 不動（最終販売週 → 8週後の最新週は出荷ゼロ）
        Make("2026-01-05", "900", "0001", [2, 0, 0, 0, 0, 0, 0], zaikosu: 50, zainiti: 20,
            ruikeiUriage: 10, ruikeiNohin: 60, hatchu: 0m),
        Make("2026-03-02", "900", "0001", [0, 0, 0, 0, 0, 0, 0], zaikosu: 48, zainiti: 90,
            ruikeiUriage: 12, ruikeiNohin: 60, hatchu: 0m),
        // 滞留（在日70 > 60・消化率 30/100 < 0.75・発注残あり）
        Make("2026-03-02", "901", "0001", [1, 0, 0, 0, 0, 0, 0], zaikosu: 40, zainiti: 70,
            ruikeiUriage: 30, ruikeiNohin: 100, hatchu: 2.0m),
        // 注意（在日50 > 45・滞留条件は未充足）
        Make("2026-03-02", "902", "0001", [1, 0, 0, 0, 0, 0, 0], zaikosu: 30, zainiti: 50,
            ruikeiUriage: 90, ruikeiNohin: 100, hatchu: 0m),
        // 健全
        Make("2026-03-02", "903", "0001", [2, 0, 0, 0, 0, 0, 0], zaikosu: 5, zainiti: 10,
            ruikeiUriage: 95, ruikeiNohin: 100, hatchu: 0m),
        // 境界: 在日45ちょうど → 健全（注意は「45日超」。> を >= に書き換えると本行が caution になり検出）
        Make("2026-03-02", "904", "0001", [1, 0, 0, 0, 0, 0, 0], zaikosu: 10, zainiti: 45,
            ruikeiUriage: 90, ruikeiNohin: 100, hatchu: 0m),
        // 境界: 在日60ちょうど×消化率0.74 → 注意（滞留は「60日超」。>= 化すると本行が stagnant になり検出）
        Make("2026-03-02", "905", "0001", [1, 0, 0, 0, 0, 0, 0], zaikosu: 20, zainiti: 60,
            ruikeiUriage: 74, ruikeiNohin: 100, hatchu: 0m),
        // 境界: 在日61×消化率0.75ちょうど → 注意（滞留は「0.75未満」。<= 化すると本行が stagnant になり検出）
        Make("2026-03-02", "906", "0001", [1, 0, 0, 0, 0, 0, 0], zaikosu: 15, zainiti: 61,
            ruikeiUriage: 75, ruikeiNohin: 100, hatchu: 0m),
    };

    private static SalesRecord Make(
        string importDate,
        string hinban,
        string tanpin,
        int[] dailyCounts,
        int zaikosu,
        int zainiti,
        int ruikeiUriage,
        int ruikeiNohin,
        decimal hatchu) => new()
    {
        ImportDate = DateOnly.Parse(importDate),
        CustomerCode = "C001",
        GyotaiCode = "G1",
        ChohyoKubunName = "売発注",
        Department = "90",
        HinbanCode = hinban,
        TanpinCode = tanpin,
        Hinmei = $"商品{hinban}",
        ShohinKigou = $"S{hinban}",
        Color = "標準色",
        Size = "M",
        ToshuUriageCount1 = dailyCounts[0],
        ToshuUriageCount2 = dailyCounts[1],
        ToshuUriageCount3 = dailyCounts[2],
        ToshuUriageCount4 = dailyCounts[3],
        ToshuUriageCount5 = dailyCounts[4],
        ToshuUriageCount6 = dailyCounts[5],
        ToshuUriageCount7 = dailyCounts[6],
        Zaikosu = zaikosu,
        RuikeiUriageCount = ruikeiUriage,
        RuikeiNohinCount = ruikeiNohin,
        HatchuCount = hatchu,
        DonyuDate = "20240101",
        Zainiti = zainiti,
        Genka = 400,
        Baika = 1000,
        Kisetsu = "通季",
    };

    [Fact]
    public async Task InventoryActions_SeedRange_AllHealthy_ReturnsAllClear()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var response = await client.GetFromJsonAsync<InventoryActionsResponse>(
            $"/api/mart/inventory/actions?{SeedRange}");

        Assert.NotNull(response);
        // 既存シードは全行 zainiti=30（45以下）かつ最新週±1週に販売があるため全件 healthy。
        Assert.Equal(DateOnly.Parse("2026-05-11"), response!.LatestWeek);
        Assert.Equal(DateOnly.Parse("2026-05-04"), response.PreviousWeek);
        Assert.Equal(3, response.StatusCounts.Healthy);
        Assert.Equal(3, response.StatusCounts.Total);
        Assert.Equal(0, response.StatusCounts.Stagnant);
        Assert.Equal(0, response.StatusCounts.Dormant);
        // 既存 /api/mart/inventory と同じ最新週スナップショット（在庫74点）。
        Assert.Equal(74, response.Kpi.TotalStock);
        // 在庫金額（原価）= 44×400 + 10×800 + 20×600。
        Assert.Equal(37600, response.Kpi.StockValueCost);
        Assert.Equal(1.0, response.Kpi.HealthyStockRatio, 6);
        Assert.NotNull(response.PreviousKpi);
        Assert.Equal(90, response.PreviousKpi!.TotalStock);
        // 滞留・不動なし → all-clear の info のみ。
        var action = Assert.Single(response.Actions);
        Assert.Equal("all-clear", action.Code);
        Assert.Equal("info", action.Severity);
        // 適用閾値はルール SoT の値をそのまま返す（フロント定義バナーの情報源）。
        Assert.Equal(InventoryHealthRules.StagnantStockDays, response.Thresholds.StagnantStockDays);
        Assert.Equal(InventoryHealthRules.DormantLookbackWeeks, response.Thresholds.DormantLookbackWeeks);
        Assert.Equal(2, response.ByDepartment.Count);
    }

    [Fact]
    public async Task InventoryActions_Scenario_DetectsDormantAndStagnant()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var response = await client.GetFromJsonAsync<InventoryActionsResponse>(
            $"/api/mart/inventory/actions?{ScenarioRange}");

        Assert.NotNull(response);
        Assert.Equal(DateOnly.Parse("2026-03-02"), response!.LatestWeek);
        Assert.Equal(1, response.StatusCounts.Dormant);
        Assert.Equal(1, response.StatusCounts.Stagnant);
        // 注意 = 902（在日50）+ 境界の905（在日60ちょうど）+ 906（消化率0.75ちょうど）。
        // 健全 = 903 + 境界の904（在日45ちょうど）。SQL 側 CASE の > / < 境界の回帰網。
        Assert.Equal(3, response.StatusCounts.Caution);
        Assert.Equal(2, response.StatusCounts.Healthy);
        Assert.Equal(7, response.StatusCounts.Total);
        // 最新週（2026-03-02）の在庫合計 = 48 + 40 + 30 + 5 + 10 + 20 + 15。
        Assert.Equal(168, response.Kpi.TotalStock);

        var dormant = Assert.Single(response.Actions, a => a.Code == "dormant-detected");
        Assert.Equal("danger", dormant.Severity);
        Assert.Equal(1, dormant.Count);
        Assert.Equal("dormant", dormant.TargetTab);

        // 滞留1件は発注残あり → danger（発注抑制の最優先対象）。
        var stagnant = Assert.Single(response.Actions, a => a.Code == "stagnant-detected");
        Assert.Equal("danger", stagnant.Severity);
        Assert.DoesNotContain(response.Actions, a => a.Code == "all-clear");
    }

    [Fact]
    public async Task InventoryItems_DormantFilter_ReturnsRecommendation()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var page = await client.GetFromJsonAsync<InventoryItemsPage>(
            $"/api/mart/inventory/items?{ScenarioRange}&statuses=dormant");

        Assert.NotNull(page);
        Assert.Equal(1, page!.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal("900", item.HinbanCode);
        Assert.Equal(InventoryHealthRules.StatusDormant, item.Status);
        // 2026-01-05 販売 → 2026-03-02 アンカーで出荷ゼロ 8週ちょうど（不動の下限境界）。
        Assert.Equal(8, item.ZeroSalesWeeks);
        // 発注残なし・8週（12週未満）→ 売場・棚割の再点検。
        Assert.Equal(InventoryHealthRules.ActionReviewShelf, item.RecommendedAction);
        // 在庫金額（原価）= 48 × 400。
        Assert.Equal(19200, item.StockValueCost);
        // StatusCounts は statuses 絞込前の全体件数（チップ・タブバッジ用）。
        Assert.Equal(7, page.StatusCounts.Total);
        Assert.Equal(1, page.StatusCounts.Dormant);
        // 不動の経過バケット: 8週は [8,12) の先頭バケット。
        Assert.Equal(new[] { 1, 0, 0 }, page.DormantAgingCounts);
    }

    [Fact]
    public async Task InventoryItems_UnknownStatusesAreIgnored()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        // 未知値（unknown）は無視され、有効な stagnant だけが適用される。
        var page = await client.GetFromJsonAsync<InventoryItemsPage>(
            $"/api/mart/inventory/items?{ScenarioRange}&statuses=unknown&statuses=stagnant");

        Assert.NotNull(page);
        Assert.Equal(1, page!.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal("901", item.HinbanCode);
        Assert.Equal(InventoryHealthRules.StatusStagnant, item.Status);
        // 発注残あり滞留 → 発注抑制が最優先。
        Assert.Equal(InventoryHealthRules.ActionReduceOrder, item.RecommendedAction);
        Assert.Equal(2.0m, item.OrderQuantity);
    }

    [Fact]
    public async Task InventoryItems_SearchNarrowsByHinban()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var page = await client.GetFromJsonAsync<InventoryItemsPage>(
            $"/api/mart/inventory/items?{ScenarioRange}&search=900");

        Assert.NotNull(page);
        Assert.Equal(1, page!.TotalCount);
        Assert.Equal("900", Assert.Single(page.Items).HinbanCode);
        // 検索は statuses 絞込前件数（StatusCounts）にも適用される。
        Assert.Equal(1, page.StatusCounts.Total);
    }
}
