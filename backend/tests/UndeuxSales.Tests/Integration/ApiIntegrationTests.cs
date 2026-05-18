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
        Assert.Equal(2, filters!.Departments.Count);
        Assert.Equal(2, filters.Weeks.Count);
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
