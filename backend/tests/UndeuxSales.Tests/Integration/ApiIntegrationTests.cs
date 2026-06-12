using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using UndeuxSales.Api;
using UndeuxSales.Core;
using UndeuxSales.Core.Parsing;
using UndeuxSales.Infrastructure.Import;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Tests.Integration;

/// <summary>
/// API のエンドポイントを実DB（テスト用）と WebApplicationFactory で検証する統合テスト。
/// PostgreSQL への接続が必要（既定: localhost:5432, undeux/undeux）。
/// </summary>
[Collection("Api")]
public sealed class ApiIntegrationTests
{
    private readonly CustomWebApplicationFactory _factory;

    public ApiIntegrationTests(DatabaseFixture fixture) => _factory = fixture.Factory;

    private HttpClient CreateAuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestAuthHandler.AdminToken);
        return client;
    }

    private HttpClient CreateMemberClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "member-token");
        return client;
    }

    [Fact]
    public async Task Health_Liveness_ReturnsOk()
    {
        var response = await _factory.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_Readiness_ReturnsOk()
    {
        var response = await _factory.CreateClient().GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ErrorCodes_ReturnsCatalog()
    {
        var response = await _factory.CreateClient().GetAsync("/api/error-codes");

        response.EnsureSuccessStatusCode();
        var codes = await response.Content.ReadFromJsonAsync<List<ErrorCodeInfo>>();
        Assert.NotNull(codes);
        Assert.NotEmpty(codes!);
    }

    [Fact]
    public async Task Summary_WithoutAuth_ReturnsUnauthorized()
    {
        var response = await _factory.CreateClient().GetAsync("/api/summary");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Summary_WithAuth_ReturnsSeededTotals()
    {
        var client = CreateAuthedClient();

        var summary = await client.GetFromJsonAsync<SummaryResponse>(
            "/api/summary?from=2026-05-04&to=2026-05-11");

        Assert.NotNull(summary);
        Assert.Equal(28, summary!.Kpi.Quantity);
        Assert.Equal(36500, summary.Kpi.Amount);
        Assert.Equal(74, summary.Kpi.CurrentStock);
        Assert.Equal(2, summary.WeeklyTrend.Count);
    }

    [Fact]
    public async Task Summary_InvalidDateRange_ReturnsBadRequest()
    {
        var client = CreateAuthedClient();

        var response = await client.GetAsync("/api/summary?from=2026-05-11&to=2026-05-04");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(ErrorCodes.InvalidDateRange.Code, error!.ErrorCode);
    }

    [Fact]
    public async Task Filters_ReturnsSeededOptions()
    {
        var client = CreateAuthedClient();

        var filters = await client.GetFromJsonAsync<FilterOptions>("/api/filters");

        Assert.NotNull(filters);
        // 件数の厳密一致ではなく包含で検証する。/api/filters は無フィルタの全選択肢を返すため、
        // 取込テスト（部門09・2026-06-01）や在庫アクションテスト（部門90・2026-01〜03の週）が
        // 投入する行でも件数が増える。xUnit のテストクラス実行順は仕様保証がないため、
        // 実行順に依存しない形で固定シード（部門01/02・2026-05の2週）の存在のみを検証する。
        Assert.Contains(filters!.Departments, d => d.Code == "01");
        Assert.Contains(filters.Departments, d => d.Code == "02");
        Assert.Contains(filters.Weeks, w => w == DateOnly.Parse("2026-05-04"));
        Assert.Contains(filters.Weeks, w => w == DateOnly.Parse("2026-05-11"));
    }

    [Fact]
    public async Task Breakdown_ByDepartment_ReturnsTwoRows()
    {
        var client = CreateAuthedClient();

        var breakdown = await client.GetFromJsonAsync<BreakdownResponse>(
            "/api/sales/breakdown?dimension=department&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(breakdown);
        Assert.Equal(2, breakdown!.Rows.Count);
    }

    [Fact]
    public async Task Breakdown_InvalidDimension_ReturnsBadRequest()
    {
        var client = CreateAuthedClient();

        var response = await client.GetAsync("/api/sales/breakdown?dimension=unknown");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(ErrorCodes.UnknownDimension.Code, error!.ErrorCode);
    }

    [Fact]
    public async Task Ranking_ByDepartment_ReturnsRows()
    {
        var client = CreateAuthedClient();

        var ranking = await client.GetFromJsonAsync<RankingResponse>(
            "/api/ranking?dimension=department&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(ranking);
        Assert.Equal("Department", ranking!.Dimension);
        // 部門は 01, 02 の 2 種類。
        Assert.Equal(2, ranking.Rows.Count);
        // 期間内集計（2週合算）: 部門01 = 7000+4000+6000 = 17000、部門02 = 4500+15000 = 19500。
        var dept01 = ranking.Rows.Single(r => r.Key == "01");
        var dept02 = ranking.Rows.Single(r => r.Key == "02");
        Assert.NotNull(dept01.Current);
        Assert.NotNull(dept02.Current);
        Assert.Equal(17000, dept01.Current!.Amount);
        Assert.Equal(19500, dept02.Current!.Amount);
        // 比較未指定なので Comparison は null。
        Assert.Null(dept01.Comparison);
        Assert.Null(dept02.Comparison);
        // 最新週スナップショットが取れるので在庫系メトリクスも利用可能。
        Assert.Contains("amount", ranking.AvailableMetrics);
        Assert.Contains("grossProfitRate", ranking.AvailableMetrics);
        Assert.Contains("stock", ranking.AvailableMetrics);
        Assert.Contains("sellThroughRate", ranking.AvailableMetrics);
        Assert.NotNull(ranking.LatestWeek);
        Assert.Null(ranking.ComparisonLatestWeek);
        Assert.False(ranking.Truncated);
    }

    [Fact]
    public async Task Ranking_WithComparison_PopulatesPreviousPeriod()
    {
        var client = CreateAuthedClient();

        // 主期間 = 2週目（05-11）、比較期間 = 1週目（05-04）。順位変動の素材を検証する。
        var ranking = await client.GetFromJsonAsync<RankingResponse>(
            "/api/ranking?dimension=department&from=2026-05-11&to=2026-05-11"
            + "&compareFrom=2026-05-04&compareTo=2026-05-04");

        Assert.NotNull(ranking);
        Assert.Equal(2, ranking!.Rows.Count);

        var dept01 = ranking.Rows.Single(r => r.Key == "01");
        Assert.NotNull(dept01.Current);
        Assert.NotNull(dept01.Comparison);
        // 部門01: 当期(05-11)=6000、前期(05-04)=7000+4000=11000。
        Assert.Equal(6000, dept01.Current!.Amount);
        Assert.Equal(11000, dept01.Comparison!.Amount);

        var dept02 = ranking.Rows.Single(r => r.Key == "02");
        Assert.NotNull(dept02.Current);
        Assert.NotNull(dept02.Comparison);
        // 部門02: 当期(05-11)=15000、前期(05-04)=4500。
        Assert.Equal(15000, dept02.Current!.Amount);
        Assert.Equal(4500, dept02.Comparison!.Amount);

        Assert.NotNull(ranking.LatestWeek);
        Assert.NotNull(ranking.ComparisonLatestWeek);
    }

    [Fact]
    public async Task Ranking_InvalidDimension_ReturnsBadRequest()
    {
        var client = CreateAuthedClient();

        var response = await client.GetAsync("/api/ranking?dimension=unknown");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(ErrorCodes.UnknownDimension.Code, error!.ErrorCode);
    }

    [Fact]
    public async Task Ranking_EmptyData_ReturnsEmptyWithoutSnapshotMetrics()
    {
        var client = CreateAuthedClient();

        // シードに含まれない期間。空行・最新週 null・在庫系メトリクス除外を確認する。
        var ranking = await client.GetFromJsonAsync<RankingResponse>(
            "/api/ranking?dimension=department&from=2099-01-01&to=2099-12-31");

        Assert.NotNull(ranking);
        Assert.Empty(ranking!.Rows);
        Assert.Null(ranking.LatestWeek);
        Assert.False(ranking.Truncated);
        // 最新週が無いのでフロー系のみ利用可能、在庫系は availableMetrics から除外。
        Assert.Contains("amount", ranking.AvailableMetrics);
        Assert.DoesNotContain("stock", ranking.AvailableMetrics);
    }

    [Fact]
    public async Task CrosstabMatrix_DepartmentByHinban_ReturnsMatrix()
    {
        var client = CreateAuthedClient();

        var response = await client.GetFromJsonAsync<CrosstabMatrixResponse>(
            "/api/crosstab?rowDimension=category:department&columnDimension=category:hinban"
            + "&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(response);
        Assert.Equal("category:department", response!.RowDimension.Key);
        Assert.Equal("category:hinban", response.ColumnDimension.Key);
        // 部門は 01, 02 の 2 種類、品番は 100, 200 の 2 種類。
        Assert.Equal(2, response.RowLabels.Count);
        Assert.Equal(2, response.ColumnLabels.Count);
        // 時間軸を含まないので在庫系メトリクスも利用可能。
        Assert.Contains("stock", response.AvailableMetrics);
        Assert.Contains("stockDays", response.AvailableMetrics);
        Assert.Contains("sellThroughRate", response.AvailableMetrics);
        Assert.NotNull(response.LatestWeek);
    }

    [Fact]
    public async Task CrosstabMatrix_BusinessTypeByYear_ReturnsMatrix()
    {
        var client = CreateAuthedClient();

        var response = await client.GetFromJsonAsync<CrosstabMatrixResponse>(
            "/api/crosstab?rowDimension=category:businessType&columnDimension=time:year"
            + "&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(response);
        Assert.Equal("category:businessType", response!.RowDimension.Key);
        Assert.Equal("time:year", response.ColumnDimension.Key);
        // 業態は G1 のみ、年は 2026 のみ（シードデータ）。
        Assert.Single(response.RowLabels);
        Assert.Single(response.ColumnLabels);
        Assert.Equal("2026", response.ColumnLabels[0]);
        // セルが正しく構築されている
        Assert.True(response.Cells.ContainsKey("G1"));
    }

    [Fact]
    public async Task CrosstabMatrix_StockMetrics_DisabledWithTimeAxis()
    {
        var client = CreateAuthedClient();

        var response = await client.GetFromJsonAsync<CrosstabMatrixResponse>(
            "/api/crosstab?rowDimension=category:hinban&columnDimension=time:month"
            + "&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(response);
        // 時間軸絡みなので在庫系メトリクスは availableMetrics に含まれない
        Assert.DoesNotContain("stock", response!.AvailableMetrics);
        Assert.DoesNotContain("stockDays", response.AvailableMetrics);
        Assert.DoesNotContain("sellThroughRate", response.AvailableMetrics);
        // 通常のフロー系は含まれる
        Assert.Contains("amount", response.AvailableMetrics);
        Assert.Contains("quantity", response.AvailableMetrics);
        Assert.Contains("grossProfit", response.AvailableMetrics);
        Assert.Contains("sharePercent", response.AvailableMetrics);

        // セル値の在庫系は null
        Assert.NotEmpty(response.RowLabels);
        Assert.NotEmpty(response.ColumnLabels);
        var firstRowLabel = response.RowLabels[0];
        var firstColLabel = response.ColumnLabels[0];
        var cell = response.Cells[firstRowLabel][firstColLabel];
        Assert.Null(cell.Values.Stock);
        Assert.Null(cell.Values.StockDays);
        Assert.Null(cell.Values.SellThroughRate);
    }

    [Fact]
    public async Task CrosstabMatrix_SameDimensionRowCol_Returns400()
    {
        var client = CreateAuthedClient();

        var response = await client.GetAsync(
            "/api/crosstab?rowDimension=category:hinban&columnDimension=category:hinban"
            + "&from=2026-05-04&to=2026-05-11");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(ErrorCodes.InvalidRequest.Code, error!.ErrorCode);
    }

    [Fact]
    public async Task Crosstab_InvalidDimension_ReturnsBadRequest()
    {
        var client = CreateAuthedClient();

        var response = await client.GetAsync(
            "/api/crosstab?rowDimension=invalid&columnDimension=category:hinban");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(ErrorCodes.UnknownDimension.Code, error!.ErrorCode);
    }

    [Fact]
    public async Task CrosstabMatrix_RowDimensionMissing_Returns400()
    {
        var client = CreateAuthedClient();

        var response = await client.GetAsync(
            "/api/crosstab?columnDimension=category:hinban&from=2026-05-04&to=2026-05-11");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(ErrorCodes.UnknownDimension.Code, error!.ErrorCode);
    }

    [Fact]
    public async Task CrosstabMatrix_InvalidDateRange_ReturnsBadRequest()
    {
        var client = CreateAuthedClient();

        // from > to の不正な日付範囲。SalesQueryFilter.EnsureValid が
        // ErrorCodes.InvalidDateRange を投げる挙動を Crosstab API でも確認する。
        var response = await client.GetAsync(
            "/api/crosstab?rowDimension=category:department&columnDimension=category:hinban"
            + "&from=2026-05-11&to=2026-05-04");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(ErrorCodes.InvalidDateRange.Code, error!.ErrorCode);
    }

    [Fact]
    public async Task CrosstabMatrix_EmptyData_ReturnsEmptyMatrix()
    {
        var client = CreateAuthedClient();

        // シードに含まれない日付範囲を指定 → 空マトリクスを 200 で返す
        var response = await client.GetFromJsonAsync<CrosstabMatrixResponse>(
            "/api/crosstab?rowDimension=category:department&columnDimension=category:hinban"
            + "&from=2099-01-01&to=2099-12-31");

        Assert.NotNull(response);
        Assert.Empty(response!.RowLabels);
        Assert.Empty(response.ColumnLabels);
        Assert.Empty(response.Cells);
        Assert.Empty(response.RowTotals);
        Assert.Empty(response.ColumnTotals);
        // grandTotal は値全 null の空セル
        Assert.Null(response.GrandTotal.Values.Amount);
        Assert.Null(response.GrandTotal.Values.Quantity);
        Assert.False(response.RowTruncated);
        Assert.False(response.ColumnTruncated);
    }

    [Fact]
    public async Task CrosstabMatrix_GrandTotalEqualsSumOfCells()
    {
        var client = CreateAuthedClient();

        var response = await client.GetFromJsonAsync<CrosstabMatrixResponse>(
            "/api/crosstab?rowDimension=category:department&columnDimension=category:hinban"
            + "&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(response);

        // 各行合計 amount の和 == grandTotal.amount を検証（切り詰め後でも整合）
        long sumRowTotals = 0;
        foreach (var rl in response!.RowLabels)
        {
            Assert.True(response.RowTotals.ContainsKey(rl));
            sumRowTotals += response.RowTotals[rl].Values.Amount ?? 0;
        }
        long sumColTotals = 0;
        foreach (var cl in response.ColumnLabels)
        {
            Assert.True(response.ColumnTotals.ContainsKey(cl));
            sumColTotals += response.ColumnTotals[cl].Values.Amount ?? 0;
        }

        var grand = response.GrandTotal.Values.Amount ?? 0;
        Assert.Equal(grand, sumRowTotals);
        Assert.Equal(grand, sumColTotals);

        // 表示セル全和 == grandTotal も検証
        long sumCells = 0;
        foreach (var rl in response.RowLabels)
        {
            if (!response.Cells.TryGetValue(rl, out var rowCells)) continue;
            foreach (var cl in response.ColumnLabels)
            {
                if (rowCells.TryGetValue(cl, out var cell))
                {
                    sumCells += cell.Values.Amount ?? 0;
                }
            }
        }
        Assert.Equal(grand, sumCells);
    }

    [Fact]
    public async Task CrosstabMatrix_UnsetLabel_AppearsAtEnd()
    {
        var client = CreateAuthedClient();

        // tanawari1 はシードでは NULL のまま（COALESCE で '' に置換 → '(未設定)' ラベル）。
        // カテゴリ軸ソートでも時間軸ソートでも '(未設定)' は末尾に来ることを確認する。
        var response = await client.GetFromJsonAsync<CrosstabMatrixResponse>(
            "/api/crosstab?rowDimension=category:tanawari1&columnDimension=category:department"
            + "&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(response);
        Assert.NotEmpty(response!.RowLabels);
        // tanawari1 は全 NULL なので '(未設定)' のみ（1件）が末尾候補
        Assert.Equal("(未設定)", response.RowLabels[^1]);
    }

    [Fact]
    public async Task CrosstabMatrix_LegacyDimension_BackwardCompat()
    {
        var client = CreateAuthedClient();

        // 既存の category:hinban / time:year が引き続き受理されることを確認
        // （フロントのレガシー互換層で `dimension=hinban` → `rowDimension=category:hinban` に
        //   変換される際の API レベルでの受理確認）
        var response = await client.GetFromJsonAsync<CrosstabMatrixResponse>(
            "/api/crosstab?rowDimension=category:hinban&columnDimension=time:year"
            + "&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(response);
        Assert.Equal("category:hinban", response!.RowDimension.Key);
        Assert.Equal("time:year", response.ColumnDimension.Key);
        // IsTimeAxis の SoT 統一: time:year は true、category:hinban は false
        Assert.False(response.RowDimension.IsTimeAxis);
        Assert.True(response.ColumnDimension.IsTimeAxis);
    }

    [Fact]
    public async Task Trend_Weekly_ReturnsTwoPoints()
    {
        var client = CreateAuthedClient();

        var trend = await client.GetFromJsonAsync<TrendResponse>(
            "/api/sales/trend?granularity=weekly&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(trend);
        Assert.Equal(2, trend!.Points.Count);
    }

    [Fact]
    public async Task Trend_Daily_ReturnsDailyPoints()
    {
        var client = CreateAuthedClient();

        var trend = await client.GetFromJsonAsync<TrendResponse>(
            "/api/sales/trend?granularity=daily&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(trend);
        Assert.Equal(14, trend!.Points.Count);
    }

    [Fact]
    public async Task Inventory_ReturnsLatestWeekSnapshot()
    {
        var client = CreateAuthedClient();

        var inventory = await client.GetFromJsonAsync<InventoryResponse>(
            "/api/inventory?from=2026-05-04&to=2026-05-11");

        Assert.NotNull(inventory);
        Assert.Equal(74, inventory!.Kpi.TotalStock);
        Assert.Equal(2, inventory.ByDepartment.Count);
    }

    [Fact]
    public async Task Products_ReturnsSeededProducts()
    {
        var client = CreateAuthedClient();

        var products = await client.GetFromJsonAsync<ProductPage>(
            "/api/products?from=2026-05-04&to=2026-05-11");

        Assert.NotNull(products);
        Assert.Equal(3, products!.TotalCount);
        Assert.Equal(3, products.Items.Count);
    }

    [Fact]
    public async Task Imports_List_ReturnsHistory()
    {
        var client = CreateAuthedClient();

        var batches = await client.GetFromJsonAsync<List<ImportBatchInfo>>("/api/imports");

        Assert.NotNull(batches);
        Assert.NotEmpty(batches!);
    }

    [Fact]
    public async Task Import_ValidCsv_Succeeds()
    {
        var client = CreateAuthedClient();
        using var content = BuildCsvUpload(BuildCsv(ValidCsvRow("2026-06-01")));

        var response = await client.PostAsync("/api/imports", content);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ImportResult>();
        Assert.Equal(1, result!.RowCount);
    }

    [Fact]
    public async Task Import_InvalidRow_ReturnsUnprocessable()
    {
        var client = CreateAuthedClient();
        var invalidRow = ValidCsvRow("2026-06-08").Replace(",500,1200,", ",abc,1200,");
        using var content = BuildCsvUpload(BuildCsv(invalidRow));

        var response = await client.PostAsync("/api/imports", content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(ErrorCodes.ImportRowInvalid.Code, error!.ErrorCode);
    }

    [Fact]
    public async Task Import_AsNonAdmin_ReturnsForbidden()
    {
        var client = CreateMemberClient();
        using var content = BuildCsvUpload(BuildCsv(ValidCsvRow("2026-06-15")));

        var response = await client.PostAsync("/api/imports", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Import_NoFile_ReturnsBadRequest()
    {
        var client = CreateAuthedClient();
        using var content = new MultipartFormDataContent
        {
            { new StringContent("placeholder"), "note" },
        };

        var response = await client.PostAsync("/api/imports", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal(ErrorCodes.ImportFileMissing.Code, error!.ErrorCode);
    }

    // ------------------------------------------------------------
    // 追加フィルタ（棚割1 / 平均在庫日数）・気温メトリクス・分析エンドポイント
    // ------------------------------------------------------------

    [Fact]
    public async Task Filters_IncludesTanawari1Field()
    {
        var client = CreateAuthedClient();

        var filters = await client.GetFromJsonAsync<FilterOptions>("/api/filters");

        Assert.NotNull(filters);
        // シードの tanawari1 は NULL のため選択肢は空だが、フィールド自体は存在する。
        Assert.NotNull(filters!.Tanawari1);
    }

    [Fact]
    public async Task Summary_StockDaysBucketGe61_ExcludesAll()
    {
        var client = CreateAuthedClient();

        // シードの在日(zainiti)は全行 30 → 61日以上バケットには該当せず売上 0。
        var summary = await client.GetFromJsonAsync<SummaryResponse>(
            "/api/summary?from=2026-05-04&to=2026-05-11&stockDaysBuckets=ge61");

        Assert.NotNull(summary);
        Assert.Equal(0, summary!.Kpi.Quantity);
    }

    [Fact]
    public async Task Summary_StockDaysBucketLe30_IncludesAll()
    {
        var client = CreateAuthedClient();

        // 在日 30 は「30日以内」バケットに該当 → 全シードが対象（売上数量 28）。
        var summary = await client.GetFromJsonAsync<SummaryResponse>(
            "/api/summary?from=2026-05-04&to=2026-05-11&stockDaysBuckets=le30");

        Assert.NotNull(summary);
        Assert.Equal(28, summary!.Kpi.Quantity);
    }

    [Fact]
    public async Task Summary_Tanawari1Filter_NonExistentValue_ReturnsZero()
    {
        var client = CreateAuthedClient();

        // シードの tanawari1 は NULL のため、任意の値で絞ると 0 件。
        var summary = await client.GetFromJsonAsync<SummaryResponse>(
            "/api/summary?from=2026-05-04&to=2026-05-11&tanawari1=NOPE");

        Assert.NotNull(summary);
        Assert.Equal(0, summary!.Kpi.Quantity);
    }

    [Fact]
    public async Task Summary_ShohinKigoFilter_Applies()
    {
        var client = CreateAuthedClient();

        // 商品記号 S100 = 品番100（数量15）。mart 系と同一契約の sales 系パリティを回帰で固定する。
        var s100 = await client.GetFromJsonAsync<SummaryResponse>(
            "/api/summary?from=2026-05-04&to=2026-05-11&shohinKigos=S100");
        var none = await client.GetFromJsonAsync<SummaryResponse>(
            "/api/summary?from=2026-05-04&to=2026-05-11&shohinKigos=S999");

        Assert.NotNull(s100);
        Assert.NotNull(none);
        Assert.Equal(15, s100!.Kpi.Quantity);
        Assert.Equal(0, none!.Kpi.Quantity);
    }

    [Fact]
    public async Task Crosstab_Temperature_AvailableWithTimeAxisAndArea()
    {
        var client = CreateAuthedClient();

        var response = await client.GetFromJsonAsync<CrosstabMatrixResponse>(
            "/api/crosstab?rowDimension=category:department&columnDimension=time:month"
            + "&temperatureArea=standard&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(response);
        // 時間軸＋エリア指定 → 気温系メトリクスが利用可能、在庫系は除外。
        Assert.Contains("tempAvg", response!.AvailableMetrics);
        Assert.Contains("tempMax", response.AvailableMetrics);
        Assert.Contains("tempMin", response.AvailableMetrics);
        Assert.DoesNotContain("stock", response.AvailableMetrics);

        // 列は 2026-05 のみ。セルの気温が標準気候（東京5月）として設定されている。
        Assert.Equal("2026-05", response.ColumnLabels[0]);
        var firstRow = response.RowLabels[0];
        var cell = response.Cells[firstRow]["2026-05"];
        Assert.NotNull(cell.Values.TempAvg);
        Assert.NotNull(cell.Values.TempMax);
        Assert.NotNull(cell.Values.TempMin);
        Assert.True(cell.Values.TempMax >= cell.Values.TempAvg);
        Assert.True(cell.Values.TempAvg >= cell.Values.TempMin);
        Assert.InRange(cell.Values.TempAvg!.Value, 10.0, 30.0); // 東京5月の妥当域
    }

    [Fact]
    public async Task Crosstab_Temperature_RequiresArea()
    {
        var client = CreateAuthedClient();

        // エリア未指定 → 時間軸があっても気温メトリクスは提供しない。
        var response = await client.GetFromJsonAsync<CrosstabMatrixResponse>(
            "/api/crosstab?rowDimension=category:department&columnDimension=time:month"
            + "&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(response);
        Assert.DoesNotContain("tempAvg", response!.AvailableMetrics);
    }

    [Fact]
    public async Task Crosstab_Temperature_RequiresTimeAxis()
    {
        var client = CreateAuthedClient();

        // 時間軸が無い（カテゴリ×カテゴリ）→ エリア指定でも気温メトリクスは提供しない。
        var response = await client.GetFromJsonAsync<CrosstabMatrixResponse>(
            "/api/crosstab?rowDimension=category:department&columnDimension=category:hinban"
            + "&temperatureArea=standard&from=2026-05-04&to=2026-05-11");

        Assert.NotNull(response);
        Assert.DoesNotContain("tempAvg", response!.AvailableMetrics);
        // 時間軸が無いので在庫系は引き続き利用可能。
        Assert.Contains("stock", response.AvailableMetrics);
    }

    [Fact]
    public async Task Analysis_WeeklySeries_ReturnsPointsWithTemperature()
    {
        var client = CreateAuthedClient();

        var series = await client.GetFromJsonAsync<WeeklySeriesResponse>(
            "/api/analysis/weekly-series?from=2026-05-04&to=2026-05-11&area=standard");

        Assert.NotNull(series);
        Assert.Equal("standard", series!.Area);
        Assert.Equal("東京", series.AreaCity);
        Assert.Equal(2, series.Points.Count);
        foreach (var point in series.Points)
        {
            Assert.True(point.TempMax >= point.TempAvg);
            Assert.True(point.TempAvg >= point.TempMin);
            Assert.InRange(point.TempAvg, 8.0, 26.0); // 東京・5月上中旬の妥当域
        }
    }

    [Fact]
    public async Task Analysis_Markdown_ReturnsOk()
    {
        var client = CreateAuthedClient();

        // マスタ未シードのため点は空だが、200 で空配列を返す（値引き率はマスタ定価が必要）。
        var response = await client.GetFromJsonAsync<MarkdownScatterResponse>(
            "/api/analysis/markdown?from=2026-05-04&to=2026-05-11");

        Assert.NotNull(response);
        Assert.NotNull(response!.Points);
    }

    private static string ValidCsvRow(string importDate)
        => $"{importDate},C900,G9,売発注,09,900,9001,テスト商品,S900,色,M,"
           + "1,0,0,0,0,0,0,0,0,0,0,5,3,5,1.0,20250101,10,500,1200,通季,0";

    private static string BuildCsv(string dataRow)
        => string.Join(",", SalesCsvReader.RequiredColumns) + "\n" + dataRow + "\n";

    private static MultipartFormDataContent BuildCsvUpload(string csv)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(fileContent, "file", "weekly.csv");
        return content;
    }
}
