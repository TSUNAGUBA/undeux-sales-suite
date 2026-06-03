using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Npgsql;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Tests.Integration;

/// <summary>
/// 分析 mart（スタースキーマ）API の統合テスト。シードを <c>mart.rebuild()</c> で mart に反映し、
/// sales 系と同一データから同じ集計値が得られること（派生整合）と、各 mart エンドポイントの
/// 契約（在庫KPI・クロス集計・ランキング・商品一覧・週次系列・値引き散布図）を検証する。
/// </summary>
[Collection("Api")]
public sealed class MartApiIntegrationTests
{
    // シード（2026-05-04 / 2026-05-11 の2週）に限定するフィルタ。取込テストが追加する
    // 6月の行に影響されないよう、シード週範囲に固定して検証する。
    private const string SeedRange = "from=2026-05-04&to=2026-05-11";

    private readonly DatabaseFixture _fixture;

    public MartApiIntegrationTests(DatabaseFixture fixture) => _fixture = fixture;

    private HttpClient CreateAuthedClient()
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuthHandler.AdminToken);
        return client;
    }

    /// <summary>mart を現在の sales_weekly から再構築する（冪等。シードは小さく即時に完了する）。</summary>
    private async Task RebuildMartAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition("SELECT mart.rebuild();", commandTimeout: 120));
    }

    [Fact]
    public async Task Mart_Status_AfterRebuild_IsBuilt()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var status = await client.GetFromJsonAsync<MartStatus>("/api/mart/status");

        Assert.NotNull(status);
        Assert.True(status!.Built);
        Assert.True(status.FactRows > 0);
    }

    [Fact]
    public async Task Mart_Summary_MatchesSalesTotals()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var summary = await client.GetFromJsonAsync<MartSummaryResponse>($"/api/mart/summary?{SeedRange}");

        Assert.NotNull(summary);
        // sales 系サマリーと同一データの派生のため、合計は一致する。
        Assert.Equal(28, summary!.Kpi.Quantity);
        Assert.Equal(36500, summary.Kpi.Amount);
        Assert.Equal(74, summary.Kpi.CurrentStock);
        Assert.Equal(2, summary.Kpi.ProductCount);
        Assert.Equal(2, summary.WeeklyTrend.Count);
    }

    [Fact]
    public async Task Mart_Summary_RatesAreFractions_MatchingSales()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var mart = await client.GetFromJsonAsync<MartSummaryResponse>($"/api/mart/summary?{SeedRange}");
        var sales = await client.GetFromJsonAsync<SummaryResponse>($"/api/summary?{SeedRange}");

        Assert.NotNull(mart);
        Assert.NotNull(sales);
        // 粗利率・消化率は比率（0..1）。共有レスポンス型は formatRatioAsPercent（×100）で描画されるため、
        // mart 側が ×100 して返すと二重スケール（"3500.0%"）になる。fraction 契約を回帰で固定する。
        // 粗利率 = 21900/36500 = 0.6。
        Assert.Equal(0.6, mart!.Kpi.GrossProfitRate, 3);
        Assert.InRange(mart.Kpi.SellThroughRate, 0.0, 1.0);
        // sales 系サマリーと同一データの派生のため、比率は一致する（fraction 同士）。
        Assert.Equal(sales!.Kpi.GrossProfitRate, mart.Kpi.GrossProfitRate, 6);
        Assert.Equal(sales.Kpi.SellThroughRate, mart.Kpi.SellThroughRate, 6);
    }

    [Fact]
    public async Task Mart_Inventory_ReturnsLatestWeekSnapshot()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var inventory = await client.GetFromJsonAsync<InventoryResponse>($"/api/mart/inventory?{SeedRange}");

        Assert.NotNull(inventory);
        Assert.Equal(74, inventory!.Kpi.TotalStock);
        Assert.Equal(2, inventory.ByDepartment.Count);
    }

    [Fact]
    public async Task Mart_Breakdown_ByDepartment_ReturnsTwoRows()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var breakdown = await client.GetFromJsonAsync<MartBreakdownResponse>(
            $"/api/mart/breakdown?dimension=department&{SeedRange}");

        Assert.NotNull(breakdown);
        Assert.Equal(2, breakdown!.Rows.Count);
    }

    [Fact]
    public async Task Mart_Ranking_ByDepartment_MatchesSalesAmounts()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var ranking = await client.GetFromJsonAsync<RankingResponse>(
            $"/api/mart/ranking?dimension=department&{SeedRange}");

        Assert.NotNull(ranking);
        Assert.Equal("Department", ranking!.Dimension);
        Assert.Equal(2, ranking.Rows.Count);
        var dept01 = ranking.Rows.Single(r => r.Key == "01");
        var dept02 = ranking.Rows.Single(r => r.Key == "02");
        Assert.Equal(17000, dept01.Current!.Amount);
        Assert.Equal(19500, dept02.Current!.Amount);
        Assert.Contains("stock", ranking.AvailableMetrics);
        Assert.NotNull(ranking.LatestWeek);
    }

    [Fact]
    public async Task Mart_Ranking_UnsupportedDimension_ReturnsBadRequest()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        // 帳票区分は mart に保持しないため未対応（400）。
        var response = await client.GetAsync($"/api/mart/ranking?dimension=chohyoKubun&{SeedRange}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Mart_Crosstab_DepartmentByHinban_ReturnsMatrixWithStock()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var response = await client.GetFromJsonAsync<CrosstabMatrixResponse>(
            $"/api/mart/crosstab?rowDimension=category:department&columnDimension=category:hinban&{SeedRange}");

        Assert.NotNull(response);
        Assert.Equal("category:department", response!.RowDimension.Key);
        Assert.Equal(2, response.RowLabels.Count);
        Assert.Equal(2, response.ColumnLabels.Count);
        // 時間軸を含まないので在庫系メトリクスが利用可能。
        Assert.Contains("stock", response.AvailableMetrics);
        Assert.NotNull(response.LatestWeek);

        // 表示セル・行合計・総計の amount 整合（共有ビルダーの整合性が mart でも保たれること）。
        long sumRowTotals = 0;
        foreach (var rl in response.RowLabels)
        {
            sumRowTotals += response.RowTotals[rl].Values.Amount ?? 0;
        }
        Assert.Equal(response.GrandTotal.Values.Amount ?? 0, sumRowTotals);
    }

    [Fact]
    public async Task Mart_Crosstab_UnsupportedDimension_ReturnsBadRequest()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        // 棚割は mart に保持しないため未対応（400）。
        var response = await client.GetAsync(
            $"/api/mart/crosstab?rowDimension=category:tanawari1&columnDimension=category:department&{SeedRange}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Mart_Crosstab_TimeAxis_DisablesStockMetrics()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var response = await client.GetFromJsonAsync<CrosstabMatrixResponse>(
            $"/api/mart/crosstab?rowDimension=category:department&columnDimension=time:month&{SeedRange}");

        Assert.NotNull(response);
        Assert.DoesNotContain("stock", response!.AvailableMetrics);
        Assert.Contains("amount", response.AvailableMetrics);
    }

    [Fact]
    public async Task Mart_Products_ReturnsSeededSkus()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var products = await client.GetFromJsonAsync<ProductPage>($"/api/mart/products?{SeedRange}");

        Assert.NotNull(products);
        // シードの SKU は (01,100,0001) / (01,100,0002) / (02,200,0001) の3件。
        Assert.Equal(3, products!.TotalCount);
        Assert.Equal(3, products.Items.Count);
    }

    [Fact]
    public async Task Mart_WeeklySeries_ReturnsPointsWithTemperature()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        var series = await client.GetFromJsonAsync<WeeklySeriesResponse>(
            $"/api/mart/weekly-series?{SeedRange}&area=standard");

        Assert.NotNull(series);
        Assert.Equal("standard", series!.Area);
        Assert.Equal("東京", series.AreaCity);
        Assert.Equal(2, series.Points.Count);
        foreach (var point in series.Points)
        {
            Assert.True(point.TempMax >= point.TempAvg);
            Assert.True(point.TempAvg >= point.TempMin);
        }
    }

    [Fact]
    public async Task Mart_Markdown_ReturnsOk()
    {
        await RebuildMartAsync();
        var client = CreateAuthedClient();

        // 商品マスタ未シードのため定価が無く点は空だが、200 で空配列を返す。
        var response = await client.GetFromJsonAsync<MarkdownScatterResponse>(
            $"/api/mart/markdown?{SeedRange}");

        Assert.NotNull(response);
        Assert.NotNull(response!.Points);
    }
}
