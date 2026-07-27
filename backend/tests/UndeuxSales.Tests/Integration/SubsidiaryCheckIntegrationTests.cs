using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// 一連のフロー・検証エラー・404・AI 未設定時 503・受付上限 429・再実行（手動回復パス）を検証する。
/// 件数はテスト実行順に依存しないよう差分（前後比較）で検証する。
/// <para>
/// AI 実行は非同期（バックグラウンド）のため、POST は status=processing を即座に返す。
/// 完了の待機は <see cref="WaitForSettledAsync"/>（公開 API の GET 詳細をポーリング）で行い、
/// フロントの自動更新と同じ観測手段だけを使う（実装の内部構造に依存しない）。
/// </para>
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

    /// <summary>
    /// 「孤児（stale processing）」と判定される滞留時間。
    /// 判定境界（<see cref="SubsidiaryCheckService.ProcessingStaleAfter"/>）から導出し、
    /// 境界値の変更でテストが意図せず壊れないようにする（境界＋1分の余裕）。
    /// </summary>
    private static readonly TimeSpan OrphanAge =
        SubsidiaryCheckService.ProcessingStaleAfter + TimeSpan.FromMinutes(1);

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
        var accepted = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();

        // POST は AI 完了を待たず、受付時点（processing）の詳細を即座に返す
        // （共用リバースプロキシのタイムアウト約60秒を超えないための構造）。
        Assert.NotNull(accepted);
        Assert.Equal(SubsidiaryCheckStatus.Processing, accepted!.Summary.Status);
        Assert.Null(accepted.Summary.Judgment);
        Assert.Null(accepted.Summary.CheckedAt);
        // 画像・登録情報は応答時点で既に永続化されている（AI 実行だけがバックグラウンド）。
        Assert.Equal(3, accepted.Images.Count);
        Assert.Equal("TAG-3943 スリッパ", accepted.Summary.ProductLabel);

        // バックグラウンド実行の完了を待つ（フロントのポーリングと同じ観測手段）。
        var created = await WaitForSettledAsync(client, accepted.Summary.CheckId);

        Assert.Equal(SubsidiaryCheckStatus.Completed, created.Summary.Status);
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
        var accepted = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();
        Assert.NotNull(accepted);
        var created = await WaitForSettledAsync(client, accepted!.Summary.CheckId);

        Assert.NotNull(created.Product);
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
        var accepted = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();
        Assert.NotNull(accepted);
        var created = await WaitForSettledAsync(client, accepted!.Summary.CheckId);

        Assert.Equal(SubsidiaryCheckStatus.Failed, created.Summary.Status);
        Assert.Null(created.Summary.Judgment);
        Assert.Contains("UNDX-AI-009", created.Summary.ErrorMessage ?? string.Empty);
        Assert.Empty(created.Findings);
        // 失敗しても登録済みの記録・画像は残る（rerun での回復に必要）
        Assert.Equal(2, created.Images.Count);

        // 応答を正常化して再実行（手動回復パス）→ completed / pass へ回復する
        stub.ResponseText = AllPassResponse;
        var rerun = await client.PostAsync($"/api/subsidiary-check/{created.Summary.CheckId}/rerun", content: null);
        rerun.EnsureSuccessStatusCode();
        var rerunAccepted = await rerun.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();
        // rerun も即座に processing を返し、AI はバックグラウンドで実行される。
        Assert.Equal(SubsidiaryCheckStatus.Processing, rerunAccepted!.Summary.Status);
        var rerunDetail = await WaitForSettledAsync(client, created.Summary.CheckId);

        Assert.Equal(SubsidiaryCheckStatus.Completed, rerunDetail.Summary.Status);
        Assert.Equal(SubsidiaryCheckSeverity.Pass, rerunDetail.Summary.Judgment);
        Assert.Null(rerunDetail.Summary.ErrorMessage);
        Assert.Equal(3, rerunDetail.Findings.Count);

        // 再実行では started_at が進む（クレームで now() に更新される）。
        Assert.NotNull(rerunDetail.Summary.StartedAt);
        Assert.True(rerunDetail.Summary.StartedAt >= created.Summary.StartedAt);

        // completed への再実行は記録保護（原則2）のため 400
        var protectedRerun = await client.PostAsync(
            $"/api/subsidiary-check/{created.Summary.CheckId}/rerun", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, protectedRerun.StatusCode);
        var protectedBody = await protectedRerun.Content.ReadAsStringAsync();
        Assert.Contains("UNDX-REQ-001", protectedBody);
        Assert.Contains("完了", protectedBody);
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

        // 実行開始から ProcessingStaleAfter 超経過した processing 孤児（プロセス消失等の想定。
        // 非同期化後はバックグラウンドタスクごと消えたケース）は再実行で回復できる。
        var staleId = await InsertProcessingCheckAsync(OrphanAge);
        var rerun = await client.PostAsync($"/api/subsidiary-check/{staleId}/rerun", content: null);
        rerun.EnsureSuccessStatusCode();
        var detail = await WaitForSettledAsync(client, staleId);
        Assert.Equal(SubsidiaryCheckStatus.Completed, detail.Summary.Status);
        Assert.Equal(SubsidiaryCheckSeverity.Pass, detail.Summary.Judgment);

        // 新しい（実行中の可能性がある）processing は従来どおり 400 で拒否される。
        var freshId = await InsertProcessingCheckAsync(TimeSpan.Zero);
        var rejected = await client.PostAsync($"/api/subsidiary-check/{freshId}/rerun", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("UNDX-REQ-001", await rejected.Content.ReadAsStringAsync());
        Assert.Contains("実行中", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rerun_OrphanJudgment_UsesStartedAtNotCreatedAt()
    {
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        // 登録は孤児判定を超える昔だが、直前に再実行が始まっている（started_at が新しい）チェックは
        // 「実行中」であり孤児ではない。基準が created_at のままだと誤って再実行を許してしまう。
        var runningId = await InsertProcessingCheckAsync(OrphanAge, startedAge: TimeSpan.Zero);
        var rejected = await client.PostAsync($"/api/subsidiary-check/{runningId}/rerun", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("UNDX-REQ-001", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(0, stub.AnalyzeCallCount);

        // 逆に、登録は直前でも実行開始が孤児判定を超えていれば回復できる。
        var orphanId = await InsertProcessingCheckAsync(TimeSpan.Zero, startedAge: OrphanAge);
        var accepted = await client.PostAsync($"/api/subsidiary-check/{orphanId}/rerun", content: null);
        accepted.EnsureSuccessStatusCode();
        var detail = await WaitForSettledAsync(client, orphanId);
        Assert.Equal(SubsidiaryCheckStatus.Completed, detail.Summary.Status);
        Assert.Equal(1, stub.AnalyzeCallCount);
    }

    [Fact]
    public async Task Rerun_Claim_IsAtomic_AndAdvancesStartedAt()
    {
        // クレーム UPDATE は compare-and-set。1回目で実行権を取ると started_at が now() へ進み、
        // 同じ孤児条件では2回目がクレームできない（重複起動が構造的に不可能）。
        var staleId = await InsertProcessingCheckAsync(OrphanAge);
        using var scope = _fixture.Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<SubsidiaryCheckRepository>();
        var staleBefore = DateTime.UtcNow - SubsidiaryCheckService.ProcessingStaleAfter;

        Assert.Equal(1, await repository.ClaimForRerunAsync(staleId, staleBefore));
        Assert.Equal(0, await repository.ClaimForRerunAsync(staleId, staleBefore));

        var summary = (await repository.GetDetailAsync(staleId))!.Summary;
        Assert.Equal(SubsidiaryCheckStatus.Processing, summary.Status);
        Assert.NotNull(summary.StartedAt);
        Assert.True(summary.StartedAt > staleBefore, "クレームで started_at が現在時刻へ進むこと");
        Assert.True(summary.StartedAt > summary.CreatedAt, "started_at は created_at より後になること");
    }

    [Fact]
    public async Task Rerun_Concurrent_OnlyOneRequestClaimsExecution()
    {
        // 同一 checkId への並行 rerun は、クレームに成功した1本だけが AI を呼ぶ。
        // 残りは 400（UNDX-REQ-001）で弾かれ、AI 呼出が多重化しない。
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = "この応答は JSON ではありません。" };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.jpg", "image/jpeg", JpegBytes(0x01));
        AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));
        var create = await client.PostAsync("/api/subsidiary-check", form);
        create.EnsureSuccessStatusCode();
        var accepted = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();
        Assert.NotNull(accepted);
        var created = await WaitForSettledAsync(client, accepted!.Summary.CheckId);
        Assert.Equal(SubsidiaryCheckStatus.Failed, created.Summary.Status);

        stub.ResponseText = AllPassResponse;
        var callsBefore = stub.AnalyzeCallCount;
        var url = $"/api/subsidiary-check/{created.Summary.CheckId}/rerun";
        var responses = await Task.WhenAll(
            client.PostAsync(url, content: null),
            client.PostAsync(url, content: null));

        Assert.Equal(1, responses.Count(r => r.IsSuccessStatusCode));
        var rejected = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.BadRequest);
        Assert.Contains("UNDX-REQ-001", await rejected.Content.ReadAsStringAsync());

        var detail = await WaitForSettledAsync(client, created.Summary.CheckId);
        Assert.Equal(SubsidiaryCheckStatus.Completed, detail.Summary.Status);
        // AI 呼出は1回だけ（read-then-act では2回呼ばれ得た）。
        Assert.Equal(callsBefore + 1, stub.AnalyzeCallCount);

        foreach (var response in responses)
        {
            response.Dispose();
        }
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
        var accepted = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();
        Assert.NotNull(accepted);
        var created = await WaitForSettledAsync(client, accepted!.Summary.CheckId);
        Assert.Equal(SubsidiaryCheckStatus.Completed, created.Summary.Status);

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
        // AI 呼出が自前のタイムアウトで打ち切られた場合（リクエスト側は未キャンセル）、
        // キャンセル扱いにせず failed 記録＋failed Detail が返る。
        // 実時間120秒を待たないよう、タイムアウトを内部シームで短縮して発火させる。
        var stub = new SubsidiaryCheckFakeAiClient
        {
            ResponseText = AllPassResponse,
            AnalyzeDelay = TimeSpan.FromSeconds(30),
        };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.jpg", "image/jpeg", JpegBytes(0x01));
        AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));

        SubsidiaryCheckService.AiCallTimeoutOverride = TimeSpan.FromMilliseconds(50);
        try
        {
            var create = await client.PostAsync("/api/subsidiary-check", form);
            create.EnsureSuccessStatusCode();
            var accepted = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();
            Assert.NotNull(accepted);
            var created = await WaitForSettledAsync(client, accepted!.Summary.CheckId);

            Assert.Equal(SubsidiaryCheckStatus.Failed, created.Summary.Status);
            Assert.Contains("UNDX-AI-001", created.Summary.ErrorMessage ?? string.Empty);
            Assert.Contains("AI 呼出がタイムアウト", created.Summary.ErrorMessage ?? string.Empty);
        }
        finally
        {
            SubsidiaryCheckService.AiCallTimeoutOverride = null;
        }
    }

    [Fact]
    public async Task Create_AiQueueWaitTimeout_RecordsFailedDetail_WithoutCallingAi()
    {
        // 同時実行スロット（SemaphoreSlim(1)）が塞がっている間の順番待ちは有界（既定420秒）。
        // 待機超過時は AI を呼ばずに failed 記録＋failed Detail で応答する（メモリ保護）。
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.jpg", "image/jpeg", JpegBytes(0x01));
        AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));

        // スロットの取得はリトライ付きで行う（直前のテストの解放待ち。内部の解放順序に依存しない）。
        await OccupyAiSlotAsync();
        SubsidiaryCheckService.AiCallQueueTimeoutOverride = TimeSpan.FromMilliseconds(50);
        try
        {
            var create = await client.PostAsync("/api/subsidiary-check", form);
            create.EnsureSuccessStatusCode();
            var accepted = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();
            Assert.NotNull(accepted);
            var created = await WaitForSettledAsync(client, accepted!.Summary.CheckId);

            Assert.Equal(SubsidiaryCheckStatus.Failed, created.Summary.Status);
            Assert.Contains("UNDX-AI-001", created.Summary.ErrorMessage ?? string.Empty);
            Assert.Contains("順番待ち", created.Summary.ErrorMessage ?? string.Empty);
            // 待機超過時は AI を呼ばない（画像バッファを抱えた滞留を増やさない）。
            Assert.Equal(0, stub.AnalyzeCallCount);
            // 記録は残るため rerun で回復できる（failed は再実行可能）。
            Assert.Equal(2, created.Images.Count);
        }
        finally
        {
            SubsidiaryCheckService.AiCallQueueTimeoutOverride = null;
            SubsidiaryCheckService.ReleaseAiSlot();
        }
    }

    [Fact]
    public async Task Create_ProductLabelTooLong_Returns400WithErrorCode()
    {
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.jpg", "image/jpeg", JpegBytes(0x01));
        AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));
        form.Add(
            new StringContent(new string('あ', SubsidiaryCheckService.MaxProductLabelLength + 1)),
            "productLabel");

        var response = await client.PostAsync("/api/subsidiary-check", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("UNDX-REQ-001", await response.Content.ReadAsStringAsync());
        // 上限超過は永続化前に拒否されるため AI も呼ばれない。
        Assert.Equal(0, stub.AnalyzeCallCount);
    }

    [Fact]
    public async Task Create_UppercaseContentType_IsAccepted()
    {
        // media type は case-insensitive。"IMAGE/PNG" 等でも正当な入力として受理する。
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        using var form = new MultipartFormDataContent();
        AddImage(form, "instructionImages", "instruction.JPG", "IMAGE/JPEG", JpegBytes(0x01));
        AddImage(form, "tagImages", "tag.PNG", "IMAGE/PNG", PngBytes(0x01));

        var create = await client.PostAsync("/api/subsidiary-check", form);
        create.EnsureSuccessStatusCode();
        var accepted = await create.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();
        Assert.NotNull(accepted);
        var created = await WaitForSettledAsync(client, accepted!.Summary.CheckId);

        Assert.Equal(SubsidiaryCheckStatus.Completed, created.Summary.Status);
    }

    [Fact]
    public async Task Create_WhenAcceptanceLimitReached_Returns429_AndPersistsNothing()
    {
        // 受付上限（実行中＋待機中の総数）に達している間の新規チェックは、永続化前に 429 で拒否する
        // （画像バッファを抱えたまま滞留する件数を有界にし、無駄なレコード・画像も作らない）。
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        var before = await client.GetFromJsonAsync<SubsidiaryCheckPage>("/api/subsidiary-check");
        var reserved = await OccupyAllAcceptanceSlotsAsync();
        try
        {
            using var form = new MultipartFormDataContent();
            AddImage(form, "instructionImages", "instruction.jpg", "image/jpeg", JpegBytes(0x01));
            AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));

            var response = await client.PostAsync("/api/subsidiary-check", form);

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Contains("UNDX-REQ-009", await response.Content.ReadAsStringAsync());
            Assert.Equal(0, stub.AnalyzeCallCount);

            // 永続化前に拒否されるため、レコードは1件も増えない。
            var after = await client.GetFromJsonAsync<SubsidiaryCheckPage>("/api/subsidiary-check");
            Assert.Equal(before!.TotalCount, after!.TotalCount);
        }
        finally
        {
            ReleaseAcceptanceSlots(reserved);
        }
    }

    [Fact]
    public async Task Create_WhenAcceptanceLimitReached_RejectsBeforeReadingImages()
    {
        // 受付枠の判定は「画像バッファ（1件あたり最大20MB）の確保より前」に行う必要がある
        // （バッファ確保後だと、429 で拒否される要求までピークメモリへ寄与してしまう）。
        // 画像の読取・検証（ReadImagesAsync）まで進んでいれば形式エラーの 400（UNDX-REQ-005）に
        // なる要求が 429 で返ることで、アプリ層の画像バッファ（new byte[file.Length]）を
        // 1バイトも確保せずに拒否していることを観測できる。
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        var reserved = await OccupyAllAcceptanceSlotsAsync();
        try
        {
            using var form = new MultipartFormDataContent();
            AddImage(form, "instructionImages", "instruction.gif", "image/gif", new byte[] { 1 });
            AddImage(form, "tagImages", "tag.png", "image/png", PngBytes(0x01));

            var response = await client.PostAsync("/api/subsidiary-check", form);

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Contains("UNDX-REQ-009", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            ReleaseAcceptanceSlots(reserved);
        }

        // 上限が解消されれば、同じ要求は本来の形式エラー（＝画像を読んだ結果）で拒否される。
        using var retryForm = new MultipartFormDataContent();
        AddImage(retryForm, "instructionImages", "instruction.gif", "image/gif", new byte[] { 1 });
        AddImage(retryForm, "tagImages", "tag.png", "image/png", PngBytes(0x01));
        var retry = await client.PostAsync("/api/subsidiary-check", retryForm);
        Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
        Assert.Contains("UNDX-REQ-005", await retry.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_ValidationFailureAiUnconfiguredAndSuccess_ReleaseAcceptanceSlots()
    {
        // 受付枠の予約はコントローラ入口（バッファ確保前）へ移ったため、成功・失敗を問わず
        // 全経路で対称に解放される必要がある。漏れると 429 が恒久化して機能停止し、
        // 過剰解放するとピークメモリの防御（受付上限）が無効化される。
        // 予約後の代表的な3経路（画像検証エラー・AI 未設定 503・成功）を上限より多い回数だけ通し、
        // 「上限ちょうどを占有できる（＝漏れなし）」「過剰解放のログが出ていない」で対称性を確認する。
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        var logs = new CapturingLoggerProvider();
        using var factory = WithSubsidiaryAi(stub, logs);
        using var client = CreateClient(factory);

        for (var i = 0; i <= SubsidiaryCheckService.MaxConcurrentAiChecks; i++)
        {
            // 予約後・永続化前の失敗（画像の形式検証エラー）。
            using (var invalidForm = new MultipartFormDataContent())
            {
                AddImage(invalidForm, "instructionImages", "bad.jpg", "image/jpeg", PngBytes(0x01));
                AddImage(invalidForm, "tagImages", "tag.png", "image/png", PngBytes(0x01));
                using var invalid = await client.PostAsync("/api/subsidiary-check", invalidForm);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            }

            // 予約後・永続化前の失敗（AI 未設定 503）。検証を通過してからの別経路。
            stub.Configured = false;
            using (var unconfiguredForm = new MultipartFormDataContent())
            {
                AddImage(unconfiguredForm, "instructionImages", "i.jpg", "image/jpeg", JpegBytes(0x01));
                AddImage(unconfiguredForm, "tagImages", "tag.png", "image/png", PngBytes(0x01));
                using var unconfigured = await client.PostAsync("/api/subsidiary-check", unconfiguredForm);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, unconfigured.StatusCode);
            }

            stub.Configured = true;

            // 成功経路（解放責務がバックグラウンドタスクへ移る）。
            using (var validForm = new MultipartFormDataContent())
            {
                AddImage(validForm, "instructionImages", "instruction.jpg", "image/jpeg", JpegBytes(0x01));
                AddImage(validForm, "tagImages", "tag.png", "image/png", PngBytes(0x01));
                using var valid = await client.PostAsync("/api/subsidiary-check", validForm);
                valid.EnsureSuccessStatusCode();
                var accepted = await valid.Content.ReadFromJsonAsync<SubsidiaryCheckDetail>();
                Assert.NotNull(accepted);
                await WaitForSettledAsync(client, accepted!.Summary.CheckId);
            }
        }

        // 漏れていれば上限数ちょうどを占有できない。
        var reserved = await OccupyAllAcceptanceSlotsAsync();
        ReleaseAcceptanceSlots(reserved);

        // 過剰解放は「上限数を占有できる」だけでは検出できない（セマフォ側で頭打ちになるため）。
        // セマフォ（SemaphoreSlim(Max, Max)）は遅くとも全枠が解放される時点までに必ず検知して
        // ログへ出すので、枠数の確認とログの不在の両方で対称性を担保する。
        Assert.DoesNotContain(logs.Snapshot(), m => m.Contains("受付枠が過剰に解放されました"));
    }

    [Fact]
    public async Task AcceptanceSlotLease_HandOffAndRepeatedDispose_ReleaseExactlyOnce()
    {
        // 受付枠の解放は「1予約につき1回」。移譲後の元スコープの Dispose（リクエスト側の using）と、
        // 引継ぎ先スコープの二重 Dispose のいずれも余分な解放を起こさないこと。
        var reserved = await OccupyAllAcceptanceSlotsAsync();
        try
        {
            // 満杯なので追加予約はできない。
            Assert.Null(SubsidiaryCheckService.TryReserveAiCheckSlotForTest());

            var lease = reserved[0];
            var handed = lease.HandOffToBackground();
            lease.Dispose();
            lease.Dispose();
            Assert.Null(SubsidiaryCheckService.TryReserveAiCheckSlotForTest());

            // 引継ぎ先の Dispose で初めて1枠空く。二重 Dispose では2枠目は空かない。
            handed.Dispose();
            handed.Dispose();
            var freed = SubsidiaryCheckService.TryReserveAiCheckSlotForTest();
            Assert.NotNull(freed);
            Assert.Null(SubsidiaryCheckService.TryReserveAiCheckSlotForTest());

            // 後始末は再取得した予約に引き継ぐ（解放数と予約数を一致させる）。
            reserved[0] = freed!;
        }
        finally
        {
            ReleaseAcceptanceSlots(reserved);
        }
    }

    [Fact]
    public async Task Rerun_LogsRequestingUser()
    {
        // rerun は外部有料 API を起動する書込操作のため、実行者を記録して無記名の反復実行を防ぐ
        // （新規作成は created_by として永続化される。rerun の恒久的な永続化は後続課題）。
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        var logs = new CapturingLoggerProvider();
        using var factory = WithSubsidiaryAi(stub, logs);
        using var client = CreateClient(factory);

        var staleId = await InsertProcessingCheckAsync(OrphanAge);
        using var rerun = await client.PostAsync($"/api/subsidiary-check/{staleId}/rerun", content: null);
        rerun.EnsureSuccessStatusCode();
        await WaitForSettledAsync(client, staleId);

        var entry = Assert.Single(logs.Snapshot(), m => m.Contains("副資材チェックを再実行します"));
        Assert.Contains(staleId.ToString(), entry);
        Assert.Contains("tester@example.com", entry);
    }

    [Fact]
    public async Task Rerun_WhenAcceptanceLimitReached_Returns429_WithoutClaiming()
    {
        // rerun はクレーム前に判定する（クレームしてから即 failed に落とすと記録を汚すため）。
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        var staleId = await InsertProcessingCheckAsync(OrphanAge);
        var reserved = await OccupyAllAcceptanceSlotsAsync();
        try
        {
            var response = await client.PostAsync($"/api/subsidiary-check/{staleId}/rerun", content: null);

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Contains("UNDX-REQ-009", await response.Content.ReadAsStringAsync());
            Assert.Equal(0, stub.AnalyzeCallCount);

            // クレームしていないため状態は processing のまま（failed 記録で履歴を汚さない）。
            var detail = await client.GetFromJsonAsync<SubsidiaryCheckDetail>(
                $"/api/subsidiary-check/{staleId}");
            Assert.Equal(SubsidiaryCheckStatus.Processing, detail!.Summary.Status);
            Assert.Null(detail.Summary.ErrorMessage);
        }
        finally
        {
            ReleaseAcceptanceSlots(reserved);
        }

        // 上限が解消されれば同じ孤児を回復できる（拒否が状態を壊していないことの確認）。
        var retry = await client.PostAsync($"/api/subsidiary-check/{staleId}/rerun", content: null);
        retry.EnsureSuccessStatusCode();
        var settled = await WaitForSettledAsync(client, staleId);
        Assert.Equal(SubsidiaryCheckStatus.Completed, settled.Summary.Status);
    }

    [Fact]
    public async Task Rerun_PreparationFailureInBackground_IsRecordedAsFailed()
    {
        // クレーム成立後・AI 呼出前の準備（商品情報・画像バイナリの DB 読取）で失敗しても、
        // processing のまま取り残さず failed 記録に落とす（無保護区間がないこと）。
        // 非同期化でこの区間はバックグラウンドへ移ったため、保護もバックグラウンド側で効くこと。
        var stub = new SubsidiaryCheckFakeAiClient { ResponseText = AllPassResponse };
        using var factory = WithSubsidiaryAi(stub);
        using var client = CreateClient(factory);

        var staleId = await InsertProcessingCheckAsync(OrphanAge);
        SubsidiaryCheckService.RerunPrepareFailureOverride =
            id => id == staleId ? new InvalidOperationException("準備失敗の注入") : null;
        try
        {
            var rerun = await client.PostAsync($"/api/subsidiary-check/{staleId}/rerun", content: null);
            rerun.EnsureSuccessStatusCode();

            var detail = await WaitForSettledAsync(client, staleId);
            Assert.Equal(SubsidiaryCheckStatus.Failed, detail.Summary.Status);
            Assert.Contains("UNDX-SYS-001", detail.Summary.ErrorMessage ?? string.Empty);
            // 準備段階で落ちているため AI は呼ばれない。
            Assert.Equal(0, stub.AnalyzeCallCount);
        }
        finally
        {
            SubsidiaryCheckService.RerunPrepareFailureOverride = null;
        }

        // failed になったことで、注入を外せば通常の rerun で回復できる。
        var recovered = await client.PostAsync($"/api/subsidiary-check/{staleId}/rerun", content: null);
        recovered.EnsureSuccessStatusCode();
        var settled = await WaitForSettledAsync(client, staleId);
        Assert.Equal(SubsidiaryCheckStatus.Completed, settled.Summary.Status);
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

    /// <summary>
    /// status が processing でなくなる（completed / failed）まで詳細をポーリングして返す。
    /// フロントの自動更新と同じく公開 API（GET 詳細）だけを観測手段に使うため、
    /// バックグラウンド実行の内部構造（タスク・スコープ・カウンタ）には依存しない。
    /// </summary>
    private static async Task<SubsidiaryCheckDetail> WaitForSettledAsync(
        HttpClient client, Guid checkId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var detail = await client.GetFromJsonAsync<SubsidiaryCheckDetail>(
                $"/api/subsidiary-check/{checkId}");
            Assert.NotNull(detail);
            if (detail!.Summary.Status != SubsidiaryCheckStatus.Processing)
            {
                return detail;
            }

            Assert.True(DateTime.UtcNow < deadline,
                $"チェック {checkId} が制限時間内に完了しませんでした（processing のまま）。");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// AI 実行スロット（同時実行1のセマフォ）を占有して「順番待ちが発生する」状態を作る。
    /// 直前のテストのバックグラウンド実行が解放し切るまで少し待つため、取得できるまでリトライする
    /// （「RunAiAsync がセマフォを解放してから status を更新する」という内部順序に依存しない）。
    /// 解放は <see cref="SubsidiaryCheckService.ReleaseAiSlot"/> で行う。
    /// </summary>
    private static async Task OccupyAiSlotAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await SubsidiaryCheckService.TryOccupyAiSlotAsync())
        {
            Assert.True(DateTime.UtcNow < deadline,
                "AI 実行スロットが解放されず、順番待ち状態を作れませんでした。");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// 受付枠（実行中＋待機中の上限）をすべて占有して「満杯」状態を作る。
    /// 直前のテストのバックグラウンドタスクが解放し切るまで少し待つため、
    /// 上限数ちょうどを確保できるまでリトライする（部分占有のまま先へ進んで取りこぼさない）。
    /// 「ちょうど上限数」であることは、受付枠に解放漏れがないこと（少なく取れる）の検証も兼ねる。
    /// 過剰解放はセマフォが頭打ちにするため件数では検出できないので、
    /// アプリ経路の対称性はログ（「受付枠が過剰に解放されました」の不在）と併せて検証する。
    /// </summary>
    /// <returns>占有した予約スコープ（<see cref="ReleaseAcceptanceSlots"/> へ渡す）。</returns>
    private static async Task<List<AiCheckSlotLease>> OccupyAllAcceptanceSlotsAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var reserved = new List<AiCheckSlotLease>();
            while (SubsidiaryCheckService.TryReserveAiCheckSlotForTest() is { } lease)
            {
                reserved.Add(lease);
            }

            if (reserved.Count == SubsidiaryCheckService.MaxConcurrentAiChecks)
            {
                return reserved;
            }

            ReleaseAcceptanceSlots(reserved);
            Assert.True(DateTime.UtcNow < deadline, "受付枠が解放されず、満杯状態を作れませんでした。");
            await Task.Delay(25);
        }
    }

    /// <summary><see cref="OccupyAllAcceptanceSlotsAsync"/> で占有した受付枠を解放する。</summary>
    private static void ReleaseAcceptanceSlots(IEnumerable<AiCheckSlotLease> leases)
    {
        foreach (var lease in leases)
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// IAiChatClient を副資材チェック用スタブへ差し替えたファクトリを生成する。
    /// <paramref name="logs"/> を渡すと、アプリのログ出力を検証できる（実行者の記録の検証等）。
    /// </summary>
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithSubsidiaryAi(
        SubsidiaryCheckFakeAiClient stub, CapturingLoggerProvider? logs = null)
        => _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services => services.AddSingleton<IAiChatClient>(stub));
            if (logs is not null)
            {
                builder.ConfigureLogging(logging =>
                {
                    logging.AddProvider(logs);
                    // appsettings のログレベル設定に左右されないよう、このプロバイダ限定で
                    // Information 以上を必ず受け取る（プロバイダ指定のルールが最も具体的で優先される）。
                    logging.AddFilter<CapturingLoggerProvider>(category: null, LogLevel.Information);
                });
            }
        });

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
    /// <param name="startedAge">
    /// started_at を現在からどれだけ過去にするか（null なら <paramref name="age"/> と同じ）。
    /// 孤児判定の基準が created_at ではなく started_at であることを検証するために分離できる。
    /// </param>
    private async Task<Guid> InsertProcessingCheckAsync(TimeSpan age, TimeSpan? startedAge = null)
    {
        var checkId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            INSERT INTO subsidiary_check
                (check_id, product_label, status, created_by, created_at, started_at)
            VALUES (@checkId, 'processing 孤児テスト', 'processing', 'tester@example.com',
                    now() - @age, now() - @startedAge);

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
                startedAge = startedAge ?? age,
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
/// ログ出力をメモリに溜めて検証するテスト用 ILoggerProvider。
/// 「実行者を記録している」ようなログ由来の要件を、公開 API 経由の実行で検証するために使う。
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = new();

    /// <summary>これまでに記録されたメッセージのスナップショット（レンダリング済み本文）。</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_messages)
        {
            return _messages.ToArray();
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private void Add(string message)
    {
        lock (_messages)
        {
            _messages.Add(message);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly CapturingLoggerProvider _owner;

        public CapturingLogger(CapturingLoggerProvider owner) => _owner = owner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _owner.Add(formatter(state, exception));
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
    /// AnalyzeImagesAsync が throw する例外（AI 呼出失敗等の異常系検証用）。null なら正常応答。
    /// </summary>
    public Exception? AnalyzeException { get; set; }

    /// <summary>
    /// 応答前に待機する時間（null なら即時応答）。待機は渡された CancellationToken を尊重するため、
    /// サービス側の AI 呼出タイムアウトを実際に発火させられる（AiCallTimeoutOverride と併用する）。
    /// </summary>
    public TimeSpan? AnalyzeDelay { get; set; }

    /// <summary>AnalyzeImagesAsync が呼ばれた回数（「AI を呼ばずに失敗した」ことの検証用）。</summary>
    public int AnalyzeCallCount => _analyzeCallCount;

    private int _analyzeCallCount;

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

    public async Task<string> AnalyzeImagesAsync(
        IReadOnlyList<AiImageInput> images, string systemPrompt, string userPrompt,
        int maxTokens, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _analyzeCallCount);

        if (!Configured)
        {
            throw new AppException(ErrorCodes.AiNotConfigured, 503);
        }

        if (AnalyzeException is not null)
        {
            throw AnalyzeException;
        }

        if (AnalyzeDelay is { } delay)
        {
            // トークンを尊重して待つ（サービス側のタイムアウト発火で OperationCanceledException になる）。
            await Task.Delay(delay, cancellationToken);
        }

        return ResponseText;
    }
}
