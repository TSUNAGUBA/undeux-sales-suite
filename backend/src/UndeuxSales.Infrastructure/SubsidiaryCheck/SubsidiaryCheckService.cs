using Microsoft.Extensions.Logging;
using UndeuxSales.Core;
using UndeuxSales.Core.Rag;
using UndeuxSales.Core.SubsidiaryCheck;
using UndeuxSales.Infrastructure.Ai;
using UndeuxSales.Infrastructure.Rag;

namespace UndeuxSales.Infrastructure.SubsidiaryCheck;

/// <summary>アップロードされたチェック画像1枚（コントローラ→サービスの入力）。</summary>
public sealed record SubsidiaryCheckImageUpload(string FileName, string ContentType, byte[] Data);

/// <summary>
/// 副資材チェックのオーケストレーション。
/// <para>
/// フロー: 検証 → INSERT（processing。SoT への記録が先）→ AI 呼出 → 応答解析 → UPDATE（completed / failed）。
/// AI 呼出・解析の失敗は例外にせず failed 記録＋エラー格納で握り、failed 状態の詳細を返す
/// （グレースフルデグラデーション・原則4。登録済み記録は残り、再実行（rerun）で回復できる）。
/// キャンセル（OperationCanceledException）も failed 記録にしてから再 throw し、processing 孤児を残さない。
/// </para>
/// </summary>
public sealed class SubsidiaryCheckService
{
    /// <summary>指示書画像の最小枚数。</summary>
    public const int MinInstructionImages = 1;

    /// <summary>指示書画像の最大枚数。</summary>
    public const int MaxInstructionImages = 3;

    /// <summary>タグ画像の最小枚数。</summary>
    public const int MinTagImages = 1;

    /// <summary>タグ画像の最大枚数。</summary>
    public const int MaxTagImages = 10;

    /// <summary>
    /// 画像1枚の上限サイズ。Anthropic API の画像上限（約5MB/枚）に由来する既存定数を再利用する（原則3）。
    /// </summary>
    public const long MaxImageSizeBytes = KnowledgeIngestionService.MaxImageFileSizeBytes;

    /// <summary>
    /// 全画像（指示書＋タグ）の合計 raw サイズ上限（20MB）。
    /// 根拠: Anthropic Messages API はリクエスト全体で 32MB 制限があり、画像は base64 で約 1.33 倍に
    /// 膨張する。20MB raw ≒ 26.6MB base64 ＋ プロンプト分で 32MB に対して安全側の値。
    /// この上限がないと「各5MB × 最大13枚 = 65MB」の正当入力が AI 呼出で構造的に失敗する。
    /// フロント（utils/subsidiaryCheck.ts の SUBSIDIARY_TOTAL_IMAGE_MAX_BYTES）と
    /// UNDX-REQ-008 のメッセージ文言はこの値と同期させること。
    /// </summary>
    public const long MaxTotalImageBytes = 20 * 1024 * 1024;

    /// <summary>
    /// processing のまま経過した場合に「孤児（プロセスクラッシュ等で結果が確定しないレコード）」と
    /// みなして再実行（rerun）を許可するまでの時間。
    /// 根拠: AI 呼出タイムアウト（<see cref="AiCallTimeout"/> = 120秒）＋記録処理を含めても
    /// 正常系で10分を超えて processing に留まることはないため、10分超は孤児と判断できる。
    /// フロント（utils/subsidiaryCheck.ts の SUBSIDIARY_PROCESSING_STALE_MS）と同期させること。
    /// </summary>
    public static readonly TimeSpan ProcessingStaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// AI チェック（画像分析）専用の最大出力トークン数。
    /// チャット用のグローバル設定（AiOptions.MaxOutputTokens。既定 2048）とは意図的に分離する:
    /// findings JSON（3カテゴリ×複数指摘）は 2048 トークンでは切り詰められ UNDX-AI-009 になるため。
    /// </summary>
    private const int CheckMaxOutputTokens = SubsidiaryCheckPromptBuilder.RecommendedMaxTokens;

