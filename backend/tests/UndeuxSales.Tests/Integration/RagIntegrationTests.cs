using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UndeuxSales.Api.Controllers;
using UndeuxSales.Core.Rag;
using UndeuxSales.Infrastructure.Database;
using UndeuxSales.Infrastructure.Queries;
using UndeuxSales.Infrastructure.Rag;

namespace UndeuxSales.Tests.Integration;

/// <summary>
/// RAG ナレッジ管理 API（/api/rag/*）・チャット API（/api/chat/*）・シーダーの統合テスト。
/// エントリはテストごとに一意なタイトルを使い、件数の絶対値に依存しない（実行順非依存）。
/// </summary>
[Collection("Api")]
public sealed class RagIntegrationTests
{
    private readonly DatabaseFixture _fixture;

    public RagIntegrationTests(DatabaseFixture fixture) => _fixture = fixture;

    private HttpClient CreateClient(string token = TestAuthHandler.AdminToken)
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Status_ReportsAiNotConfigured_AndEmbeddingModel()
    {
        using var client = CreateClient("member-token");
        var status = await client.GetFromJsonAsync<RagStatusResponse>("/api/rag/status");

        Assert.NotNull(status);
        Assert.False(status!.AiConfigured);
        Assert.Equal(HashingVectorizer.ModelName, status.EmbeddingModel);
    }

