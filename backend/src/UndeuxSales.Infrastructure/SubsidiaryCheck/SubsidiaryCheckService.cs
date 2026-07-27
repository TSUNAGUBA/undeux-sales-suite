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
    /// 商品ラベル（クライアント指定の任意テキスト）の最大文字数（200文字）。
    /// 根拠: 商品ラベルは「品番・商品名相当の短い識別文字列」を想定した表示用スナップショットであり、
    /// 上限がないと Kestrel の ValueLengthLimit（既定 4MB）まで受理してしまう。
    /// 巨大テキストの永続化・一覧展開・AI プロンプトへの混入という増幅経路を塞ぐ
    /// （inventory_action_flag.note を 1,000 文字に制限した先例と同じ方針）。
    /// フロント（utils/subsidiaryCheck.ts の SUBSIDIARY_PRODUCT_LABEL_MAX_LENGTH）と同期させること。
    /// </summary>
    public const int MaxProductLabelLength = 200;

    /// <summary>
    /// processing のまま経過した場合に「孤児（プロセスクラッシュ等で結果が確定しないレコード）」と
    /// みなして再実行（rerun）を許可するまでの時間。基準時刻は started_at（最後の AI 実行開始日時）。
    /// <para>
    /// 根拠: 1リクエストの processing 滞留時間は
    /// 「セマフォ待機（<see cref="AiCallQueueTimeout"/> = 30秒で打切り）
    /// ＋ AI 呼出（<see cref="AiCallTimeout"/> = 120秒で打切り）＋ 記録処理（DB 更新・秒オーダ）」で
    /// <b>有界</b>であり、上限は実質3分以内。待機超過・呼出タイムアウトはいずれも failed 記録で
    /// 確定するため、これを超えて processing に留まるのはプロセス消失（クラッシュ・強制終了）だけ。
    /// 有界化された最大滞留時間 約3分に対し十分な余裕を見て10分とする
    /// （正常に実行待ちのチェックを孤児と誤判定しないことを優先）。
    /// </para>
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
    /// 同時 AI チェック実行数の上限（1＝直列化）。
    /// <para>
    /// <b>メモリ収支（同時実行数を 1 にした根拠）:</b> api コンテナのメモリ上限は本番
    /// （infra/aws/docker-compose.ec2.yml）・ローカル（docker-compose.yml）とも 512m で、
    /// cgroup 制限下の .NET GC ヒープハードリミットは既定でその 75% ＝ 約 384MB。
    /// 一方 AI 呼出中の1リクエストが同時に保持する量は、設計上許容された正常系の最大入力
    /// （<see cref="MaxTotalImageBytes"/> = 20MB）で概算 <b>約100MB</b>:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>画像バッファ byte[]: 約 20MB</description></item>
    ///   <item><description>base64 文字列: 20MB → 約 26.7M 文字。.NET string は UTF-16
    ///     （2バイト/文字）のため約 53MB</description></item>
    ///   <item><description>HTTP リクエストボディの UTF-8 直列化: 約 27MB</description></item>
    /// </list>
    /// <para>
    /// 同時1件ならピークは 100MB ＋ ASP.NET Core のベースライン（約100〜150MB）＝ 約250MB で、
    /// 384MB に対し十分な余裕がある。3並列では約300MB ＋ ベースラインでハードリミットに到達し、
    /// <b>正常系の入力で OOM → コンテナ再起動（全機能停止）</b>に至るため直列化する。
    /// 想定利用（日次数件〜10件）に対し同時1件で機能上の問題はない。
    /// スループットが不足する場合は、同時実行数を上げる前にコンテナのメモリ上限引上げ
    /// （EC2 インスタンスの空き容量確認が前提のオペレーター判断）を選択肢とすること。
    /// </para>
    /// <para>
    /// AI 呼出部分のみを制限し、DB 操作はセマフォの外で行う（DB まで直列化しない）。
    /// 待機は <see cref="AiCallQueueTimeout"/> で有界化し、待ち行列にバッファを抱えたまま
    /// 滞留するリクエスト数も抑制する。
    /// </para>
    /// </summary>
    private static readonly SemaphoreSlim AiCallSemaphore = new(1);

    /// <summary>
    /// AI 呼出の順番待ち（セマフォ待機）の上限（30秒）。
    /// 根拠: 待機を無制限にすると、画像バッファ（1件あたり約100MB）を保持したままのリクエストが
    /// 無制限に積み上がり、同時実行数を絞ったメモリ保護（<see cref="AiCallSemaphore"/>）が
    /// 待ち行列側から破られる。また processing の滞留時間が非有界になり、孤児判定
    /// （<see cref="ProcessingStaleAfter"/>）の根拠が成り立たなくなる。
    /// 超過時は AI を呼ばずに failed 記録＋failed Detail で応答し、rerun で回復できる。
    /// </summary>
    private static readonly TimeSpan AiCallQueueTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 許可する画像 Content-Type（jpeg / png のみ）。
    /// 比較は大文字小文字非依存で行う（RFC 9110 上 media type は case-insensitive で、
    /// クライアントが "IMAGE/JPEG" 等を送っても正当な入力のため）。
    /// </summary>
    private static readonly IReadOnlyList<string> AllowedContentTypes = new[]
    {
        "image/jpeg", "image/png",
    };

    /// <summary>PNG の Content-Type（マジックバイト分岐の判定に使う）。</summary>
    private const string PngContentType = "image/png";

    // ---- AI 実行スロット制御の内部シーム（統合テスト専用。InternalsVisibleTo=UndeuxSales.Tests） ----
    // 本番では両オーバーライドとも null で、上の定数どおり（順番待ち30秒 / 呼出120秒）に動作する。
    // 待機超過・呼出タイムアウトの各経路を実時間で待たずに検証するためだけに用意している。

    /// <summary>順番待ち上限のテスト用オーバーライド（null＝<see cref="AiCallQueueTimeout"/>）。</summary>
    internal static TimeSpan? AiCallQueueTimeoutOverride;

    /// <summary>AI 呼出タイムアウトのテスト用オーバーライド（null＝<see cref="AiCallTimeout"/>）。</summary>
    internal static TimeSpan? AiCallTimeoutOverride;

    /// <summary>実行スロットを即時取得できたか（テストから待ち行列状態を作るために使う）。</summary>
    internal static Task<bool> TryOccupyAiSlotAsync() => AiCallSemaphore.WaitAsync(TimeSpan.Zero);

    /// <summary><see cref="TryOccupyAiSlotAsync"/> で取得したスロットを解放する。</summary>
    internal static void ReleaseAiSlot() => AiCallSemaphore.Release();

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
    /// 「status=failed」または「status=processing かつ最後の実行開始（started_at）から
    /// <see cref="ProcessingStaleAfter"/> 超経過（プロセスクラッシュ等で結果が確定しない
    /// processing 孤児の回復）」。completed のチェックは記録保護（原則2）のため 400 を返す。
    /// <para>
    /// 実行権は DB の1回の UPDATE（<see cref="SubsidiaryCheckRepository.ClaimForRerunAsync"/>）で
    /// 原子的にクレームする。read-then-act では「stale processing の rerun 実行中も status・
    /// 基準時刻が不変」のため、別タブ・別ユーザーから何度でも起動でき同一 checkId への AI 呼出が
    /// 多重化するが、クレーム成立と同時に started_at が now() へ進むことで孤児条件から外れ、
    /// 重複起動が構造的に成立しない。
    /// </para>
    /// </summary>
    public async Task<SubsidiaryCheckDetail> RerunAsync(
        Guid checkId, CancellationToken cancellationToken = default)
    {
        // 未存在は 404 で早期に返す（クレーム 0 行と「存在しない」を区別するため）。
        var current = await GetDetailRequiredAsync(checkId, cancellationToken);

        if (!_aiClient.IsConfigured)
        {
            throw new AppException(ErrorCodes.AiNotConfigured, 503);
        }

        // 実行権の原子的クレーム。0 行なら「他が実行中」「completed で確定済み」のいずれか。
        var claimed = await _repository.ClaimForRerunAsync(
            checkId, DateTime.UtcNow - ProcessingStaleAfter, cancellationToken);
        if (claimed == 0)
        {
            throw await BuildRerunRejectedAsync(checkId, cancellationToken);
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

    /// <summary>
    /// クレームできなかった（0 行）ときの拒否理由を、現在状態を読み直して構築する。
    /// completed（確定済み）と processing（実行中）でメッセージを出し分ける。
    /// </summary>
    private async Task<AppException> BuildRerunRejectedAsync(
        Guid checkId, CancellationToken cancellationToken)
    {
        var latest = await GetDetailRequiredAsync(checkId, cancellationToken);
        var reason = latest.Summary.Status == SubsidiaryCheckStatus.Completed
            ? "このチェックは完了（completed）済みのため再実行できません（確定した判定結果は保護されます）。"
            : "このチェックは現在実行中のため再実行できません。"
              + $"完了しない場合は{(int)ProcessingStaleAfter.TotalMinutes}分経過後に再実行できます。";
        return new AppException(ErrorCodes.InvalidRequest, 400, reason);
    }

    /// <summary>詳細を取得する。未存在は 404（UNDX-DATA-005）。</summary>
    public async Task<SubsidiaryCheckDetail> GetDetailRequiredAsync(
        Guid checkId, CancellationToken cancellationToken = default) =>
        await _repository.GetDetailAsync(checkId, cancellationToken)
        ?? throw new AppException(ErrorCodes.SubsidiaryCheckNotFound, 404);

    /// <summary>
    /// 画像の枚数・形式・サイズ（各上限＋合計上限）を検証する
    /// （コントローラの事前チェックと二重でも安全な再検証）。
    /// <para>
    /// 検証順序は意図的に「不足枚数 → 超過枚数 → 各画像（形式→サイズ→中身）→ 合計サイズ」とする:
    /// 利用者が最初に直すべき事項（そもそも枚数が足りない／多すぎる）を優先して提示し、
    /// 個別画像の不備は「合計を減らす」より具体的な指示になるため合計サイズより先に返す。
    /// </para>
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
        // media type は RFC 9110 上 case-insensitive のため大文字小文字を区別せず比較する。
        if (!AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
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

        // 分岐も Content-Type と同じく大文字小文字非依存で判定する（"IMAGE/PNG" を JPEG 扱いにしない）。
        var isPng = string.Equals(image.ContentType, PngContentType, StringComparison.OrdinalIgnoreCase);
        var magicValid = isPng ? HasPngMagic(image.Data) : HasJpegMagic(image.Data);
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

            // AI 呼出のみを同時実行制限・タイムアウトで保護する（DB 操作はセマフォの外で行う）。
            // 待機は有界（AiCallQueueTimeout）。待機超過は AI を呼ばずに failed 記録で応答することで、
            // 画像バッファを保持したまま滞留するリクエスト数を抑える（メモリ保護・CRITICAL）。
            var queueTimeout = AiCallQueueTimeoutOverride ?? AiCallQueueTimeout;
            if (!await AiCallSemaphore.WaitAsync(queueTimeout, cancellationToken))
            {
                _logger.LogWarning(
                    "副資材チェックの AI 実行が順番待ちタイムアウトしました"
                    + "（checkId: {CheckId}、上限: {Timeout}秒）",
                    checkId, (int)queueTimeout.TotalSeconds);
                await RecordFailureAsync(checkId, BuildFailureMessage(
                    ErrorCodes.AiCallFailed,
                    "AI 実行の順番待ちがタイムアウトしました。時間をおいて再実行してください。"));
                return;
            }

            string? responseText = null;
            var callTimedOut = false;
            var callTimeout = AiCallTimeoutOverride ?? AiCallTimeout;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(callTimeout);
                try
                {
                    responseText = await _aiClient.AnalyzeImagesAsync(
                        aiImages, systemPrompt, userPrompt, CheckMaxOutputTokens, timeoutCts.Token);
                }
                catch (OperationCanceledException)
                    when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // 自前のタイムアウト発火（リクエスト側は未キャンセル）: 本物のキャンセル
                    // （クライアント切断）と区別し、throw せず failed 記録へ進む（記録はセマフォ解放後）。
                    // どちらのトークンも未キャンセルの OperationCanceledException（AI SDK 由来の
                    // 想定外キャンセル）はここで握らず、下の汎用 catch で failed 記録にする。
                    callTimedOut = true;
                }
            }
            finally
            {
                AiCallSemaphore.Release();
            }

            if (callTimedOut)
            {
                // タイムアウト: failed 記録＋failed Detail の返却で応答する（キャンセル扱いにしない）。
                _logger.LogWarning(
                    "副資材チェックの AI 呼出がタイムアウトしました（checkId: {CheckId}、上限: {Timeout}秒）",
                    checkId, (int)callTimeout.TotalSeconds);
                await RecordFailureAsync(checkId, BuildFailureMessage(
                    ErrorCodes.AiCallFailed, "AI 呼出がタイムアウトしました。再実行してください。"));
                return;
            }

            var parsed = SubsidiaryCheckResponseParser.Parse(responseText!);
            if (!parsed.Success)
            {
                _logger.LogWarning(
                    "副資材チェックの AI 応答を解析できませんでした（checkId: {CheckId}）: {Error}",
                    checkId, parsed.Error);
                await RecordFailureAsync(checkId, BuildFailureMessage(
                    ErrorCodes.AiResponseUnparseable, parsed.Error));
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // クライアント切断によるキャンセルでも processing 孤児を残さない: failed 記録後に再 throw する。
            // どちらのトークンも未キャンセルの OperationCanceledException は「キャンセル」ではないため
            // ここでは扱わず、下の汎用 catch で failed 記録にする（500 を返さない・原則4）。
            await RecordFailureAsync(checkId, BuildFailureMessage(
                ErrorCodes.AiCallFailed, "リクエストが中断されました（クライアント切断）。再実行してください。"));
            throw;
        }
        catch (Exception ex)
        {
            // AI 呼出失敗は主要フロー（記録）を止めない（原則4）。failed 記録で応答し、rerun で回復できる。
            // error_message には採番コード＋日本語の汎用文言のみを保存し、
            // 生の例外メッセージ（SDK 内部文言等）はログに留める（ユーザー露出防止）。
            _logger.LogWarning(ex, "副資材チェックの AI 実行に失敗しました（checkId: {CheckId}）", checkId);
            var message = ex is AppException app
                ? BuildFailureMessage(app.Error, app.Message)
                : BuildFailureMessage(ErrorCodes.Unexpected, "再実行してください。");
            await RecordFailureAsync(checkId, message);
        }
    }

    /// <summary>
    /// failed 記録用のエラーメッセージを全経路で同一形状
    /// 「<c>{コード}: {概要} {詳細}</c>」に統一して構築する。
    /// 詳細が空、または概要と同一（<see cref="AppException"/> の detail 未指定時は
    /// Message＝Summary になる）の場合は重複を避けてコード＋概要のみとする。
    /// </summary>
    private static string BuildFailureMessage(ErrorCodeInfo error, string? detail)
    {
        var head = $"{error.Code}: {error.Summary}";
        return string.IsNullOrWhiteSpace(detail) || detail.Trim() == error.Summary
            ? head
            : $"{head} {detail.Trim()}";
    }

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

    /// <summary>
    /// 保存・表示・AI プロンプトに使う商品ラベルを決定する。
    /// クライアント指定値を優先し、未指定なら商品マスタから導出する。
    /// クライアント指定値のみ長さ上限（<see cref="MaxProductLabelLength"/>）を課す
    /// （マスタ由来の値は運用管理下の内部データで、外部からの増幅経路ではないため）。
    /// </summary>
    public static string ResolveProductLabel(string? productLabel, SubsidiaryCheckProductInfo? product)
    {
        if (!string.IsNullOrWhiteSpace(productLabel))
        {
            var trimmed = productLabel.Trim();
            if (trimmed.Length > MaxProductLabelLength)
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"商品ラベルは {MaxProductLabelLength} 文字以内で指定してください"
                    + $"（指定: {trimmed.Length} 文字）。");
            }

            return trimmed;
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
