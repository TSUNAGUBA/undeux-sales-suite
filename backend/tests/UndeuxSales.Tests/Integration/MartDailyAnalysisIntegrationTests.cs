using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Database;
using UndeuxSales.Infrastructure.Import;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Tests.Integration;

/// <summary>
/// 週間モニタリングの日次分析 API（/api/mart/daily-series・/api/mart/chohyo-transition）と
/// 在庫アクションの品番3桁別健全性（/api/mart/inventory/actions?includeHinban=true）の統合テスト。
/// 他テストと干渉しない隔離レンジ（2026-08〜2026-10）に専用データをシードして検証する。
/// </summary>
[Collection("Api")]
public sealed class MartDailyAnalysisIntegrationTests
{
    private readonly DatabaseFixture _fixture;

    public MartDailyAnalysisIntegrationTests(DatabaseFixture fixture) => _fixture = fixture;

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

    private async Task SeedAsync(IReadOnlyList<SalesRecord> records)
    {
        var connectionFactory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        await using (connectionFactory)
        {
            var importService = new SalesImportService(
                connectionFactory, NullLogger<SalesImportService>.Instance);
            await importService.ImportAsync(records, ImportSourceType.InitialDump, "daily-analysis.test");
        }
    }

    /// <summary>1行の売上参照レコードを構成する（帳票区分・部門・曜日別数量を可変に）。</summary>
    private static SalesRecord Make(
        string importDate,
        string department,
        string hinban,
        string tanpin,
        string chohyo,
        int[] dailyCounts) => new()
    {
        ImportDate = DateOnly.Parse(importDate),
        CustomerCode = "C001",
        GyotaiCode = "G1",
        ChohyoKubunName = chohyo,
        Department = department,
        HinbanCode = hinban,
        TanpinCode = tanpin,
        Hinmei = $"商品{hinban}-{tanpin}",
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
        Zaikosu = 10,
        RuikeiUriageCount = 50,
        RuikeiNohinCount = 100,
        HatchuCount = 0m,
        DonyuDate = "20240101",
        Zainiti = 20,
        Genka = 400,
        Baika = 1000,
        Kisetsu = "通季",
    };

    // -----------------------------------------------------------------
    // daily-series: 曜日別7列の実日付展開（import_date - 8 + day_index）と気温
    // -----------------------------------------------------------------
    [Fact]
    public async Task DailySeries_ExpandsWeekIntoSevenDays()
    {
        // 部門96・取込週 2026-09-07（→ 前週 月〜日 = 2026-08-31〜2026-09-06）。曜日別数量 1..7。
        await SeedAsync(new[]
        {
            Make("2026-09-07", "96", "960", "0001", "売発注", [1, 2, 3, 4, 5, 6, 7]),
        });
        var client = CreateAuthedClient();

        var response = await client.GetFromJsonAsync<DailySeriesResponse>(
            "/api/mart/daily-series?from=2026-09-07&to=2026-09-07&departments=96");

        Assert.NotNull(response);
        Assert.Equal("東京", response!.AreaCity);
        Assert.Equal(7, response.Points.Count);

        var first = response.Points[0];
        Assert.Equal(DateOnly.Parse("2026-08-31"), first.Date);
        Assert.Equal(1, first.Quantity);
        Assert.Equal(1000, first.Amount);          // 1 × baika(1000)
        Assert.Equal(600, first.GrossProfit);      // 1 × (baika - genka) = 600

        var last = response.Points[6];
        Assert.Equal(DateOnly.Parse("2026-09-06"), last.Date);
        Assert.Equal(7, last.Quantity);
        Assert.Equal(7000, last.Amount);

        // 気温は dim_climate 実測 or 平年値フォールバック。常に有限値（NaN にならない）。
        Assert.All(response.Points, p => Assert.False(double.IsNaN(p.TempAvg)));
    }

