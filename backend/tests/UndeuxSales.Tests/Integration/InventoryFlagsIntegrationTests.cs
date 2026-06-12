using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using UndeuxSales.Api;
using UndeuxSales.Core;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Database;
using UndeuxSales.Infrastructure.Import;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Tests.Integration;

/// <summary>
/// 在庫アクションフラグ API（/api/inventory-flags/*）と items レスポンスのフラグ結合の統合テスト。
/// フラグは fixture の TRUNCATE（コレクション初期化時）以外で削除されないため、
/// **テストごとに専用の品番（910〜918）を使い、クエリも自品番に絞って実行順非依存を保つ**。
/// 週レンジは他テストと干渉しない 2025-06-02 / 2025-06-09 を使用する。
/// </summary>
[Collection("Api")]
public sealed class InventoryFlagsIntegrationTests
{
    /// <summary>本クラス専用シナリオの週範囲（既存テストの週・走査窓と非干渉）。</summary>
    private const string ScenarioRange = "from=2025-06-02&to=2025-06-09";

    private readonly DatabaseFixture _fixture;

    public InventoryFlagsIntegrationTests(DatabaseFixture fixture) => _fixture = fixture;

    private HttpClient CreateAuthedClient(string token = TestAuthHandler.AdminToken)
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task RebuildMartAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition("SELECT mart.rebuild();", commandTimeout: 120));
    }

    /// <summary>シナリオ行（品番910〜916・部門91）を取込サービス経由でシードする（UPSERT・冪等）。</summary>
    private async Task SeedScenarioAsync()
    {
        var connectionFactory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        await using (connectionFactory)
        {
            var importService = new SalesImportService(
                connectionFactory, NullLogger<SalesImportService>.Instance);
            await importService.ImportAsync(ScenarioRecords(), ImportSourceType.InitialDump, "inventory-flags.test");
        }
    }

    private static IReadOnlyList<SalesRecord> ScenarioRecords() => new[]
    {
        // T1: 滞留（在日70×消化率0.2）と健全の2 SKU
        Make("910", "0001", zaikosu: 30, zainiti: 70, ruikeiUriage: 20, ruikeiNohin: 100),
        Make("910", "0002", zaikosu: 20, zainiti: 10, ruikeiUriage: 90, ruikeiNohin: 100),
        // T2〜T7: 各テスト専用の1〜2 SKU
        Make("911", "0001", zaikosu: 15, zainiti: 65, ruikeiUriage: 30, ruikeiNohin: 100),
        Make("912", "0001", zaikosu: 15, zainiti: 65, ruikeiUriage: 30, ruikeiNohin: 100),
        Make("913", "0001", zaikosu: 15, zainiti: 65, ruikeiUriage: 30, ruikeiNohin: 100),
        Make("914", "0001", zaikosu: 15, zainiti: 65, ruikeiUriage: 30, ruikeiNohin: 100),
        Make("915", "0001", zaikosu: 15, zainiti: 65, ruikeiUriage: 30, ruikeiNohin: 100),
        Make("916", "0001", zaikosu: 15, zainiti: 65, ruikeiUriage: 30, ruikeiNohin: 100),
        Make("916", "0002", zaikosu: 15, zainiti: 10, ruikeiUriage: 90, ruikeiNohin: 100),
        Make("917", "0001", zaikosu: 15, zainiti: 65, ruikeiUriage: 30, ruikeiNohin: 100),
        Make("918", "0001", zaikosu: 15, zainiti: 65, ruikeiUriage: 30, ruikeiNohin: 100),
    };

    private static SalesRecord Make(
        string hinban, string tanpin, int zaikosu, int zainiti, int ruikeiUriage, int ruikeiNohin) => new()
    {
        ImportDate = DateOnly.Parse("2025-06-09"),
        CustomerCode = "C001",
        GyotaiCode = "G1",
        ChohyoKubunName = "売発注",
        Department = "91",
        HinbanCode = hinban,
        TanpinCode = tanpin,
        Hinmei = $"商品{hinban}",
        ShohinKigou = $"S{hinban}",
        Color = "標準色",
        Size = "M",
        ToshuUriageCount1 = 1,
        Zaikosu = zaikosu,
        RuikeiUriageCount = ruikeiUriage,
        RuikeiNohinCount = ruikeiNohin,
        HatchuCount = 1.0m,
        DonyuDate = "20240101",
        Zainiti = zainiti,
        Genka = 400,
        Baika = 1000,
        Kisetsu = "通季",
    };

    private static object BulkBody(string flagType, params (string Hinban, string Tanpin, string Status)[] items) => new
    {
        flagType,
        items = items.Select(i => new
        {
            gyotaiCode = "G1",
            shohinKigou = $"S{i.Hinban}",
            hinbanCode = i.Hinban,
            tanpinCode = i.Tanpin,
            currentStatus = i.Status,
        }).ToArray(),
    };

    private async Task<InventoryItemsPage> GetItemsAsync(HttpClient client, string hinban, string extra = "")
        => (await client.GetFromJsonAsync<InventoryItemsPage>(
            $"/api/mart/inventory/items?{ScenarioRange}&hinbans={hinban}{extra}"))!;

    [Fact]
    public async Task Bulk_RegistersFlags_AndItemsExposeThem()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var response = await client.PostAsJsonAsync("/api/inventory-flags/bulk",
            BulkBody("markdown", ("910", "0001", "stagnant"), ("910", "0002", "healthy")));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<InventoryFlagBulkResult>();
        Assert.Equal(2, result!.Created);
        Assert.Equal(0, result.Skipped);

        var page = await GetItemsAsync(client, "910");
        Assert.Equal(2, page.TotalCount);
        var stagnantRow = Assert.Single(page.Items, r => r.TanpinCode == "0001");
        var flag = Assert.Single(stagnantRow.Flags);
        Assert.Equal("markdown", flag.FlagType);
        Assert.Equal(InventoryFlagRules.StatusCandidate, flag.Status);
        // 付与時の判定状態が保存される（閾値変更後の「現在は判定対象外」検知の根拠）。
        Assert.Equal("stagnant", flag.FlaggedStatus);
        // 監査ユーザーは TestAuthHandler の生クレーム email（本番の MapInboundClaims=false 相当の経路）。
        Assert.Equal("tester@example.com", flag.UpdatedBy);
        // flagged_week はサーバが「無フィルタのグローバル最新スナップショット週」を解決して記録する
        // （クライアント申告にしない）。期待値も同じ定義で DB から直接引き、実行順非依存に固定する。
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        var globalLatestWeek = await connection.ExecuteScalarAsync<DateOnly>(
            @"SELECT MAX(dd.week_monday)
              FROM mart.fact_inventory_snapshot inv
              JOIN mart.dim_date dd ON dd.date_key = inv.date_key;");
        Assert.Equal(globalLatestWeek, flag.FlaggedWeek);
    }

    [Fact]
    public async Task Bulk_IsIdempotent_AndDoesNotRollBackStatus()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var first = await client.PostAsJsonAsync("/api/inventory-flags/bulk",
            BulkBody("stop-order", ("911", "0001", "stagnant")));
        first.EnsureSuccessStatusCode();

        var flagId = (await GetItemsAsync(client, "911")).Items.Single().Flags.Single().Id;

        // 対応状況を進める。
        var advance = await client.PostAsJsonAsync("/api/inventory-flags/status",
            new { ids = new[] { flagId }, status = "in-progress" });
        Assert.Equal(HttpStatusCode.NoContent, advance.StatusCode);

        // 同一内容の再登録（二重クリック・再実行）が状態を candidate に巻き戻さないこと（原則2）。
        var second = await client.PostAsJsonAsync("/api/inventory-flags/bulk",
            BulkBody("stop-order", ("911", "0001", "stagnant")));
        second.EnsureSuccessStatusCode();
        var result = await second.Content.ReadFromJsonAsync<InventoryFlagBulkResult>();
        Assert.Equal(0, result!.Created);
        Assert.Equal(1, result.Skipped);

        var flag = (await GetItemsAsync(client, "911")).Items.Single().Flags.Single();
        Assert.Equal(InventoryFlagRules.StatusInProgress, flag.Status);
    }

    [Fact]
    public async Task Status_UnknownId_Returns404_AndUpdatesNothing()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        (await client.PostAsJsonAsync("/api/inventory-flags/bulk",
            BulkBody("markdown", ("912", "0001", "stagnant")))).EnsureSuccessStatusCode();
        var flagId = (await GetItemsAsync(client, "912")).Items.Single().Flags.Single().Id;

        // 未知 id を含む一括変更は all-or-nothing で 404（UNDX-DATA-003）。
        var response = await client.PostAsJsonAsync("/api/inventory-flags/status",
            new { ids = new[] { flagId, 99_999_999L }, status = "done" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(ErrorCodes.FlagNotFound.Code, error!.ErrorCode);

        // 実在側の行も未更新（candidate のまま）。
        var flag = (await GetItemsAsync(client, "912")).Items.Single().Flags.Single();
        Assert.Equal(InventoryFlagRules.StatusCandidate, flag.Status);
    }

    [Fact]
    public async Task Status_UpdatesNoteAndAuditColumns()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        (await client.PostAsJsonAsync("/api/inventory-flags/bulk",
            BulkBody("markdown", ("913", "0001", "stagnant")))).EnsureSuccessStatusCode();
        var flagId = (await GetItemsAsync(client, "913")).Items.Single().Flags.Single().Id;

        var response = await client.PostAsJsonAsync("/api/inventory-flags/status",
            new { ids = new[] { flagId }, status = "done", note = "値下げ実施済み" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var flag = (await GetItemsAsync(client, "913")).Items.Single().Flags.Single();
        Assert.Equal(InventoryFlagRules.StatusDone, flag.Status);
        Assert.Equal("値下げ実施済み", flag.Note);
        Assert.Equal("tester@example.com", flag.UpdatedBy);
    }

    [Fact]
    public async Task Authorization_RequiresAuthButNotAdmin()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();

        // 無トークン → 401。
        var anonymous = _fixture.Factory.CreateClient();
        var unauthorized = await anonymous.PostAsJsonAsync("/api/inventory-flags/bulk",
            BulkBody("markdown", ("914", "0001", "stagnant")));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        // 非 admin の認証ユーザー → 登録可能（チーム共有の判断記録のため admin 不要）。
        var member = CreateAuthedClient("member-token");
        var response = await member.PostAsJsonAsync("/api/inventory-flags/bulk",
            BulkBody("markdown", ("914", "0001", "stagnant")));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<InventoryFlagBulkResult>();
        Assert.Equal(1, result!.Created);
    }

    [Fact]
    public async Task MartRebuild_PreservesFlags()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        (await client.PostAsJsonAsync("/api/inventory-flags/bulk",
            BulkBody("stop-order", ("915", "0001", "stagnant")))).EnsureSuccessStatusCode();

        // mart 全再構築（TRUNCATE + 再生成）後もフラグは自然キー結合で明細に出続ける。
        await RebuildMartAsync();

        var flag = (await GetItemsAsync(client, "915")).Items.Single().Flags.Single();
        Assert.Equal("stop-order", flag.FlagType);
        Assert.Equal(InventoryFlagRules.StatusCandidate, flag.Status);
    }

    [Fact]
    public async Task FlagFilters_NarrowItems()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        // 916-0001 のみフラグ付与（916-0002 はフラグなし）。
        (await client.PostAsJsonAsync("/api/inventory-flags/bulk",
            BulkBody("markdown", ("916", "0001", "stagnant")))).EnsureSuccessStatusCode();

        var flaggedAny = await GetItemsAsync(client, "916", "&flagged=any");
        Assert.Equal(1, flaggedAny.TotalCount);
        Assert.Equal("0001", Assert.Single(flaggedAny.Items).TanpinCode);

        var flaggedNone = await GetItemsAsync(client, "916", "&flagged=none");
        Assert.Equal(1, flaggedNone.TotalCount);
        Assert.Equal("0002", Assert.Single(flaggedNone.Items).TanpinCode);

        // 種別絞込: stop-order のフラグは存在しない → 0件。未知値は無視され絞込なし扱い。
        var stopOrderOnly = await GetItemsAsync(client, "916", "&flagTypes=stop-order");
        Assert.Equal(0, stopOrderOnly.TotalCount);
        var unknownIgnored = await GetItemsAsync(client, "916", "&flagTypes=bogus");
        Assert.Equal(2, unknownIgnored.TotalCount);

        // 状態絞込（種別未指定 → 全種別を対象）。
        var byStatus = await GetItemsAsync(client, "916", "&flagStatuses=candidate");
        Assert.Equal(1, byStatus.TotalCount);
    }

    [Fact]
    public async Task Delete_IsAllOrNothing_AndRemovesFlag()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        // 同一 SKU に2種別のフラグを併存させる（stop-order と markdown は両立可能な設計）。
        (await client.PostAsJsonAsync("/api/inventory-flags/bulk",
            BulkBody("stop-order", ("917", "0001", "stagnant")))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/inventory-flags/bulk",
            BulkBody("markdown", ("917", "0001", "stagnant")))).EnsureSuccessStatusCode();
        var flags = (await GetItemsAsync(client, "917")).Items.Single().Flags;
        Assert.Equal(2, flags.Count);
        var markdownId = flags.Single(f => f.FlagType == "markdown").Id;

        // 未知 id を含む削除は all-or-nothing で 404、両フラグとも残る。
        var notFound = await client.PostAsJsonAsync("/api/inventory-flags/delete",
            new { ids = new[] { markdownId, 99_999_999L } });
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        var error = await notFound.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(ErrorCodes.FlagNotFound.Code, error!.ErrorCode);
        Assert.Equal(2, (await GetItemsAsync(client, "917")).Items.Single().Flags.Count);

        // 正常削除: markdown のみ消え、stop-order は残る。
        var deleted = await client.PostAsJsonAsync("/api/inventory-flags/delete",
            new { ids = new[] { markdownId } });
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var remaining = (await GetItemsAsync(client, "917")).Items.Single().Flags;
        Assert.Equal("stop-order", Assert.Single(remaining).FlagType);
    }

    [Fact]
    public async Task Summary_CountsByTypeAndStatus_AndDetectsOrphans()
    {
        await SeedScenarioAsync();
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        // 実在 SKU（918）と、mart に存在しない SKU（孤児）の両方へフラグを付与する。
        // bulk は自然キーの存在検証を行わない（完売・取扱終了後の運用記録を許容する）ため登録できる。
        (await client.PostAsJsonAsync("/api/inventory-flags/bulk",
            BulkBody("markdown", ("918", "0001", "stagnant")))).EnsureSuccessStatusCode();
        var orphan = await client.PostAsJsonAsync("/api/inventory-flags/bulk", new
        {
            flagType = "markdown",
            items = new[]
            {
                new
                {
                    gyotaiCode = "G1",
                    shohinKigou = "S999",
                    hinbanCode = "999",
                    tanpinCode = "9999",
                    currentStatus = "stagnant",
                },
            },
        });
        orphan.EnsureSuccessStatusCode();

        var summary = await client.GetFromJsonAsync<InventoryFlagSummary>("/api/inventory-flags/summary");

        Assert.NotNull(summary);
        Assert.NotNull(summary!.LatestWeek);
        // 他テストのフラグと並存するため、厳密件数ではなく包含と下限で検証する（実行順非依存）。
        var markdownCandidates = summary.Rows.Single(r => r.FlagType == "markdown" && r.Status == "candidate");
        Assert.True(markdownCandidates.Count >= 2,
            $"markdown×candidate は 918 + 孤児の2件以上のはず（実際: {markdownCandidates.Count}）");
        Assert.True(summary.OrphanCount >= 1,
            $"mart に存在しない SKU のフラグが1件以上のはず（実際: {summary.OrphanCount}）");
    }
}
