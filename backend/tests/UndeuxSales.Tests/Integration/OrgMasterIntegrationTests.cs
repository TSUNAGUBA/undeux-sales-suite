using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using UndeuxSales.Api.Controllers;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Tests.Integration;

/// <summary>
/// 組織マスタ API（/api/org-master/*）の統合テスト。
/// 参照系はシード値（お取引の基準 総括編由来）を検証し、
/// 変更系は既存シードに影響しない業態「03（思夢樂）」配下でのみ行う（実行順非依存）。
/// </summary>
[Collection("Api")]
public sealed class OrgMasterIntegrationTests
{
    private readonly DatabaseFixture _fixture;

    public OrgMasterIntegrationTests(DatabaseFixture fixture) => _fixture = fixture;

    private HttpClient CreateClient(string token = TestAuthHandler.AdminToken)
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task GetTree_ReturnsSeededMasterData()
    {
        using var client = CreateClient("member-token");
        var response = await client.GetFromJsonAsync<OrgMasterTreeResponse>("/api/org-master");

        Assert.NotNull(response);
        var byCode = response!.BusinessTypes.ToDictionary(bt => bt.Code);

        // しまむら（01）: 商品1〜7部・部門23件（キャプチャ/総括編どおり）
        var shimamura = byCode["01"];
        Assert.Equal("しまむら", shimamura.DisplayName);
        Assert.Equal(7, shimamura.Sections.Count);
        Assert.Equal(23, shimamura.Departments.Count);
        var cutSaw = shimamura.Departments.Single(d => d.DeptCode == "5A");
        Assert.Equal("カットソー", cutSaw.DeptName);
        var section1 = shimamura.Sections.Single(s => s.Id == cutSaw.BuyerSectionId);
        Assert.Equal("商品1部", section1.Name);
        Assert.Equal("048-631-2151", section1.Phone);
        Assert.Equal("2F", section1.Floor);

        // アベイル（02）/ バースデイ（04）/ シャンブル（05）/ ディバロ（06）
        Assert.Equal(4, byCode["02"].Sections.Count);
        Assert.Equal(16, byCode["02"].Departments.Count);
        Assert.Equal(3, byCode["04"].Sections.Count);
        Assert.Equal(11, byCode["04"].Departments.Count);
        Assert.Equal(2, byCode["05"].Sections.Count);
        Assert.Equal(7, byCode["05"].Departments.Count);
        Assert.Equal(1, byCode["06"].Sections.Count);
        Assert.Equal(6, byCode["06"].Departments.Count);
        Assert.Equal("事業部", byCode["06"].Sections[0].Name);

        // 同一部門コードでも業態が違えば別部門（01の5A=カットソー / 02の5A=メンズ シューズ）
        Assert.Equal("メンズ シューズ", byCode["02"].Departments.Single(d => d.DeptCode == "5A").DeptName);
    }

    [Fact]
    public async Task GetContactDesks_ReturnsSeededDesks_WithChatDomains()
    {
        using var client = CreateClient("member-token");
        var response = await client.GetFromJsonAsync<ContactDeskListResponse>("/api/org-master/contact-desks");

        Assert.NotNull(response);
        Assert.True(response!.Desks.Count >= 6);
        Assert.Equal("システム2部", response.Desks.Single(d => d.ChatDomain == "system").DepartmentName);
        Assert.Equal("商品管理部", response.Desks.Single(d => d.ChatDomain == "quality").DepartmentName);
        Assert.Equal("物流部", response.Desks.Single(d => d.ChatDomain == "logistics").DepartmentName);
    }