    // -----------------------------------------------------------------
    // chohyo-transition: 帳票区分の前週比較による SKU 遷移分類
    // -----------------------------------------------------------------
    [Fact]
    public async Task ChohyoTransition_ClassifiesWeekOverWeekKubunChanges()
    {
        // 部門95・前週 2026-08-03 / 当週 2026-08-10。
        await SeedAsync(new[]
        {
            // 売→情
            Make("2026-08-03", "95", "950", "0001", "売発注", [1, 0, 0, 0, 0, 0, 0]),
            Make("2026-08-10", "95", "950", "0001", "情報発注", [1, 0, 0, 0, 0, 0, 0]),
            // 情→売
            Make("2026-08-03", "95", "951", "0001", "情報発注", [1, 0, 0, 0, 0, 0, 0]),
            Make("2026-08-10", "95", "951", "0001", "売発注", [1, 0, 0, 0, 0, 0, 0]),
            // 変化なし
            Make("2026-08-03", "95", "952", "0001", "売発注", [1, 0, 0, 0, 0, 0, 0]),
            Make("2026-08-10", "95", "952", "0001", "売発注", [1, 0, 0, 0, 0, 0, 0]),
            // 当週新規（前週に無し）
            Make("2026-08-10", "95", "953", "0001", "売発注", [1, 0, 0, 0, 0, 0, 0]),
            // 情売→無（前週のみ）
            Make("2026-08-03", "95", "954", "0001", "売発注", [1, 0, 0, 0, 0, 0, 0]),
            // 売→情（当週は全角スペース付き。正規化して比較されること）
            Make("2026-08-03", "95", "955", "0001", "売発注", [1, 0, 0, 0, 0, 0, 0]),
            Make("2026-08-10", "95", "955", "0001", "情報発注　", [1, 0, 0, 0, 0, 0, 0]),
        });
        var client = CreateAuthedClient();

        var response = await client.GetFromJsonAsync<ChohyoTransitionResponse>(
            "/api/mart/chohyo-transition?from=2026-08-03&to=2026-08-10&departments=95");

        Assert.NotNull(response);
        Assert.Equal(DateOnly.Parse("2026-08-10"), response!.CurrentWeek);
        Assert.Equal(DateOnly.Parse("2026-08-03"), response.PreviousWeek);
        Assert.False(response.Truncated);

        // 当週5件（950/951/952/953/955）＋ dropped 1件（954）。
        Assert.Equal(6, response.Rows.Count);

        string TransitionOf(string hinban) =>
            response.Rows.Single(r => r.HinbanCode == hinban).Transition;

        Assert.Equal("sale-to-info", TransitionOf("950"));
        Assert.Equal("info-to-sale", TransitionOf("951"));
        Assert.Equal("unchanged", TransitionOf("952"));
        Assert.Equal("new", TransitionOf("953"));
        Assert.Equal("dropped", TransitionOf("954"));
        // 全角スペースが正規化され「情報発注」と一致 → 売→情。
        Assert.Equal("sale-to-info", TransitionOf("955"));

        // dropped 行は前週帳票区分を持ち、当週は空。
        var dropped = response.Rows.Single(r => r.HinbanCode == "954");
        Assert.Equal("売発注", dropped.PreviousKubun);
        Assert.Equal("", dropped.CurrentKubun);
    }

    [Fact]
    public async Task ChohyoTransition_SingleWeek_AllNew()
    {
        // 部門98・当週のみ（前週なし）→ 全行 new、dropped なし、PreviousWeek=null。
        await SeedAsync(new[]
        {
            Make("2026-08-17", "98", "980", "0001", "売発注", [1, 0, 0, 0, 0, 0, 0]),
        });
        var client = CreateAuthedClient();

        var response = await client.GetFromJsonAsync<ChohyoTransitionResponse>(
            "/api/mart/chohyo-transition?from=2026-08-17&to=2026-08-17&departments=98");

        Assert.NotNull(response);
        Assert.Equal(DateOnly.Parse("2026-08-17"), response!.CurrentWeek);
        Assert.Null(response.PreviousWeek);
        Assert.Equal("new", Assert.Single(response.Rows).Transition);
    }

    // -----------------------------------------------------------------
    // inventory/actions?includeHinban=true: 品番3桁別の健全性
    // -----------------------------------------------------------------
    [Fact]
    public async Task InventoryActions_IncludeHinban_ReturnsByHinban()
    {
        // 部門97・取込週 2026-10-05。2品番（970/971）。
        await SeedAsync(new[]
        {
            Make("2026-10-05", "97", "970", "0001", "売発注", [1, 0, 0, 0, 0, 0, 0]),
            Make("2026-10-05", "97", "971", "0001", "売発注", [1, 0, 0, 0, 0, 0, 0]),
        });
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        const string range = "from=2026-10-05&to=2026-10-05&departments=97";

        // includeHinban=true → 品番3桁別の健全性を返す。
        var withHinban = await client.GetFromJsonAsync<InventoryActionsResponse>(
            $"/api/mart/inventory/actions?{range}&includeHinban=true");
        Assert.NotNull(withHinban);
        Assert.Equal(2, withHinban!.ByHinban.Count);
        Assert.Contains(withHinban.ByHinban, h => h.Key == "970");
        Assert.Contains(withHinban.ByHinban, h => h.Key == "971");
        // 品番3桁別の在庫合計は最新週スナップショットの全体在庫と一致する。
        Assert.Equal(withHinban.Kpi.TotalStock, withHinban.ByHinban.Sum(h => h.Stock));

        // includeHinban 省略 → byHinban は空（在庫ダイジェスト等の既存呼出に追加コストを掛けない）。
        var withoutHinban = await client.GetFromJsonAsync<InventoryActionsResponse>(
            $"/api/mart/inventory/actions?{range}");
        Assert.NotNull(withoutHinban);
        Assert.Empty(withoutHinban!.ByHinban);
    }
}
