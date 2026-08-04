using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using UndeuxSales.Core;
using UndeuxSales.Core.SubsidiaryCheck.ManualCheck;
using UndeuxSales.Infrastructure.SubsidiaryCheck;

namespace UndeuxSales.Api.Controllers;

/// <summary>
/// 副資材の手入力チェック（手入力表示項目 × タグ画像の AI 抽出値の突合）API。
/// 履歴一覧・新規チェック（multipart）・詳細・画像取得・再実行・洗濯アイコンマスタを提供する。
/// 既存 <see cref="SubsidiaryCheckController"/> と同じ非同期実行（processing→completed/failed）方式。
/// </summary>
[ApiController]
[Authorize]
[Route("api/subsidiary-check/manual")]
public sealed class SubsidiaryManualCheckController : ControllerBase
{
    // multipart 全体の transport 層上限（防御層）。既存副資材チェックと同じ 25MB
    // （アプリ層の合計サイズ上限 14MB ＋ multipart オーバーヘッド）。
    private const long HardSizeLimitBytes = 25 * 1024 * 1024;

    // input（手入力スナップショット JSON）の最大文字数。巨大テキストの受理・保存・AI プロンプト混入を防ぐ
    // 増幅ガード（項目数・値数・値長はサービス側でさらに検証する）。
    private const int MaxInputJsonLength = 200 * 1024;

    private const int DefaultPage = 1;
    private const int DefaultPageSize = 20;

    private static readonly JsonSerializerOptions InputJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ManualCheckService _service;

    public SubsidiaryManualCheckController(ManualCheckService service)
    {
        _service = service;
    }

