using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UndeuxSales.Core;
using UndeuxSales.Core.Rag;
using UndeuxSales.Core.SubsidiaryCheck;
using UndeuxSales.Infrastructure.Queries;
using UndeuxSales.Infrastructure.SubsidiaryCheck;

namespace UndeuxSales.Tests.Integration;

/// <summary>
/// 副資材チェック API（/api/subsidiary-check/*）の統合テスト。
/// IAiChatClient をテスト用スタブ（固定 JSON 応答）へ差し替えて、一覧→作成→判定→詳細→画像の
/// 一連のフロー・検証エラー・404・AI 未設定時 503・再実行（手動回復パス）を検証する。
/// 件数はテスト実行順に依存しないよう差分（前後比較）で検証する。
/// </summary>
[Collection("Api")]
public sealed class SubsidiaryCheckIntegrationTests
{
    /// <summary>3カテゴリすべて pass の固定応答（判定 pass を導く）。</summary>
    private const string AllPassResponse = """
        { "findings": [
            { "category": "layout", "severity": "pass", "title": "表面の並び",
              "detail": "商品名→アイコン→サイズ表記の順で一致しています。", "suggestion": null, "evidence": "指示書1枚目" },
            { "category": "order", "severity": "pass", "title": "表示の順序",
              "detail": "品番→サイズ→混率→洗濯表示→原産国表示→製造者の順で一致しています。",
              "suggestion": null, "evidence": "指示書の表示順序欄" },
            { "category": "content", "severity": "pass", "title": "記載内容",
              "detail": "品番・原産国・組成の記載が指示書と一致しています。", "suggestion": null, "evidence": null }
        ] }
        """;

    private readonly DatabaseFixture _fixture;

    public SubsidiaryCheckIntegrationTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Lifecycle_CreateCompletesWithPass_ThenDetailAndImageAreServed()
    {
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        // 一覧はチェック未登録でも空ページとして応答する（件数は実行順非依存の差分で検証）。
        var before = await client.GetFromJsonAsync<SubsidiaryCheckPage>("/api/subsidiary-check");
        Assert.NotNull(before);

        var instructionBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0x01, 0x02 };
        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction1.jpg", "image/jpeg", instructionBytes);
        AddImage(form, "tagImages", "tag1.png", "image/png", PngBytes(0x01));
        AddImage(form, "tagImages", "tag2.png", "image/png", PngBytes(0x02));
        form.Add(new StringContent("TAG-3943 スリッパ"), "productLabel");

        var create = await client.PostAsync("/api/subsidiary-check", form);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();

        Assert.NotNull(created);
        Assert.Equal(SubsidiaryCheckStatus.Completed, created!.Summary.Status);
        Assert.Equal(SubsidiaryCheckSeverity.Pass, created.Summary.Judgment);
        Assert.Equal(0, created.Summary.FailCount);
        Assert.Equal(0, created.Summary.WarnCount);
        Assert.Equal(3, created.Summary.FindingCount);
        Assert.Equal(1, created.Summary.InstructionImageCount);
        Assert.Equal(2, created.Summary.TagImageCount);
        Assert.Equal("TAG-3943 スリッパ", created.Summary.ProductLabel);
        Assert.Equal("fake-subsidiary-model", created.Summary.AiModel);
        Assert.Null(created.Summary.ErrorMessage);
        Assert.NotNull(created.Summary.CheckedAt);
        Assert.Equal("tester@example.com", created.Summary.CreatedBy);
        Assert.Null(created.Product);
        Assert.Equal(3, created.Findings.Count);
        Assert.Equal(
            new[] { SubsidiaryCheckCategory.Layout, SubsidiaryCheckCategory.Order, SubsidiaryCheckCategory.Content },
            created.Findings.Select(f => f.Category).ToArray());
        Assert.Equal(3, created.Images.Count);
        Assert.Single(created.Images, i => i.Kind == SubsidiaryCheckImageKind.Instruction);
        Assert.Equal(2, created.Images.Count(i => i.Kind == SubsidiaryCheckImageKind.Tag));