    /// <summary>
    /// AI 呼出1回のタイムアウト（120秒）。
    /// 根拠: 最大13枚の画像分析でも通常応答は数十秒であり、ネットワーク断・API 側の張り付き等の
    /// 異常時にリクエストスレッドと processing 状態が無期限に滞留するのを防ぐ余裕値。
    /// </summary>
    private static readonly TimeSpan AiCallTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// 同時 AI チェック実行数の上限（3並列）。
    /// 根拠: 1リクエストで最大 20MB の画像バッファ＋base64 変換（約1.33倍）を保持するため、
    /// 無制限の並列実行はメモリ圧迫と Anthropic API のレート制限超過を招く。
    /// AI 呼出部分のみを制限し、DB 操作はセマフォの外で行う（DB まで直列化しない）。
    /// </summary>
    private static readonly SemaphoreSlim AiCallSemaphore = new(3);

    /// <summary>許可する画像 Content-Type（jpeg / png のみ）。</summary>
    private static readonly IReadOnlyList<string> AllowedContentTypes = new[]
    {
        "image/jpeg", "image/png",
    };

    private readonly SubsidiaryCheckRepository _repository;
    private readonly IAiChatClient _aiClient;
    private readonly ILogger<SubsidiaryCheckService> _logger;

    public SubsidiaryCheckService(
        SubsidiaryCheckRepository repository,
        IAiChatClient aiClient,
        ILogger<SubsidiaryCheckService> logger)
    {
        _repository = repository;
        _aiClient = aiClient;
        _logger = logger;
    }

    /// <summary>
    /// 新規チェックを登録し、AI チェックを同期実行して結果詳細を返す。
    /// AI 未設定（IsConfigured=false）は永続化前に 503（UNDX-AI-008）を throw する（無駄なレコードを作らない）。
    /// </summary>
    public async Task<SubsidiaryCheckDetail> CreateAndRunAsync(
        Guid? productId,
        string? productLabel,
        IReadOnlyList<SubsidiaryCheckImageUpload> instructionImages,
        IReadOnlyList<SubsidiaryCheckImageUpload> tagImages,
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        ValidateImages(instructionImages, tagImages);

        if (!_aiClient.IsConfigured)
        {
            throw new AppException(ErrorCodes.AiNotConfigured, 503);
        }

        SubsidiaryCheckProductInfo? product = null;
        if (productId is { } id)
        {
            product = await _repository.GetProductInfoAsync(id, cancellationToken)
                      ?? throw new AppException(ErrorCodes.ProductNotFound, 404);
        }

        var label = ResolveProductLabel(productLabel, product);

        // SoT（subsidiary_check）への記録を先に確定してから AI を呼び出す（原則6）。
        var checkId = Guid.NewGuid();
        await _repository.InsertAsync(
            checkId, productId, label, createdBy, BuildImageRecords(instructionImages, tagImages),
            cancellationToken);

        await RunAiAsync(checkId, product, label,
            ToAiImages(instructionImages, tagImages), cancellationToken);

        return await GetDetailRequiredAsync(checkId, cancellationToken);
    }

