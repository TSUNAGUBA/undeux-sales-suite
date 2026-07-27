using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
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
    // multipart 全体の transport 層上限（防御層）。
    // アプリ層の合計サイズ上限（SubsidiaryCheckService.MaxTotalImageBytes = 20MB）に
    // multipart のオーバーヘッド（boundary・パートヘッダ・その他フォーム値）分を加えた 25MB とし、
    // RagController（本体 20MB に対し transport 25MB）と同じ流儀に揃える。
    // 過大に取ると（旧値 60MB）、アプリ層で拒否される入力をボディ全体受信し切ってからでないと
    // 拒否できず、無駄な受信帯域とメモリを消費する。
    // 到達時の BadHttpRequestException / InvalidDataException は
    // ExceptionHandlingMiddleware が 413（UNDX-REQ-008）へマップする。
    // 注意: RequestSizeLimit は Kestrel の機能で TestServer 上では適用されないため、
    // この transport 層上限そのものは統合テストで E2E 検証できていない
    // （アプリ層の合計サイズ上限 UNDX-REQ-008 の経路はテスト済み）。
    private const long HardSizeLimitBytes = 25 * 1024 * 1024;

    // ページング既定値の SoT（ProductMasterController と同じ流儀。Repository は防御的クランプのみ行う）。
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
    /// 新規チェックの登録＋AI チェックのバックグラウンド開始。multipart/form-data で
    /// <c>productId</c>（任意 uuid）・<c>productLabel</c>（任意）・
    /// <c>instructionImages</c>（指示書画像 1〜3枚）・<c>tagImages</c>（タグ画像 1〜10枚）を受け取り、
    /// レコード＋画像を永続化したうえで <b>status=processing の詳細を即座に返す</b>。
    /// AI 実行はリクエストから切り離したバックグラウンドタスクで行い、フロントは
    /// <c>GET /api/subsidiary-check/{checkId}</c> をポーリングして completed / failed を待つ
    /// （共用リバースプロキシのタイムアウト約60秒を超えないための構造。mart 再構築と同じ流儀）。
    /// 受付上限超過は 429（UNDX-REQ-009）で拒否する（<b>画像バッファ確保前・永続化前</b>）。
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

        var instructionFiles = form.Files.GetFiles("instructionImages");
        var tagFiles = form.Files.GetFiles("tagImages");

        // 合計サイズはバッファ確保前に file.Length の合算で早期拒否する（UNDX-REQ-008・413）。
        SubsidiaryCheckService.EnsureTotalSizeWithinLimit(
            instructionFiles.Concat(tagFiles).Sum(file => file.Length));

        // 受付枠の予約は「画像バッファ（1リクエストあたり最大20MB）を確保する前」に行う。
        // バッファ確保後だと、429 で拒否される要求まで 20MB を確保することになり、
        // 受付上限（SubsidiaryCheckService.MaxConcurrentAiChecks）がピークメモリを有界化しない
        // （受付枠が満杯の状態で同時 POST が届くと GC ヒープハードリミットを突破しうる）。
        // using により、以降の全失敗パス（画像読取・検証・永続化・応答生成）で確実に解放される。
        // 成功時は CreateAndStartAsync が解放責務をバックグラウンドタスクへ移すため二重解放しない。
        using var slot = _service.ReserveAiCheckSlotOrThrow();

        var instructionImages = await ReadImagesAsync(
            instructionFiles, SubsidiaryCheckService.MaxInstructionImages, cancellationToken);
        var tagImages = await ReadImagesAsync(
            tagFiles, SubsidiaryCheckService.MaxTagImages, cancellationToken);

        return await _service.CreateAndStartAsync(
            productId,
            NullIfEmpty(form["productLabel"]),
            instructionImages,
            tagImages,
            RequestIdentity.AuditUserOf(User),
            slot,
            cancellationToken);
    }

    /// <summary>チェック詳細。存在しない場合は 404（UNDX-DATA-005）。</summary>
    [HttpGet("{checkId}")]
    public Task<SubsidiaryCheckDetail> Get(string checkId, CancellationToken cancellationToken)
        => _service.GetDetailRequiredAsync(ParseId(checkId), cancellationToken);

    /// <summary>
    /// チェック画像のバイナリを返す（認証付き fetch でのプレビュー・ダウンロード用）。
    /// 同時実行数はサービス側で有界化する
    /// （<see cref="SubsidiaryCheckService.MaxConcurrentImageDownloads"/>）。
    /// 直接リポジトリを呼ぶとこの上限を迂回してしまうため、必ずサービス経由にすること。
    /// <para>
    /// <b>File(...) を返さず本文を自分で書き出しているのは意図的。</b> IActionResult を返すと
    /// MVC が本文を書き出すのは<b>アクション復帰後</b>で、<c>using</c> スコープを抜けた後になる。
    /// それでは枠が DB 読取しか覆わず、byte[] の生存が枠の外へ出るため
    /// （低速回線では書き出しの方が支配的）、ピークメモリが同時閲覧者数に比例してしまう。
    /// ここで書き切ってから枠を返すことで、上限が閲覧者数に依存しなくなる。
    /// </para>
    /// </summary>
    [HttpGet("{checkId}/images/{imageId}")]
    public async Task<IActionResult> GetImage(
        string checkId, string imageId, CancellationToken cancellationToken)
    {
        using var slot = await _service.AcquireImageDownloadSlotAsync(cancellationToken);
        var image = await _service.GetImageRequiredAsync(
            ParseId(checkId), ParseId(imageId), cancellationToken);

        // File(...) が付ける応答ヘッダと同等のものを手で設定する。
        Response.ContentType = image.ContentType;
        Response.ContentLength = image.Data.Length;
        var disposition = new ContentDispositionHeaderValue("attachment");
        // 非 ASCII のファイル名も RFC 5987 形式で安全に載せる（FileResult と同じ扱い）。
        disposition.SetHttpFileName(image.FileName);
        Response.Headers.ContentDisposition = disposition.ToString();

        await Response.Body.WriteAsync(image.Data, cancellationToken);
        return new EmptyResult();
    }

    /// <summary>
    /// AI 再実行（手動回復パス）。対象は failed 状態、または processing のまま
    /// 一定時間（SubsidiaryCheckService.ProcessingStaleAfter）超経過した孤児チェック。
    /// completed のチェックは記録保護のため 400（UNDX-REQ-001）、受付上限超過は 429（UNDX-REQ-009）。
    /// 新規チェックと同じく AI 実行はバックグラウンドで行い、status=processing の詳細を即座に返す。
    /// 外部有料 API を起動する書込操作のため、実行者（監査ユーザー）をサービスへ渡して記録する。
    /// </summary>
    [HttpPost("{checkId}/rerun")]
    public Task<SubsidiaryCheckDetail> Rerun(string checkId, CancellationToken cancellationToken)
        => _service.RerunAsync(ParseId(checkId), RequestIdentity.AuditUserOf(User), cancellationToken);

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
            // 形式（Content-Type）・サイズはバッファ確保前に検証し、無駄な大容量確保を避ける。
            SubsidiaryCheckService.EnsureAllowedContentType(file.FileName, file.ContentType);
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

            // 読込後の最終検証（空ファイル・マジックバイト等はデータが必要）。
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