    [Fact]
    public async Task SaveSection_RequiresOwnerRole()
    {
        using var member = CreateClient("member-token");
        var response = await member.PostAsJsonAsync("/api/org-master/sections/save",
            new { businessTypeCode = "03", name = "テスト部", displayOrder = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SectionAndDepartment_CrudLifecycle()
    {
        using var client = CreateClient();

        // 作成（業態03=思夢樂はシードに部門を持たないため他テストと干渉しない）
        var createSection = await client.PostAsJsonAsync("/api/org-master/sections/save",
            new { businessTypeCode = "03", name = "商品テスト部", categoryNote = "検証", phone = "000-000-0000", floor = "9F", displayOrder = 1 });
        createSection.EnsureSuccessStatusCode();
        var section = await createSection.Content.ReadFromJsonAsync<OrgSection>();
        Assert.NotNull(section);
        Assert.Equal("商品テスト部", section!.Name);

        // 更新
        var updateSection = await client.PostAsJsonAsync("/api/org-master/sections/save",
            new { id = section.Id, businessTypeCode = "03", name = "商品テスト部", categoryNote = "更新済", phone = "000-000-0001", floor = "9F", displayOrder = 2 });
        updateSection.EnsureSuccessStatusCode();
        var updated = await updateSection.Content.ReadFromJsonAsync<OrgSection>();
        Assert.Equal("更新済", updated!.CategoryNote);

        // 部門作成（セクション紐づけ）
        var createDept = await client.PostAsJsonAsync("/api/org-master/departments/save",
            new { businessTypeCode = "03", deptCode = "9Z", deptName = "検証部門", buyerSectionId = section.Id, displayOrder = 1 });
        createDept.EnsureSuccessStatusCode();
        var department = await createDept.Content.ReadFromJsonAsync<OrgDepartment>();
        Assert.Equal(section.Id, department!.BuyerSectionId);

        // セクション削除 → 部門は所属未設定で温存される
        var deleteSection = await client.PostAsJsonAsync("/api/org-master/sections/delete", new { id = section.Id });
        deleteSection.EnsureSuccessStatusCode();

        var tree = await client.GetFromJsonAsync<OrgMasterTreeResponse>("/api/org-master");
        var simuraku = tree!.BusinessTypes.Single(bt => bt.Code == "03");
        var orphan = simuraku.Departments.Single(d => d.DeptCode == "9Z");
        Assert.Null(orphan.BuyerSectionId);

        // 部門削除（後片付け）
        var deleteDept = await client.PostAsJsonAsync("/api/org-master/departments/delete", new { id = department.Id });
        deleteDept.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SaveDepartment_DuplicateCodeInSameBusinessType_Returns400()
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/org-master/departments/save",
            new { businessTypeCode = "01", deptCode = "5A", deptName = "重複テスト", displayOrder = 99 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SaveDepartment_SectionFromDifferentBusinessType_Returns400()
    {
        using var client = CreateClient();

        // 業態01の商品部を、業態03の部門の所属先に指定する（業態越境）→ 400
        var tree = await client.GetFromJsonAsync<OrgMasterTreeResponse>("/api/org-master");
        var foreignSection = tree!.BusinessTypes.Single(bt => bt.Code == "01").Sections[0];

        var response = await client.PostAsJsonAsync("/api/org-master/departments/save",
            new { businessTypeCode = "03", deptCode = "9Y", deptName = "越境テスト", buyerSectionId = foreignSection.Id, displayOrder = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("同じ業態", body);

        // 部門は作成されていないこと（バリデーションが INSERT より先に効いている）
        var after = await client.GetFromJsonAsync<OrgMasterTreeResponse>("/api/org-master");
        Assert.DoesNotContain(after!.BusinessTypes.Single(bt => bt.Code == "03").Departments, d => d.DeptCode == "9Y");
    }

    [Fact]
    public async Task SchemaReapply_DoesNotResurrectOperatorDeletedSeedRows()
    {
        // デプロイごとの schema.sql 再適用（DataLoader）で、運用者が削除したシード行が
        // 復活しないこと（空テーブルガード）のリグレッションテスト。原則2（状態保護）。
        await using var connection = new Npgsql.NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        // シード済みの相談受付デスク1件を退避してから削除（運用者による削除を再現）
        var deleted = await connection.QuerySingleAsync<ContactDeskBackupRow>("""
            SELECT topic AS Topic, department_name AS DepartmentName, phone AS Phone,
                   chat_domain AS ChatDomain, display_order AS DisplayOrder
            FROM m_contact_desk WHERE topic = '消耗品';
            """);
        await connection.ExecuteAsync("DELETE FROM m_contact_desk WHERE topic = '消耗品';");
        var countAfterDelete = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM m_contact_desk;");

        try
        {
            // デプロイ相当の schema.sql 再適用
            await _fixture.ApplySchemaAsync();

            // 削除した行が復活していないこと・他の行が巻き戻っていないこと
            var resurrected = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*)::int FROM m_contact_desk WHERE topic = '消耗品';");
            Assert.Equal(0, resurrected);
            Assert.Equal(countAfterDelete,
                await connection.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM m_contact_desk;"));

            // 組織マスタ側（商品部・部門）も件数が巻き戻っていないこと
            Assert.Equal(17, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM m_buyer_section;"));
            Assert.Equal(63, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*)::int FROM m_section_department;"));
        }
        finally
        {
            // 後片付け: 退避しておいた行を復元し、他テストへ影響を残さない
            await connection.ExecuteAsync("""
                INSERT INTO m_contact_desk (topic, department_name, phone, chat_domain, display_order)
                VALUES (@Topic, @DepartmentName, @Phone, @ChatDomain, @DisplayOrder)
                ON CONFLICT (topic) DO NOTHING;
                """, deleted);
        }
    }

    [Fact]
    public async Task DeleteSection_UnknownId_Returns404()
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/org-master/sections/delete", new { id = 99_999_999L });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>シード行の退避・復元用（Dapper は ValueTuple をパラメータに使えないため record で受ける）。</summary>
    private sealed record ContactDeskBackupRow(
        string Topic, string DepartmentName, string? Phone, string? ChatDomain, int DisplayOrder);
}