    /// <summary>
    /// AI を再実行する（手動回復パス）。許可条件は
    /// 「status=failed」または「status=processing かつ作成から <see cref="ProcessingStaleAfter"/> 超経過
    /// （プロセスクラッシュ等で結果が確定しない processing 孤児の回復）」。
    /// completed のチェックは記録保護（原則2）のため 400 を返す。
    /// </summary>
    public async Task<SubsidiaryCheckDetail> RerunAsync(
        Guid checkId, CancellationToken cancellationToken = default)
    {
        var current = await GetDetailRequiredAsync(checkId, cancellationToken);
        if (!CanRerun(current.Summary))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                "再実行できるのは失敗（failed）状態のチェック、または処理中（processing）のまま"
                + $"{(int)ProcessingStaleAfter.TotalMinutes}分以上経過したチェックのみです"
                + "（完了済みの判定結果は保護されます）。");
        }

        if (!_aiClient.IsConfigured)
        {
            throw new AppException(ErrorCodes.AiNotConfigured, 503);
        }

        // 商品が後から削除されている場合（FK SET NULL）は商品情報なしで再実行する。
        var product = current.Summary.ProductId is { } productId
            ? await _repository.GetProductInfoAsync(productId, cancellationToken)
            : null;

        var stored = await _repository.GetImagesWithDataAsync(checkId, cancellationToken);
        var aiImages = ToAiImages(
            stored.Where(i => i.Kind == SubsidiaryCheckImageKind.Instruction).ToList(),
            stored.Where(i => i.Kind == SubsidiaryCheckImageKind.Tag).ToList());

        await RunAiAsync(checkId, product, current.Summary.ProductLabel, aiImages, cancellationToken);

        return await GetDetailRequiredAsync(checkId, cancellationToken);
    }

    /// <summary>再実行を許可するか（failed、または作成から一定時間超経過した processing 孤児）。</summary>
    private static bool CanRerun(SubsidiaryCheckSummary summary)
        => summary.Status == SubsidiaryCheckStatus.Failed
           || (summary.Status == SubsidiaryCheckStatus.Processing
               // created_at は timestamptz（Npgsql は UTC の DateTime を返す）。UtcNow と直接比較できる。
               && DateTime.UtcNow - summary.CreatedAt >= ProcessingStaleAfter);

    /// <summary>詳細を取得する。未存在は 404（UNDX-DATA-005）。</summary>
    public async Task<SubsidiaryCheckDetail> GetDetailRequiredAsync(
        Guid checkId, CancellationToken cancellationToken = default) =>
        await _repository.GetDetailAsync(checkId, cancellationToken)
        ?? throw new AppException(ErrorCodes.SubsidiaryCheckNotFound, 404);

    /// <summary>
    /// 画像の枚数・形式・サイズ（各上限＋合計上限）を検証する
    /// （コントローラの事前チェックと二重でも安全な再検証）。
    /// </summary>
    public static void ValidateImages(
        IReadOnlyList<SubsidiaryCheckImageUpload> instructionImages,
        IReadOnlyList<SubsidiaryCheckImageUpload> tagImages)
    {
        if (instructionImages.Count < MinInstructionImages || tagImages.Count < MinTagImages)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageMissing, 400,
                $"指示書画像 {instructionImages.Count} 枚・タグ画像 {tagImages.Count} 枚が指定されました。"
                + $"指示書画像は {MinInstructionImages}〜{MaxInstructionImages} 枚、"
                + $"タグ画像は {MinTagImages}〜{MaxTagImages} 枚が必要です。");
        }

        if (instructionImages.Count > MaxInstructionImages || tagImages.Count > MaxTagImages)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageTooMany, 400,
                $"指示書画像は {MaxInstructionImages} 枚以内、タグ画像は {MaxTagImages} 枚以内にしてください"
                + $"（指定: 指示書 {instructionImages.Count} 枚 / タグ {tagImages.Count} 枚）。");
        }

        foreach (var image in instructionImages.Concat(tagImages))
        {
            ValidateImage(image);
        }

        var totalBytes = instructionImages.Concat(tagImages).Sum(image => image.Data.LongLength);
        EnsureTotalSizeWithinLimit(totalBytes);
    }

    /// <summary>
    /// 全画像の合計サイズが上限（<see cref="MaxTotalImageBytes"/>）以内であることを検証する。
    /// 超過時は 413（UNDX-REQ-008）。コントローラのバッファ確保前チェックと本検証で共用する。
    /// </summary>
    public static void EnsureTotalSizeWithinLimit(long totalBytes)
    {
        if (totalBytes > MaxTotalImageBytes)
        {
            throw new AppException(ErrorCodes.UploadTotalTooLarge, 413,
                $"画像の合計サイズが上限（{MaxTotalImageBytes / 1024 / 1024}MB）を超えています"
                + $"（指定: 約 {totalBytes / 1024.0 / 1024.0:F1}MB）。"
                + "画像の枚数を減らすか、解像度を下げて再試行してください。");
        }
    }

    /// <summary>
    /// Content-Type が許可形式（JPEG / PNG）であることを検証する。
    /// コントローラのバッファ確保前チェックと <see cref="ValidateImage"/> で共用する。
    /// </summary>
    public static void EnsureAllowedContentType(string fileName, string contentType)
    {
        if (!AllowedContentTypes.Contains(contentType))
        {
            throw new AppException(ErrorCodes.SubsidiaryImageInvalidFormat, 400,
                $"{fileName} の形式（{contentType}）には対応していません（JPEG / PNG のみ）。");
        }
    }

    /// <summary>
    /// 画像1枚の形式・サイズを検証する（バッファ確保後の最終検証）。
    /// Content-Type の申告だけでなく、先頭バイト（マジックバイト）が JPEG（FF D8 FF）/
    /// PNG（89 50 4E 47）として妥当かも検証する（Content-Type 偽装対策）。
    /// </summary>
    public static void ValidateImage(SubsidiaryCheckImageUpload image)
    {
        EnsureAllowedContentType(image.FileName, image.ContentType);

        if (image.Data.LongLength == 0)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageInvalidFormat, 400,
                $"{image.FileName} が空ファイルです。");
        }

        if (image.Data.LongLength > MaxImageSizeBytes)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageTooLarge, 413,
                $"{image.FileName} のサイズが上限（{MaxImageSizeBytes / 1024 / 1024}MB）を超えています。");
        }

        var magicValid = image.ContentType == "image/png" ? HasPngMagic(image.Data) : HasJpegMagic(image.Data);
        if (!magicValid)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageInvalidFormat, 400,
                $"{image.FileName} のファイル内容が {image.ContentType} の画像形式ではありません"
                + "（JPEG / PNG のみ対応）。");
        }
    }

    /// <summary>JPEG のマジックバイト（FF D8 FF）を持つか。</summary>
    private static bool HasJpegMagic(byte[] data)
        => data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    /// <summary>PNG のマジックバイト（89 50 4E 47）を持つか。</summary>
    private static bool HasPngMagic(byte[] data)
        => data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;

    /// <summary>
    /// AI チェックを実行し、結果（completed / failed）を記録する。
    /// 失敗は throw せず failed 記録に変換する（クライアント切断によるキャンセルのみ記録後に再 throw）。
    /// AI 呼出タイムアウト（リクエスト自体は未キャンセル）はキャンセル扱いにせず failed 記録として応答する。
    /// </summary>
    private async Task RunAiAsync(
        Guid checkId,
        SubsidiaryCheckProductInfo? product,
        string productLabel,
        IReadOnlyList<AiImageInput> aiImages,
        CancellationToken cancellationToken)
    {
        try
        {
            var systemPrompt = SubsidiaryCheckPromptBuilder.BuildSystemPrompt();
            var userPrompt = SubsidiaryCheckPromptBuilder.BuildUserPrompt(product, productLabel);

            string? responseText = null;
            // AI 呼出のみを同時実行制限・タイムアウトで保護する（DB 操作はセマフォの外で行う）。
            await AiCallSemaphore.WaitAsync(cancellationToken);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(AiCallTimeout);
                try
                {
                    responseText = await _aiClient.AnalyzeImagesAsync(
                        aiImages, systemPrompt, userPrompt, CheckMaxOutputTokens, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // タイムアウト発火（リクエスト側は未キャンセル）: 本物のキャンセル
                    // （クライアント切断）と区別し、throw せず failed 記録へ進む（記録はセマフォ解放後）。
                }
            }
            finally
            {
                AiCallSemaphore.Release();
            }

            if (responseText is null)
            {
                // タイムアウト: failed 記録＋failed Detail の返却で応答する（キャンセル扱いにしない）。
                _logger.LogWarning(
                    "副資材チェックの AI 呼出がタイムアウトしました（checkId: {CheckId}、上限: {Timeout}秒）",
                    checkId, (int)AiCallTimeout.TotalSeconds);
                await RecordFailureAsync(checkId,
                    $"{ErrorCodes.AiCallFailed.Code}: AI 呼出がタイムアウトしました。再実行してください。");
                return;
            }

            var parsed = SubsidiaryCheckResponseParser.Parse(responseText);
            if (!parsed.Success)
            {
                _logger.LogWarning(
                    "副資材チェックの AI 応答を解析できませんでした（checkId: {CheckId}）: {Error}",
                    checkId, parsed.Error);
                await RecordFailureAsync(checkId,
                    $"{ErrorCodes.AiResponseUnparseable.Code}: {parsed.Error}");
                return;
            }

            var (failCount, warnCount) = SubsidiaryCheckJudgment.Count(parsed.Findings);
            var judgment = SubsidiaryCheckJudgment.Decide(failCount, warnCount);

            var updated = await _repository.UpdateResultAsync(
                checkId, SubsidiaryCheckStatus.Completed, judgment, failCount, warnCount,
                SubsidiaryCheckRepository.SerializeFindings(parsed.Findings),
                _aiClient.ChatModel, errorMessage: null, cancellationToken);
            if (updated == 0)
            {
                // 並行 rerun 等で先に completed が確定していた場合。記録保護（原則2）のため結果は破棄する。
                _logger.LogWarning(
                    "副資材チェックは既に completed のため結果を破棄しました（checkId: {CheckId}）", checkId);
            }
        }
        catch (OperationCanceledException)
        {
            // クライアント切断によるキャンセルでも processing 孤児を残さない: failed 記録後に再 throw する。
            await RecordFailureAsync(checkId, "処理が中断されました（クライアント切断）。再実行してください。");
            throw;
        }
        catch (Exception ex)
        {
            // AI 呼出失敗は主要フロー（記録）を止めない（原則4）。failed 記録で応答し、rerun で回復できる。
            // error_message には採番コード＋日本語の汎用文言のみを保存し、
            // 生の例外メッセージ（SDK 内部文言等）はログに留める（ユーザー露出防止）。
            _logger.LogWarning(ex, "副資材チェックの AI 実行に失敗しました（checkId: {CheckId}）", checkId);
            var message = ex is AppException app
                ? BuildFailureMessage(app)
                : $"{ErrorCodes.Unexpected.Code}: {ErrorCodes.Unexpected.Summary} 再実行してください。";
            await RecordFailureAsync(checkId, message);
        }
    }

    /// <summary>
    /// failed 記録用のエラーメッセージ（採番コード＋概要＋整形済み詳細）を構築する。
    /// AppException の Message はアプリ側で整形した日本語文言のみが入る前提
    /// （AnthropicAiClient は SDK の生メッセージを詳細に入れない）。
    /// </summary>
    private static string BuildFailureMessage(AppException app)
        => app.Message == app.Error.Summary
            ? $"{app.Error.Code}: {app.Error.Summary}"
            : $"{app.Error.Code}: {app.Error.Summary} {app.Message}";

    /// <summary>
    /// failed 記録を書き込む。キャンセル済みでも記録が届くよう CancellationToken.None で実行し、
    /// 記録自体の失敗はログのみとする（記録失敗で元のエラーを覆い隠さない）。
    /// 既に completed のチェックは状態遷移ガード（記録保護・原則2）により更新されない（警告ログのみ）。
    /// </summary>
    private async Task RecordFailureAsync(Guid checkId, string errorMessage)
    {
        try
        {
            var updated = await _repository.UpdateResultAsync(
                checkId, SubsidiaryCheckStatus.Failed, judgment: null, failCount: 0, warnCount: 0,
                findingsJson: null, _aiClient.ChatModel, errorMessage, CancellationToken.None);
            if (updated == 0)
            {
                _logger.LogWarning(
                    "副資材チェックは既に completed のため失敗記録を破棄しました（checkId: {CheckId}）", checkId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "副資材チェックの失敗記録に失敗しました（checkId: {CheckId}）", checkId);
        }
    }

    private static string ResolveProductLabel(string? productLabel, SubsidiaryCheckProductInfo? product)
    {
        if (!string.IsNullOrWhiteSpace(productLabel))
        {
            return productLabel.Trim();
        }

        return product is null
            ? string.Empty
            : $"{product.ProductSign} {product.ProductTypeCrd} {product.ProductName}";
    }

    private static IReadOnlyList<SubsidiaryCheckImagePayload> BuildImageRecords(
        IReadOnlyList<SubsidiaryCheckImageUpload> instructionImages,
        IReadOnlyList<SubsidiaryCheckImageUpload> tagImages)
    {
        var records = new List<SubsidiaryCheckImagePayload>(instructionImages.Count + tagImages.Count);
        records.AddRange(instructionImages.Select((image, index) => new SubsidiaryCheckImagePayload(
            SubsidiaryCheckImageKind.Instruction, image.FileName, image.ContentType, image.Data, index)));
        records.AddRange(tagImages.Select((image, index) => new SubsidiaryCheckImagePayload(
            SubsidiaryCheckImageKind.Tag, image.FileName, image.ContentType, image.Data, index)));
        return records;
    }

    /// <summary>新規アップロード画像を AI 入力（指示書→タグの順・ラベル付き）へ変換する。</summary>
    private static IReadOnlyList<AiImageInput> ToAiImages(
        IReadOnlyList<SubsidiaryCheckImageUpload> instructionImages,
        IReadOnlyList<SubsidiaryCheckImageUpload> tagImages)
        => BuildAiImages(
            instructionImages.Select(i => (i.Data, i.ContentType)).ToList(),
            tagImages.Select(i => (i.Data, i.ContentType)).ToList());

    /// <summary>保存済み画像を AI 入力へ変換する（再実行用）。</summary>
    private static IReadOnlyList<AiImageInput> ToAiImages(
        IReadOnlyList<SubsidiaryCheckImagePayload> instructionImages,
        IReadOnlyList<SubsidiaryCheckImagePayload> tagImages)
        => BuildAiImages(
            instructionImages.Select(i => (i.Data, i.ContentType)).ToList(),
            tagImages.Select(i => (i.Data, i.ContentType)).ToList());

    /// <summary>指示書→タグの順にラベル（「指示書画像（正） 1/3」等）を付けて AI 入力列を構築する。</summary>
    private static IReadOnlyList<AiImageInput> BuildAiImages(
        IReadOnlyList<(byte[] Data, string ContentType)> instructionImages,
        IReadOnlyList<(byte[] Data, string ContentType)> tagImages)
    {
        var images = new List<AiImageInput>(instructionImages.Count + tagImages.Count);
        images.AddRange(instructionImages.Select((image, index) => new AiImageInput(
            image.Data, image.ContentType,
            SubsidiaryCheckPromptBuilder.BuildImageLabel(
                SubsidiaryCheckImageKind.Instruction, index + 1, instructionImages.Count))));
        images.AddRange(tagImages.Select((image, index) => new AiImageInput(
            image.Data, image.ContentType,
            SubsidiaryCheckPromptBuilder.BuildImageLabel(
                SubsidiaryCheckImageKind.Tag, index + 1, tagImages.Count))));
        return images;
    }
}
