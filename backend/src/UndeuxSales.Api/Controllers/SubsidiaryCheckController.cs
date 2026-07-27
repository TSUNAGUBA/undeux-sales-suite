using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Core;
using UndeuxSales.Core.SubsidiaryCheck;
using UndeuxSales.Infrastructure.SubsidiaryCheck;

namespace UndeuxSales.Api.Controllers;

/// <summary>
/// 副資材チェック（指示書×タグ画像の AI 検品）API。
/// 履歴一覧・新規チェック（multipart）・詳細・画像取得・再実行・ルールブックを提供する。
/// </summary>
[ApiController]
[Authorize]
[Route("api/subsidiary-check")]
public sealed class SubsidiaryCheckController : ControllerBase
{
    // multipart 全体の上限（指示書3枚＋タグ10枚 × 各5MB ＋ メタ）。
    private const long HardSizeLimitBytes = 60 * 1024 * 1024;
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 20;

    private readonly SubsidiaryCheckRepository _repository;
    private readonly SubsidiaryCheckService _service;

    public SubsidiaryCheckController(
        SubsidiaryCheckRepository repository,
        SubsidiaryCheckService service)
    {
        _repository = repository;
        _service = service;
    }

    /// <summary>チェック履歴の一覧（作成日時降順）をページングで返す。</summary>
    [HttpGet]
    public Task<SubsidiaryCheckPage> List(
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
        => _repository.ListAsync(
            page <= 0 ? DefaultPage : page,
            pageSize <= 0 ? DefaultPageSize : pageSize,
            cancellationToken);

    /// <summary>
    /// 新規チェックの実行。multipart/form-data で
    /// <c>productId</c>（任意 uuid）・<c>productLabel</c>（任意）・
    /// <c>instructionImages</c>（指示書画像 1〜3枚）・<c>tagImages</c>（タグ画像 1〜10枚）を受け取り、
    /// レコード＋画像を永続化 → AI 同期実行 → 結果を保存して詳細を返す。
    /// AI 失敗時は failed 状態の詳細を返す（エラーを throw しない。登録済み記録は残る）。
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(HardSizeLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = HardSizeLimitBytes)]
    public async Task<SubsidiaryCheckDetail> Create(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageMissing, 400,
                "multipart/form-data で指示書画像とタグ画像を送信してください。");
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

        var instructionImages = await ReadImagesAsync(
            form.Files.GetFiles("instructionImages"), SubsidiaryCheckService.MaxInstructionImages,
            cancellationToken);
        var tagImages = await ReadImagesAsync(
            form.Files.GetFiles("tagImages"), SubsidiaryCheckService.MaxTagImages, cancellationToken);

        return await _service.CreateAndRunAsync(
            productId,
            NullIfEmpty(form["productLabel"]),
            instructionImages,
            tagImages,
            RequestIdentity.AuditUserOf(User),
            cancellationToken);
    }

    /// <summary>チェック詳細。存在しない場合は 404（UNDX-DATA-005）。</summary>
    [HttpGet("{checkId}")]
    public Task<SubsidiaryCheckDetail> Get(string checkId, CancellationToken cancellationToken)
        => _service.GetDetailRequiredAsync(ParseId(checkId), cancellationToken);

    /// <summary>チェック画像のバイナリを返す（認証付き fetch でのプレビュー・ダウンロード用）。</summary>
    [HttpGet("{checkId}/images/{imageId}")]
    public async Task<IActionResult> GetImage(
        string checkId, string imageId, CancellationToken cancellationToken)
    {
        var image = await _repository.GetImageAsync(ParseId(checkId), ParseId(imageId), cancellationToken)
                    ?? throw new AppException(ErrorCodes.SubsidiaryCheckNotFound, 404,
                        "指定された画像が見つかりません。");
        return File(image.Data, image.ContentType, image.FileName);
    }

    /// <summary>
    /// 失敗（failed）状態のチェックの AI 再実行（手動回復パス）。
    /// completed のチェックは記録保護のため 400（UNDX-REQ-001）。
    /// </summary>
    [HttpPost("{checkId}/rerun")]
    public Task<SubsidiaryCheckDetail> Rerun(string checkId, CancellationToken cancellationToken)
        => _service.RerunAsync(ParseId(checkId), cancellationToken);

    /// <summary>ルールブック（チェック基準の静的カタログ。AI プロンプトと同一 SoT から生成）。</summary>
    [HttpGet("rules")]
    public SubsidiaryRuleBook GetRules() => SubsidiaryCheckRuleCatalog.BuildRuleBook();

    /// <summary>
    /// アップロードファイルを検証しつつバッファへ読み込む。
    /// サイズ・形式はバッファ確保前に検証し、無駄な大容量確保を避ける（RagController と同じ方針）。
    /// 枚数超過も読込前に検証する。
    /// </summary>
    private static async Task<IReadOnlyList<SubsidiaryCheckImageUpload>> ReadImagesAsync(
        IReadOnlyList<IFormFile> files, int maxCount, CancellationToken cancellationToken)
    {
        if (files.Count > maxCount)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageTooMany, 400,
                $"画像は {maxCount} 枚以内にしてください（指定: {files.Count} 枚）。");
        }

        var images = new List<SubsidiaryCheckImageUpload>(files.Count);
        foreach (var file in files)
        {
            if (file.Length > SubsidiaryCheckService.MaxImageSizeBytes)
            {
                throw new AppException(ErrorCodes.SubsidiaryImageTooLarge, 413,
                    $"{file.FileName} のサイズが上限"
                    + $"（{SubsidiaryCheckService.MaxImageSizeBytes / 1024 / 1024}MB）を超えています。");
            }

            // MemoryStream 経由の二重バッファを避け、単一バッファへ直接読み込む。
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