    [Fact]
    public async Task TextKnowledge_CrudAndSearchLifecycle()
    {
        using var client = CreateClient("member-token");
        var marker = $"藍染めデニム試験{Guid.NewGuid():N}";

        // 登録（ユーザースコープは一般ユーザーでも可能）
        var create = await client.PostAsJsonAsync("/api/rag/knowledge", new
        {
            scope = "user",
            category = "basic",
            title = $"検証ナレッジ {marker}",
            content = $"{marker} は当社の新商品で、藍染めデニム素材のワイドパンツです。最低ロットは100枚です。",
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<KnowledgeDetail>();
        Assert.NotNull(created);
        Assert.Equal("indexed", created!.IndexState);
        Assert.True(created.ChunkCount >= 1);
        Assert.False(created.IsSeed);

        // 一覧（検索語で絞込）
        var list = await client.GetFromJsonAsync<KnowledgeListResult>(
            $"/api/rag/knowledge?scope=user&search={Uri.EscapeDataString(marker)}");
        Assert.Equal(1, list!.Total);
        Assert.Equal(created.Id, list.Items[0].Id);

        // RAG 検索（ハイブリッド）で見つかる
        var search = await client.GetFromJsonAsync<RagSearchResponse>(
            $"/api/rag/search?query={Uri.EscapeDataString(marker + " の最低ロット")}");
        Assert.Contains(search!.Results, r => r.EntryId == created.Id);

        // 更新（本文変更 → 再インデックス）
        var update = await client.PostAsJsonAsync($"/api/rag/knowledge/{created.Id}/update", new
        {
            content = $"{marker} の最低ロットは500枚に変更されました。",
        });
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<KnowledgeDetail>();
        Assert.Contains("500枚", updated!.Content);
        Assert.Equal("indexed", updated.IndexState);

        // 再インデックス（明示）
        var reindex = await client.PostAsJsonAsync($"/api/rag/knowledge/{created.Id}/reindex", new { });
        reindex.EnsureSuccessStatusCode();

        // 削除（手動登録は物理削除）→ 取得 404
        var delete = await client.PostAsJsonAsync($"/api/rag/knowledge/{created.Id}/delete", new { });
        delete.EnsureSuccessStatusCode();
        var get = await client.GetAsync($"/api/rag/knowledge/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task OperatorScope_RequiresAdminRole()
    {
        using var member = CreateClient("member-token");
        var denied = await member.PostAsJsonAsync("/api/rag/knowledge", new
        {
            scope = "operator",
            category = "basic",
            title = "運営者ナレッジ",
            content = "テスト",
        });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var admin = CreateClient();
        var allowed = await admin.PostAsJsonAsync("/api/rag/knowledge", new
        {
            scope = "operator",
            category = "basic",
            title = $"運営者ナレッジ {Guid.NewGuid():N}",
            content = "運営者による登録テスト。",
        });
        allowed.EnsureSuccessStatusCode();
        var created = await allowed.Content.ReadFromJsonAsync<KnowledgeDetail>();

        // 運営者スコープのエントリは一般ユーザーからは変更できない
        var memberUpdate = await member.PostAsJsonAsync(
            $"/api/rag/knowledge/{created!.Id}/update", new { title = "改ざん" });
        Assert.Equal(HttpStatusCode.Forbidden, memberUpdate.StatusCode);

        await admin.PostAsJsonAsync($"/api/rag/knowledge/{created.Id}/delete", new { });
    }

    [Fact]
    public async Task ManualCategory_RejectedForUserScope()
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/rag/knowledge", new
        {
            scope = "user",
            category = "manual",
            title = "マニュアル",
            content = "テスト",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FileKnowledge_UploadExtractAndDownload()
    {
        using var client = CreateClient("member-token");
        var marker = $"直流納品手順{Guid.NewGuid():N}";
        var fileBytes = Encoding.UTF8.GetBytes($"# 手順書\n{marker} の納品は事前予約が必要です。");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("user"), "scope");
        form.Add(new StringContent("basic"), "category");
        form.Add(new StringContent($"アップロード検証 {marker}"), "title");
        var filePart = new ByteArrayContent(fileBytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(filePart, "file", "tejun.md");

        var create = await client.PostAsync("/api/rag/knowledge", form);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<KnowledgeDetail>();
        Assert.Equal("file", created!.SourceType);
        Assert.Equal("tejun.md", created.FileName);
        Assert.Equal("indexed", created.IndexState);
        Assert.Contains(marker, created.Content);

        // 原本ダウンロード（バイト一致）
        var download = await client.GetAsync($"/api/rag/knowledge/{created.Id}/file");
        download.EnsureSuccessStatusCode();
        Assert.Equal(fileBytes, await download.Content.ReadAsByteArrayAsync());

        await client.PostAsJsonAsync($"/api/rag/knowledge/{created.Id}/delete", new { });
    }

    [Fact]
    public async Task FileKnowledge_RejectsUnsupportedExtension()
    {
        using var client = CreateClient("member-token");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("user"), "scope");
        form.Add(new StringContent("basic"), "category");
        form.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "macro.xlsm");

        var response = await client.PostAsync("/api/rag/knowledge", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TextKnowledge_UnknownBusinessTypeCode_Returns400()
    {
        // FK 違反（存在しない業態コード）は 500 ではなく想定エラー 400 に変換される
        using var client = CreateClient("member-token");
        var response = await client.PostAsJsonAsync("/api/rag/knowledge", new
        {
            scope = "user",
            category = "business_type",
            businessTypeCode = "99",
            title = "存在しない業態の検証",
            content = "本文",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("業態コード", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FileKnowledge_ImageOverSizeLimit_Returns413()
    {
        // 画像は 5MB 上限（Vertex AI（Gemini）の画像上限内。他形式の 20MB より厳しい）
        using var client = CreateClient("member-token");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("user"), "scope");
        form.Add(new StringContent("basic"), "category");
        form.Add(new ByteArrayContent(new byte[5 * 1024 * 1024 + 1]), "file", "big.png");

        var response = await client.PostAsync("/api/rag/knowledge", form);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task FileKnowledge_FailedExtraction_ReindexRetriesAndKeepsFailedState()
    {
        // 抽出失敗（破損PDF）＋説明ありのエントリを再インデックスした場合:
        // 原本からの抽出を再試行し、失敗のままなら indexed に戻さない（回復パスの保証）。
        // 説明文が重複混入しないこと（冪等）も確認する。
        using var client = CreateClient("member-token");
        var description = $"破損PDF検証{Guid.NewGuid():N} の説明文";

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("user"), "scope");
        form.Add(new StringContent("basic"), "category");
        form.Add(new StringContent("破損PDFの再インデックス検証"), "title");
        form.Add(new StringContent(description), "description");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("this is not a pdf")), "file", "broken.pdf");

        var create = await client.PostAsync("/api/rag/knowledge", form);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<KnowledgeDetail>();
        Assert.Equal("failed", created!.IndexState);
        Assert.Equal(description, created.Content);

        var reindex = await client.PostAsJsonAsync($"/api/rag/knowledge/{created.Id}/reindex", new { });
        reindex.EnsureSuccessStatusCode();
        var reindexed = await reindex.Content.ReadFromJsonAsync<KnowledgeDetail>();
        Assert.Equal("failed", reindexed!.IndexState);
        Assert.Equal(description, reindexed.Content);

        await client.PostAsJsonAsync($"/api/rag/knowledge/{created.Id}/delete", new { });
    }

    [Fact]
    public async Task BusinessChatSearchScope_FiltersByDomainTag()
    {
        using var client = CreateClient();
        var marker = Guid.NewGuid().ToString("N");

        foreach (var (domain, text) in new[] { ("system", "システム"), ("logistics", "物流") })
        {
            var create = await client.PostAsJsonAsync("/api/rag/knowledge", new
            {
                scope = "operator",
                category = "manual",
                bizDomain = domain,
                title = $"{text}検証 {marker}",
                content = $"{marker} に関する{text}部門向けの手順です。",
            });
            create.EnsureSuccessStatusCode();
        }

        var search = await client.GetFromJsonAsync<RagSearchResponse>(
            $"/api/rag/search?query={Uri.EscapeDataString(marker)}&mode=business&domain=system");
        Assert.NotNull(search);
        Assert.Contains(search!.Results, r => r.Title.StartsWith("システム検証"));
        Assert.DoesNotContain(search.Results, r => r.Title.StartsWith("物流検証"));
    }

    [Fact]
    public async Task Chat_WithoutAiConfiguration_Returns503()
    {
        using var client = CreateClient("member-token");
        var response = await client.PostAsJsonAsync("/api/chat/business", new
        {
            domain = "system",
            messages = new[] { new { role = "user", content = "Web-EDIとは？" } },
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("UNDX-AI-008", body);
    }

    [Fact]
    public async Task BusinessChat_WithFakeAi_StreamsSseEvents()
    {
        using var factory = WithFakeAi();
        using var client = CreateFactoryClient(factory, "member-token");

        var response = await client.PostAsJsonAsync("/api/chat/business", new
        {
            domain = "quality",
            messages = new[] { new { role = "user", content = "品質規定の概要を教えてください" } },
        });

        response.EnsureSuccessStatusCode();
        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType!.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: sources", body);
        Assert.Contains("event: delta", body);
        Assert.Contains("これはテスト応答です", body);
        Assert.Contains("event: done", body);
    }

    [Fact]
    public async Task NegotiationChat_WithFakeAi_ValidatesDepartmentAndStreams()
    {
        using var factory = WithFakeAi();
        using var client = CreateFactoryClient(factory, "member-token");

        // 未登録の部門は 400
        var invalid = await client.PostAsJsonAsync("/api/chat/negotiation", new
        {
            businessTypeCode = "01",
            deptCode = "ZZ",
            messages = new[] { new { role = "user", content = "新商品のご提案です" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        // しまむら 5A（カットソー）は成功しストリーミングされる
        var response = await client.PostAsJsonAsync("/api/chat/negotiation", new
        {
            businessTypeCode = "01",
            deptCode = "5A",
            messages = new[] { new { role = "user", content = "カットソーの新商品のご提案です" } },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: delta", body);
        Assert.Contains("event: done", body);
    }

    [Fact]
    public async Task Seeder_IsIdempotent_AndGeneratesOrgKnowledge()
    {
        var connectionFactory = new NpgsqlConnectionFactory(_fixture.ConnectionString);
        await using (connectionFactory)
        {
            var repository = new KnowledgeRepository(connectionFactory);
            var seeder = new KnowledgeSeeder(
                repository, new OrgMasterRepository(connectionFactory), NullLogger<KnowledgeSeeder>.Instance);

            var slug = $"seed-test-{Guid.NewGuid():N}";
            var sources = new[]
            {
                new KnowledgeSeedSource(
                    slug, "シード検証", "basic", null, null, null, "test.pdf", "シード投入の検証用本文です。"),
            };

            await seeder.SeedAsync(sources, CancellationToken.None);
            var first = await repository.FindBySeedSlugAsync(slug);
            Assert.NotNull(first);

            // 再実行しても重複せず、既存エントリの内容は巻き戻らない（原則2）
            await seeder.SeedAsync(sources, CancellationToken.None);
            var second = await repository.FindBySeedSlugAsync(slug);
            Assert.Equal(first!.Value.Id, second!.Value.Id);

            // マスタ自動生成ナレッジが存在し、シード由来の内容（業態・連絡先）を含む
            var org = await repository.FindBySeedSlugAsync(KnowledgeSeeder.OrgSeedSlug);
            Assert.NotNull(org);
            var detail = await repository.GetAsync(org!.Value.Id);
            Assert.NotNull(detail);
            Assert.Contains("しまむら", detail!.Content);
            Assert.Contains("048-631-2151", detail.Content);
            Assert.Contains("システム2部", detail.Content);

            // シード由来の削除は論理削除で、GetAsync からは見えなくなる
            await repository.DeleteAsync(first.Value.Id, "tester");
            Assert.Null(await repository.GetAsync(first.Value.Id));

            // 再シードしても復活しない（原則2）
            await seeder.SeedAsync(sources, CancellationToken.None);
            Assert.Null(await repository.GetAsync(first.Value.Id));
        }
    }

    /// <summary>IAiChatClient を擬似クライアントへ差し替えたファクトリを生成する。</summary>
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithFakeAi()
        => _fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IAiChatClient, FakeAiChatClient>()));

    private static HttpClient CreateFactoryClient(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <summary>チャット統合テスト用の擬似 AI クライアント（決定的な応答を返す）。</summary>
public sealed class FakeAiChatClient : IAiChatClient
{
    public bool IsConfigured => true;

    public string ChatModel => "fake-model";

    public async IAsyncEnumerable<AiStreamEvent> StreamChatAsync(
        AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return AiStreamEvent.Delta("これはテスト応答です。");
        yield return AiStreamEvent.Delta("system ブロック数=" + request.System.Count);
        yield return AiStreamEvent.Done(100, 10);
    }

    public Task<string> DescribeImageAsync(
        byte[] imageData, string mediaType, string? hint, CancellationToken cancellationToken)
        => Task.FromResult("テスト画像の説明");

    public Task<string> AnalyzeImagesAsync(
        IReadOnlyList<AiImageInput> images, string systemPrompt, string userPrompt,
        int maxTokens, CancellationToken cancellationToken)
        => Task.FromResult("""
            { "findings": [ { "category": "content", "severity": "pass",
                "title": "テスト確認", "detail": "テスト応答", "suggestion": null, "evidence": null } ] }
            """);
}