        // 一覧: 1件増え、最新（先頭）が作成したチェックになる（作成日時降順）。
        var after = await client.GetFromJsonAsync<SubsidiaryCheckPage>("/api/subsidiary-check");
        Assert.Equal(before!.TotalCount + 1, after!.TotalCount);
        Assert.Equal(created.Summary.CheckId, after.Items[0].CheckId);
        Assert.Equal(3, after.Items[0].FindingCount);

        // 詳細取得
        var detail = await client.GetFromJsonAsync<SubsidiaryCheckDetail>(
            $"/api/subsidiary-check/{created.Summary.CheckId}");
        Assert.Equal(created.Summary.CheckId, detail!.Summary.CheckId);
        Assert.Equal(SubsidiaryCheckSeverity.Pass, detail.Summary.Judgment);

        // 画像バイナリ取得（バイト一致・Content-Type・ファイル名）
        var instructionImage = detail.Images.Single(i => i.Kind == SubsidiaryCheckImageKind.Instruction);
        var download = await client.GetAsync(
            $"/api/subsidiary-check/{created.Summary.CheckId}/images/{instructionImage.ImageId}");
        download.EnsureSuccessStatusCode();
        Assert.Equal("image/jpeg", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal(instructionBytes, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Create_WithProduct_IncludesAttachment_AndProductMasterDetailExposesIt()
    {
        var productId = await InsertProductWithAttachmentAsync();
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.jpg", "image/jpeg", JpegBytes(0x01));
        AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));
        form.Add(new StringContent(productId.ToString()), "productId");

        var create = await client.PostAsync("/api/subsidiary-check", form);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();

        Assert.NotNull(created!.Product);
        Assert.Equal(productId, created.Product!.ProductId);
        Assert.NotNull(created.Product.Attachment);
        Assert.Equal("綿 100%", created.Product.Attachment!.Composition);
        Assert.Equal("MADE IN CHINA", created.Product.Attachment.OriginCountry);
        // productLabel 未指定時は商品マスタから導出したスナップショットが保存される
        Assert.Contains("検証用ルームシューズ", created.Summary.ProductLabel);
        Assert.Equal(productId, created.Summary.ProductId);

        // 商品マスタ詳細 API にも付属情報が追加フィールドとして載る（下位互換の追加のみ・原則7）
        var master = await client.GetFromJsonAsync<MasterProductDetail>($"/api/product-master/{productId}");
        Assert.NotNull(master!.Attachment);
        Assert.Equal("綿 100%", master.Attachment!.Composition);
        Assert.Equal("品番,サイズ,混率,洗濯表示,色落ち表示等,原産国表示,製造者", master.Attachment.DisplayOrder);
    }

