using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

    /// <summary>許可する画像 Content-Type（jpeg / png のみ）。</summary>
    private static readonly IReadOnlyList<string> AllowedContentTypes = new[]
    {
        "image/jpeg", "image/png",
    };

    private readonly SubsidiaryCheckRepository _repository;
    private readonly IAiChatClient _aiClient;
    private readonly AiOptions _aiOptions;
    private readonly ILogger<SubsidiaryCheckService> _logger;

    public SubsidiaryCheckService(
        SubsidiaryCheckRepository repository,
        IAiChatClient aiClient,
        IOptions<AiOptions> aiOptions,
        ILogger<SubsidiaryCheckService> logger)
    {
        _repository = repository;
        _aiClient = aiClient;
        _aiOptions = aiOptions.Value;
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
    /// 失敗（failed）状態のチェックのみ AI を再実行する（手動回復パス）。
    /// completed のチェックは記録保護（原則2）のため 400 を返す。
    /// </summary>
    public async Task<SubsidiaryCheckDetail> RerunAsync(
        Guid checkId, CancellationToken cancellationToken = default)
    {
        var current = await GetDetailRequiredAsync(checkId, cancellationToken);
        if (current.Summary.Status != SubsidiaryCheckStatus.Failed)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                "再実行できるのは失敗（failed）状態のチェックのみです（完了済みの判定結果は保護されます）。");
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

    /// <summary>詳細を取得する。未存在は 404（UNDX-DATA-005）。</summary>
    public async Task<SubsidiaryCheckDetail> GetDetailRequiredAsync(
        Guid checkId, CancellationToken cancellationToken = default) =>
        await _repository.GetDetailAsync(checkId, cancellationToken)
        ?? throw new AppException(ErrorCodes.SubsidiaryCheckNotFound, 404);

    /// <summary>
    /// 画像の枚数・形式・サイズを検証する（コントローラの事前チェックと二重でも安全な再検証）。
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
    }

    /// <summary>画像1枚の形式・サイズを検証する（バッファ確保後の最終検証）。</summary>
    public static void ValidateImage(SubsidiaryCheckImageUpload image)
    {
        if (!AllowedContentTypes.Contains(image.ContentType))
        {
            throw new AppException(ErrorCodes.SubsidiaryImageInvalidFormat, 400,
                $"{image.FileName} の形式（{image.ContentType}）には対応していません（JPEG / PNG のみ）。");
        }

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
    }

    /// <summary>
    /// AI チェックを実行し、結果（completed / failed）を記録する。
    /// 失敗は throw せず failed 記録に変換する（キャンセルのみ記録後に再 throw）。
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
            // 出力トークンは設定上限（MaxOutputTokens）の範囲内で推奨値まで使用する。
            var maxTokens = Math.Min(SubsidiaryCheckPromptBuilder.RecommendedMaxTokens, _aiOptions.MaxOutputTokens);

            var responseText = await _aiClient.AnalyzeImagesAsync(
                aiImages, systemPrompt, userPrompt, maxTokens, cancellationToken);

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

            await _repository.UpdateResultAsync(
                checkId, SubsidiaryCheckStatus.Completed, judgment, failCount, warnCount,
                SubsidiaryCheckRepository.SerializeFindings(parsed.Findings),
                _aiClient.ChatModel, errorMessage: null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // キャンセルでも processing 孤児を残さない: failed 記録後に再 throw する。
            await RecordFailureAsync(checkId, "処理が中断されました（クライアント切断またはタイムアウト）。再実行してください。");
            throw;
        }
        catch (Exception ex)
        {
            // AI 呼出失敗は主要フロー（記録）を止めない（原則4）。failed 記録で応答し、rerun で回復できる。
            _logger.LogWarning(ex, "副資材チェックの AI 実行に失敗しました（checkId: {CheckId}）", checkId);
            var message = ex is AppException app ? $"{app.Error.Code}: {app.Message}" : ex.Message;
            await RecordFailureAsync(checkId, message);
        }
    }

    /// <summary>
    /// failed 記録を書き込む。キャンセル済みでも記録が届くよう CancellationToken.None で実行し、
    /// 記録自体の失敗はログのみとする（記録失敗で元のエラーを覆い隠さない）。
    /// </summary>
    private async Task RecordFailureAsync(Guid checkId, string errorMessage)
    {
        try
        {
            await _repository.UpdateResultAsync(
                checkId, SubsidiaryCheckStatus.Failed, judgment: null, failCount: 0, warnCount: 0,
                findingsJson: null, _aiClient.ChatModel, errorMessage, CancellationToken.None);
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

    private static IReadOnlyList<SubsidiaryCheckImageRecord> BuildImageRecords(
        IReadOnlyList<SubsidiaryCheckImageUpload> instructionImages,
        IReadOnlyList<SubsidiaryCheckImageUpload> tagImages)
    {
        var records = new List<SubsidiaryCheckImageRecord>(instructionImages.Count + tagImages.Count);
        records.AddRange(instructionImages.Select((image, index) => new SubsidiaryCheckImageRecord(
            SubsidiaryCheckImageKind.Instruction, image.FileName, image.ContentType, image.Data, index)));
        records.AddRange(tagImages.Select((image, index) => new SubsidiaryCheckImageRecord(
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