    /// <summary>チェック履歴の一覧（作成日時降順）をページングで返す。</summary>
    [HttpGet]
    public Task<ManualCheckPage> List(
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
        => _service.ListAsync(
            page <= 0 ? DefaultPage : page,
            pageSize <= 0 ? DefaultPageSize : pageSize,
            cancellationToken);

    /// <summary>洗濯表示アイコンのマスタ（選択 UI・突合の選択肢。静的カタログ）。</summary>
    [HttpGet("care-icons")]
    public CareIconCatalogResponse GetCareIcons() => CareIconCatalog.BuildResponse();

    /// <summary>
    /// 新規チェックの登録＋AI 抽出・突合のバックグラウンド開始。multipart/form-data で
    /// <c>productId</c>（任意 uuid）・<c>productLabel</c>（任意）・<c>input</c>（手入力スナップショット JSON・必須）・
    /// <c>tagImages</c>（タグ画像 1〜3枚）を受け取り、レコード＋画像を永続化して
    /// <b>status=processing の詳細を即座に返す</b>（AI 実行はバックグラウンド）。
    /// 受付上限超過は 429（<b>画像バッファ確保前・永続化前</b>）。
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(HardSizeLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = HardSizeLimitBytes)]
    public async Task<ManualCheckDetail> Create(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageMissing, 400,
                "multipart/form-data で手入力（input）とタグ画像を送信してください。");
        }

        var form = await Request.ReadFormAsync(cancellationToken);

        Guid? productId = null;
        var productIdRaw = form["productId"].ToString();
        if (!string.IsNullOrWhiteSpace(productIdRaw))
        {
            if (!Guid.TryParse(productIdRaw, out var parsed))
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    "productId は UUID 形式で指定してください。");
            }

            productId = parsed;
        }

        var input = ParseInput(form["input"].ToString());
        var tagFiles = form.Files.GetFiles("tagImages");

        // 合計サイズはバッファ確保前に file.Length の合算で早期拒否する（UNDX-REQ-008・413）。
        SubsidiaryCheckService.EnsureTotalSizeWithinLimit(tagFiles.Sum(file => file.Length));

        // 受付枠の予約は画像バッファ確保の前に行う（メモリ有界化。既存と同じ・W-1）。
        // using により以降の全失敗パスで確実に解放される。成功時は CreateAndStartAsync が
        // 解放責務をバックグラウンドタスクへ移すため二重解放しない。
        using var slot = _service.ReserveAiCheckSlotOrThrow();

        var tagImages = await ReadImagesAsync(tagFiles, ManualCheckService.MaxTagImages, cancellationToken);

        return await _service.CreateAndStartAsync(
            productId,
            NullIfEmpty(form["productLabel"]),
            input,
            tagImages,
            RequestIdentity.AuditUserOf(User),
            slot,
            cancellationToken);
    }

    /// <summary>チェック詳細。存在しない場合は 404（UNDX-DATA-005）。</summary>
    [HttpGet("{checkId}")]
    public Task<ManualCheckDetail> Get(string checkId, CancellationToken cancellationToken)
        => _service.GetDetailRequiredAsync(ParseId(checkId), cancellationToken);

    /// <summary>
    /// タグ画像のバイナリを返す（認証付き fetch でのプレビュー用）。同時実行数はサービス側で有界化する
    /// （既存副資材チェックと同じ配信枠を共有）。本文を自分で書き出し、書き終えるまで枠を保持する。
    /// </summary>
    [HttpGet("{checkId}/images/{imageId}")]
    public async Task<IActionResult> GetImage(
        string checkId, string imageId, CancellationToken cancellationToken)
    {
        var parsedCheckId = ParseId(checkId);
        var parsedImageId = ParseId(imageId);

        using var slot = await _service.AcquireImageDownloadSlotAsync(cancellationToken);
        var image = await _service.GetImageRequiredAsync(parsedCheckId, parsedImageId, cancellationToken);

        Response.ContentType = image.ContentType;
        Response.ContentLength = image.Data.Length;
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(image.FileName);
        Response.Headers.ContentDisposition = disposition.ToString();

        await Response.Body.WriteAsync(image.Data, cancellationToken);
        return new EmptyResult();
    }

    /// <summary>
    /// AI 再実行（手動回復パス）。対象は failed、または processing のまま滞留超過した孤児チェック。
    /// completed は 400、受付上限超過は 429。status=processing の詳細を即座に返す。
    /// </summary>
    [HttpPost("{checkId}/rerun")]
    public Task<ManualCheckDetail> Rerun(string checkId, CancellationToken cancellationToken)
        => _service.RerunAsync(ParseId(checkId), RequestIdentity.AuditUserOf(User), cancellationToken);

    // ------------------------------------------------------------

    /// <summary>手入力スナップショット JSON を検証・正規化して <see cref="ManualCheckInput"/> にする。</summary>
    private static ManualCheckInput ParseInput(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "手入力（input）が指定されていません。");
        }

        if (raw.Length > MaxInputJsonLength)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                "手入力（input）のサイズが大きすぎます。項目数・入力値を減らしてください。");
        }

        ManualCheckInput? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ManualCheckInput>(raw, InputJsonOptions);
        }
        catch (JsonException)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "手入力（input）の JSON 形式が不正です。");
        }

        if (parsed is null)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "手入力（input）が空です。");
        }

        // null 混入（欠落プロパティ）を空リストへ正規化し、下流（検証・突合）を NRE から守る。
        var fields = (parsed.Fields ?? new List<ManualInputField>())
            .Where(f => f is not null)
            .Select(NormalizeField)
            .ToList();

        return new ManualCheckInput(parsed.PatternId, parsed.PatternName ?? string.Empty, fields);
    }

    private static ManualInputField NormalizeField(ManualInputField field) => new(
        field.Key ?? string.Empty,
        field.Label ?? string.Empty,
        ManualFieldValueType.IsKnown(field.ValueType ?? string.Empty)
            ? field.ValueType!
            : ManualFieldValueType.String,
        field.Multiple,
        field.Compare ?? ManualFieldCompareMode.Text,
        (field.Values ?? new List<string>()).Where(v => v is not null).ToList(),
        (field.Ratios ?? new List<RatioComponent>())
            .Where(r => r is not null)
            .Select(r => new RatioComponent(r.Material ?? string.Empty, r.Percent))
            .ToList());

    /// <summary>アップロードファイルを検証しつつバッファへ読み込む（既存副資材チェックと同一方針）。</summary>
    private static async Task<IReadOnlyList<SubsidiaryCheckImageUpload>> ReadImagesAsync(
        IReadOnlyList<IFormFile> files, int maxCount, CancellationToken cancellationToken)
    {
        if (files.Count > maxCount)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageTooMany, 400,
                $"タグ画像は {maxCount} 枚以内にしてください（指定: {files.Count} 枚）。");
        }

        var images = new List<SubsidiaryCheckImageUpload>(files.Count);
        foreach (var file in files)
        {
            SubsidiaryCheckService.EnsureAllowedContentType(file.FileName, file.ContentType);
            if (file.Length > SubsidiaryCheckService.MaxImageSizeBytes)
            {
                throw new AppException(ErrorCodes.SubsidiaryImageTooLarge, 413,
                    $"{file.FileName} のサイズが上限"
                    + $"（{SubsidiaryCheckService.MaxImageSizeBytes / 1024 / 1024}MB）を超えています。");
            }

            var data = new byte[file.Length];
            if (file.Length > 0)
            {
                await using var stream = file.OpenReadStream();
                await stream.ReadExactlyAsync(data, cancellationToken);
            }

            var image = new SubsidiaryCheckImageUpload(file.FileName, file.ContentType, data);
            SubsidiaryCheckService.ValidateImage(image);
            images.Add(image);
        }

        return images;
    }

    private static Guid ParseId(string id)
        => Guid.TryParse(id, out var parsed)
            ? parsed
            : throw new AppException(ErrorCodes.SubsidiaryCheckNotFound, 404, "ID の形式が不正です。");

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