    [Fact]
    public async Task Create_MissingTagImages_Returns400WithErrorCode()
    {
        var stub = new SubsidiaryCheckFakeAiClient();
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.jpg", "image/jpeg", JpegBytes(0x01));

        var response = await client.PostAsync("/api/subsidiary-check", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("UNDX-REQ-004", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_TooManyInstructionImages_Returns400WithErrorCode()
    {
        var stub = new SubsidiaryCheckFakeAiClient();
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        using var form = new MultipartFormDataContent();
        for (var i = 0; i < 4; i++)
        {
            AddImage(form, "instructionImages", $"instruction{i}.jpg", "image/jpeg", JpegBytes((byte)i));
        }

        AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));

        var response = await client.PostAsync("/api/subsidiary-check", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("UNDX-REQ-007", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_UnsupportedImageFormat_Returns400WithErrorCode()
    {
        var stub = new SubsidiaryCheckFakeAiClient();
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.gif", "image/gif", new byte[] { 1 });
        AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));

        var response = await client.PostAsync("/api/subsidiary-check", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("UNDX-REQ-005", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Detail_UnknownOrMalformedId_Returns404WithErrorCode()
    {
        using var client = CreateClient(_fixture.Factory);

        var unknown = await client.GetAsync($"/api/subsidiary-check/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("UNDX-DATA-005", await unknown.Content.ReadAsStringAsync());

        var malformed = await client.GetAsync("/api/subsidiary-check/not-a-guid");
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [Fact]
    public async Task Create_WithoutAiConfiguration_Returns503_AndPersistsNothing()
    {
        var stub = new SubsidiaryCheckFakeAiClient { Configured = false };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        var before = await client.GetFromJsonAsync<SubsidiaryCheckPage>("/api/subsidiary-check");

        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.jpg", "image/jpeg", JpegBytes(0x01));
        AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));

        var response = await client.PostAsync("/api/subsidiary-check", form);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("UNDX-AI-008", await response.Content.ReadAsStringAsync());

        // 永続化前に 503 を返すため、無駄なレコードは作られない
        var after = await client.GetFromJsonAsync<SubsidiaryCheckPage>("/api/subsidiary-check");
        Assert.Equal(before!.TotalCount, after!.TotalCount);
    }

    [Fact]
    public async Task FailedCheck_CanBeRerun_AndCompletedCheckIsProtected()
    {
        // 解析不能な応答 → failed 記録（エラーは throw されず failed 詳細が返る）
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = "この応答は JSON ではありません。" };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.jpg", "image/jpeg", JpegBytes(0x01));
        AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));

        var create = await client.PostAsync("/api/subsidiary-check", form);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();

        Assert.Equal(SubsidiaryCheckStatus.Failed, created!.Summary.Status);
        Assert.Null(created.Summary.Judgment);
        Assert.Contains("UNDX-AI-009", created.Summary.ErrorMessage ?? string.Empty);
        Assert.Empty(created.Findings);
        // 失敗しても登録済みの記録・画像は残る（rerun での回復に必要）
        Assert.Equal(2, created.Images.Count);

        // 応答を正常化して再実行（手動回復パス）→ completed / pass へ回復する
        stub.ResponseText = AllPassResponse;
        var rerun = await client.PostAsync($"/api/subsidiary-check/{created.Summary.CheckId}/rerun", content: null);
        rerun.EnsureSuccessStatusCode();
        var rerunDetail = await rerun.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();

        Assert.Equal(SubsidiaryCheckStatus.Completed, rerunDetail!.Summary.Status);
        Assert.Equal(SubsidiaryCheckSeverity.Pass, rerunDetail.Summary.Judgment);
        Assert.Null(rerunDetail.Summary.ErrorMessage);
        Assert.Equal(3, rerunDetail.Findings.Count);

        // completed への再実行は記録保護（原則2）のため 400
        var protectedRerun = await client.PostAsync(
            $"/api/subsidiary-check/{created.Summary.CheckId}/rerun", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, protectedRerun.StatusCode);
        Assert.Contains("UNDX-REQ-001", await protectedRerun.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_TotalSizeOverLimit_Returns413WithErrorCode()
    {
        var stub = new SubsidiaryCheckFakeAiClient();
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        // 各画像は 5MB 以下（各上限内）だが、合計 21MB > 20MB（MaxTotalImageBytes）で拒否される。
        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.jpg", "image/jpeg",
            LargeImage(JpegBytes(0x01), 5 * 1024 * 1024));
        for (var i = 0; i < 4; i++)
        {
            AddImage(form, "tagImages", $"tag{i}.png", "image/png",
                LargeImage(PngBytes((byte)i), 4 * 1024 * 1024));
        }

        var response = await client.PostAsync("/api/subsidiary-check", form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("UNDX-REQ-008", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_ContentTypeMagicMismatch_Returns400WithErrorCode()
    {
        var stub = new SubsidiaryCheckFakeAiClient();
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        // Content-Type の申告は image/jpeg だが中身は PNG（マジックバイト不一致）→ UNDX-REQ-005。
        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "fake.jpg", "image/jpeg", PngBytes(0x01));
        AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x02));

        var response = await client.PostAsync("/api/subsidiary-check", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("UNDX-REQ-005", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rerun_StaleProcessing_IsAllowed_AndFreshProcessingIsRejected()
    {
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        // 作成から10分超経過した processing 孤児（プロセスクラッシュ等の想定）は再実行で回復できる。
        var staleId = await InsertProcessingCheckAsync(TimeSpan.FromMinutes(11));
        var rerun = await client.PostAsync($"/api/subsidiary-check/{staleId}/rerun", content: null);
        rerun.EnsureSuccessStatusCode();
        var detail = await rerun.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();
        Assert.Equal(SubsidiaryCheckStatus.Completed, detail!.Summary.Status);
        Assert.Equal(SubsidiaryCheckSeverity.Pass, detail.Summary.Judgment);

        // 新しい（実行中の可能性がある）processing は従来どおり 400 で拒否される。
        var freshId = await InsertProcessingCheckAsync(TimeSpan.Zero);
        var rejected = await client.PostAsync($"/api/subsidiary-check/{freshId}/rerun", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("UNDX-REQ-001", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UpdateResult_CompletedCheck_IsProtectedFromOverwrite()
    {
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.jpg", "image/jpeg", JpegBytes(0x01));
        AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));
        var create = await client.PostAsync("/api/subsidiary-check", form);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();
        Assert.Equal(SubsidiaryCheckStatus.Completed, created!.Summary.Status);

        // completed へ failed を書き込もうとしても状態遷移ガード（記録保護・原則2）で 0 行更新になる。
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<SubsidiaryCheckRepository>();
        var updated = await repository.UpdateResultAsync(
            created.Summary.CheckId, SubsidiaryCheckStatus.Failed, judgment: null,
            failCount: 0, warnCount: 0, findingsJson: null, aiModel: "overwrite-model",
            errorMessage: "上書きテスト");

        Assert.Equal(0, updated);
        var detail = await client.GetFromJsonAsync<SubsidiaryCheckDetail>(
            $"/api/subsidiary-check/{created.Summary.CheckId}");
        Assert.Equal(SubsidiaryCheckStatus.Completed, detail!.Summary.Status);
        Assert.Equal(SubsidiaryCheckSeverity.Pass, detail.Summary.Judgment);
        Assert.Null(detail.Summary.ErrorMessage);
    }

    [Fact]
    public async Task Create_AiTimeout_RecordsFailedDetail_WithTimeoutMessage()
    {
        // AI 呼出がタイムアウト（OperationCanceledException・リクエスト側は未キャンセル）した場合、
        // キャンセル扱いにせず failed 記録＋failed Detail が返る。
        var stub = new SubsidiaryCheckFakeAiClient { AnalyzeException = new OperationCanceledException() };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.jpg", "image/jpeg", JpegBytes(0x01));
        AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));

        var create = await client.PostAsync("/api/subsidiary-check", form);
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();

        Assert.Equal(SubsidiaryCheckStatus.Failed, created!.Summary.Status);
        Assert.Contains("UNDX-AI-001", created.Summary.ErrorMessage ?? string.Empty);
        Assert.Contains("タイムアウト", created.Summary.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public async Task Rules_ReturnsRuleBookGeneratedFromCatalog()
    {
        // ルールブックは静的カタログ由来のため AI 設定に依存しない（既定ファクトリ＝AI 未設定でも応答する）
        using var client = CreateClient(_fixture.Factory);

        var book = await client.GetFromJsonAsync<SubsidiaryRuleBook>("/api/subsidiary-check/rules");

        Assert.NotNull(book);
        Assert.Equal(5, book!.Sections.Count);
        Assert.False(string.IsNullOrWhiteSpace(book.Source));
        Assert.Contains(book.Sections, s => s.Id == "icon-priority" && s.Items.Count == 7);
    }

    /// <summary>IAiChatClient を副資材チェック用スタブへ差し替えたファクトリを生成する。</summary>
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithSubsidiaryAi(
        SubsidiaryCheckFakeAiClient stub)
        => _fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IAiChatClient>(stub)));

    private static HttpClient CreateClient(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        string token = "member-token")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static void AddImage(
        MultipartFormDataContent form, string fieldName, string fileName, string contentType, byte[] bytes)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, fieldName, fileName);
    }

    /// <summary>JPEG マジックバイト（FF D8 FF）を持つ擬似画像（marker で内容を変えられる）。</summary>
    private static byte[] JpegBytes(byte marker) => new byte[] { 0xFF, 0xD8, 0xFF, marker };

    /// <summary>PNG マジックバイト（89 50 4E 47）を持つ擬似画像（marker で内容を変えられる）。</summary>
    private static byte[] PngBytes(byte marker) => new byte[] { 0x89, 0x50, 0x4E, 0x47, marker };

    /// <summary>指定サイズまでゼロ埋めした擬似画像（先頭は magic のバイト列）。</summary>
    private static byte[] LargeImage(byte[] magic, int totalLength)
    {
        var data = new byte[totalLength];
        magic.CopyTo(data, 0);
        return data;
    }

    /// <summary>
    /// processing 状態のチェック（＋画像2枚）を DB へ直接投入する（processing 孤児のシミュレーション）。
    /// </summary>
    /// <param name="age">created_at を現在からどれだけ過去にするか。</param>
    private async Task<Guid> InsertProcessingCheckAsync(TimeSpan age)
    {
        var checkId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            INSERT INTO subsidiary_check (check_id, product_label, status, created_by, created_at)
            VALUES (@checkId, 'processing 孤児テスト', 'processing', 'tester@example.com', now() - @age);

            INSERT INTO subsidiary_check_image
                (image_id, check_id, kind, file_name, content_type, size_bytes, data, sort_order)
            VALUES
                (@instructionId, @checkId, 'instruction', 'i.jpg', 'image/jpeg', 4, @jpeg, 0),
                (@tagId, @checkId, 'tag', 't.png', 'image/png', 5, @png, 0);
            """,
            new
            {
                checkId,
                age,
                instructionId = Guid.NewGuid(),
                tagId = Guid.NewGuid(),
                jpeg = JpegBytes(0x01),
                png = PngBytes(0x01),
            });
        return checkId;
    }

    /// <summary>商品マスタ＋付属情報のテストデータを投入する（自然キーはテストごとに一意）。</summary>
    private async Task<Guid> InsertProductWithAttachmentAsync()
    {
        var productId = Guid.NewGuid();
        var uniqueSign = $"TAG-{Guid.NewGuid():N}"[..20];

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            INSERT INTO m_product
                (product_id, business_category_cd, business_category_sign, division_cd, division_name,
                 product_name, brand, product_sign, manager, product_type_crd)
            VALUES
                (@productId, '01', 'sm', 11, '婦人',
                 '検証用ルームシューズ', 'テストブランド', @productSign, NULL, '100');

            INSERT INTO m_product_attachment
                (product_id, composition, origin_country, care_labels, color_fastness_note,
                 display_order, quality_notes)
            VALUES
                (@productId, '綿 100%', 'MADE IN CHINA', '手洗い可（110）', '色落ち表示あり',
                 '品番,サイズ,混率,洗濯表示,色落ち表示等,原産国表示,製造者', '天然素材のささくれ注意');
            """, new { productId, productSign = uniqueSign });

        return productId;
    }
}

/// <summary>
/// 副資材チェック統合テスト用の擬似 AI クライアント。
/// ResponseText を差し替えることで正常応答／解析不能応答を切り替えられる（rerun の回復検証用）。
/// </summary>
public sealed class SubsidiaryCheckFakeAiClient : IAiChatClient
{
    /// <summary>API キー構成済み扱いにするか（false で UNDX-AI-008 経路を検証する）。</summary>
    public bool Configured { get; set; } = true;

    /// <summary>AnalyzeImagesAsync が返す固定応答テキスト。</summary>
    public string ResponseText { get; set; } = "{ \"findings\": [] }";

    /// <summary>
    /// AnalyzeImagesAsync が throw する例外（AI 呼出タイムアウト等の異常系検証用）。null なら正常応答。
    /// </summary>
    public Exception? AnalyzeException { get; set; }

    public bool IsConfigured => Configured;

    public string ChatModel => "fake-subsidiary-model";

    public async IAsyncEnumerable<AiStreamEvent> StreamChatAsync(
        AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return AiStreamEvent.Delta("テスト応答");
        yield return AiStreamEvent.Done(1, 1);
    }

    public Task<string> DescribeImageAsync(
        byte[] imageData, string mediaType, string? hint, CancellationToken cancellationToken)
        => Task.FromResult("テスト画像の説明");

    public Task<string> AnalyzeImagesAsync(
        IReadOnlyList<AiImageInput> images, string systemPrompt, string userPrompt,
        int maxTokens, CancellationToken cancellationToken)
    {
        if (!Configured)
        {
            throw new AppException(ErrorCodes.AiNotConfigured, 503);
        }

        if (AnalyzeException is not null)
        {
            throw AnalyzeException;
        }

        return Task.FromResult(ResponseText);
    }
}
