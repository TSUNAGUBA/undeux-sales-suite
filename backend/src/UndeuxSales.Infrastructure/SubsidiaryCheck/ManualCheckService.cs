using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UndeuxSales.Core;
using UndeuxSales.Core.Rag;
using UndeuxSales.Core.SubsidiaryCheck;
using UndeuxSales.Core.SubsidiaryCheck.ManualCheck;

namespace UndeuxSales.Infrastructure.SubsidiaryCheck;

/// <summary>
/// 手入力チェックのオーケストレーション（手入力値 × タグ画像の AI 抽出値の突合）。
/// <para>
/// 既存の副資材チェック（<see cref="SubsidiaryCheckService"/>）と同じ非同期実行構造
/// （受付枠予約 → 検証 → INSERT(processing) → 即時応答 →（バックグラウンド）AI 抽出 → 突合 →
/// UPDATE(completed/failed)）に揃える。<b>AI 呼出の同時実行スロット・受付枠・画像配信枠は
/// <see cref="SubsidiaryCheckService"/> と共有する</b>（<see cref="SubsidiaryCheckService.RunUnderAiCallSlotAsync"/>・
/// <see cref="SubsidiaryCheckService.ReserveAiCheckSlotOrThrow"/>・
/// <see cref="SubsidiaryCheckService.AcquireImageDownloadSlotAsync"/>）ことで、機能追加後もメモリ収支を保つ。
/// </para>
/// <para>
/// AI にはタグ画像と「読み取るべき項目」だけを渡し、手入力の期待値は渡さない（独立抽出。
/// <see cref="ManualCheckPromptBuilder"/> 参照）。突合はコード側（<see cref="ManualCheckComparer"/>）で行う。
/// AI 呼出・解析・タイムアウト・順番待ち超過・再実行準備の失敗はすべて failed 記録に落とす（原則4）。
/// </para>
/// </summary>
public sealed class ManualCheckService
{
    /// <summary>タグ画像の最小枚数。</summary>
    public const int MinTagImages = 1;

    /// <summary>
    /// タグ画像の最大枚数。手入力チェックは1枚の下げ札が基本だが、表裏・拡大の複数ショットを許容する。
    /// 合計サイズ上限（<see cref="SubsidiaryCheckService.MaxTotalImageBytes"/>）は既存と共有する。
    /// </summary>
    public const int MaxTagImages = 3;

    /// <summary>入力項目数の上限（AI プロンプト・保存サイズの増幅を防ぐ）。</summary>
    public const int MaxInputFields = 40;

    /// <summary>1項目あたりの入力値・組成成分の上限。</summary>
    public const int MaxValuesPerField = 30;

    /// <summary>入力値1件・材質名の最大文字数。</summary>
    public const int MaxValueLength = 500;

    /// <summary>項目ラベルの最大文字数。</summary>
    public const int MaxLabelLength = 100;

    /// <summary>
    /// 項目キーの最大文字数（TagPatternController.MaxKeyLength と同値）。
    /// key/label は AI プロンプト（<see cref="ManualCheckPromptBuilder.BuildUserPrompt"/>）へ渡るため、
    /// 直接送信経路（パターン非経由の input）でも長さを制限して増幅・注入を防ぐ。
    /// </summary>
    public const int MaxKeyLength = 60;

    private readonly ManualCheckRepository _repository;
    private readonly SubsidiaryCheckRepository _subsidiaryRepository;
    private readonly TagPatternRepository _patternRepository;
    private readonly SubsidiaryCheckService _slots;
    private readonly IAiChatClient _aiClient;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ManualCheckService> _logger;

    public ManualCheckService(
        ManualCheckRepository repository,
        SubsidiaryCheckRepository subsidiaryRepository,
        TagPatternRepository patternRepository,
        SubsidiaryCheckService slots,
        IAiChatClient aiClient,
        IServiceScopeFactory scopeFactory,
        ILogger<ManualCheckService> logger)
    {
        _repository = repository;
        _subsidiaryRepository = subsidiaryRepository;
        _patternRepository = patternRepository;
        _slots = slots;
        _aiClient = aiClient;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>AI 実行本体への入力（AI へ渡すタグ画像＋手入力スナップショット）。</summary>
    private sealed record ManualRunInput(IReadOnlyList<AiImageInput> Images, ManualCheckInput Manual);

    // ------------------------------------------------------------
    // 新規チェック
    // ------------------------------------------------------------

    /// <summary>
    /// 新規チェックを登録し、AI 抽出＋突合を<b>バックグラウンドで開始</b>して processing の詳細を即座に返す。
    /// 入力検証エラーは同期的に 4xx、AI 未設定は 503、受付上限超過は<b>画像バッファ確保前</b>に 429。
    /// </summary>
    /// <param name="reservation">
    /// コントローラが画像バッファ確保前に取得済みの受付枠。null の場合はここで予約する（テスト経路）。
    /// </param>
    public async Task<ManualCheckDetail> CreateAndStartAsync(
        Guid? productId,
        string? productLabel,
        ManualCheckInput input,
        IReadOnlyList<SubsidiaryCheckImageUpload> tagImages,
        string createdBy,
        AiCheckSlotLease? reservation = null,
        CancellationToken cancellationToken = default)
    {
        using var ownReservation = reservation is null ? _slots.ReserveAiCheckSlotOrThrow() : null;
        var lease = reservation ?? ownReservation!;

        ValidateTagImages(tagImages);
        ValidateInput(input);

        if (!_aiClient.IsConfigured)
        {
            throw new AppException(ErrorCodes.AiNotConfigured, 503);
        }

        SubsidiaryCheckProductInfo? product = null;
        if (productId is { } id)
        {
            product = await _subsidiaryRepository.GetProductInfoAsync(id, cancellationToken)
                      ?? throw new AppException(ErrorCodes.ProductNotFound, 404);
        }

        var label = SubsidiaryCheckService.ResolveProductLabel(productLabel, product);

        // pattern_id は現存するものだけ FK に残す（削除済みパターンでも登録を止めない・原則4）。
        // 実行時の項目定義は input スナップショットが SoT のため、リンク切れでも履歴表示は維持される。
        Guid? patternId = null;
        if (input.PatternId is { } pid && await _patternRepository.GetAsync(pid, cancellationToken) is not null)
        {
            patternId = pid;
        }

        var checkId = Guid.NewGuid();
        await _repository.InsertAsync(
            checkId, productId, label, patternId, input.PatternName ?? string.Empty, createdBy,
            ManualCheckRepository.SerializeInput(input),
            BuildImageRecords(tagImages), cancellationToken);

        ManualCheckDetail accepted;
        ManualRunInput runInput;
        try
        {
            accepted = await GetDetailRequiredAsync(checkId, cancellationToken);
            runInput = new ManualRunInput(ToAiImages(tagImages), input);
        }
        catch
        {
            await RecordFailureAsync(checkId, BuildFailureMessage(
                ErrorCodes.Unexpected, "チェックの登録後に受付処理へ失敗しました。再実行してください。"));
            throw;
        }

        StartBackgroundRun(checkId, runInput, lease);
        return accepted;
    }

    // ------------------------------------------------------------
    // 再実行（failed / 長時間 processing の孤児。手動回復パス）
    // ------------------------------------------------------------

    /// <summary>
    /// AI 抽出＋突合を再実行する。許可条件・原子的クレーム・記録保護は
    /// <see cref="SubsidiaryCheckService.RerunAsync"/> と同一方針。手入力スナップショット・画像は
    /// 保存済みの記録から読み直す（バックグラウンド側で準備し、失敗も failed 記録に落とす）。
    /// </summary>
    public async Task<ManualCheckDetail> RerunAsync(
        Guid checkId, string requestedBy, CancellationToken cancellationToken = default)
    {
        _ = await GetDetailRequiredAsync(checkId, cancellationToken);

        if (!_aiClient.IsConfigured)
        {
            throw new AppException(ErrorCodes.AiNotConfigured, 503);
        }

        using var lease = _slots.ReserveAiCheckSlotOrThrow();

        var claimed = await _repository.ClaimForRerunAsync(
            checkId, DateTime.UtcNow - SubsidiaryCheckService.ProcessingStaleAfter, cancellationToken);
        if (claimed == 0)
        {
            throw await BuildRerunRejectedAsync(checkId, cancellationToken);
        }

        ManualCheckDetail accepted;
        try
        {
            _logger.LogInformation(
                "手入力チェックを再実行します（checkId: {CheckId}、実行者: {User}）", checkId, requestedBy);
            accepted = await GetDetailRequiredAsync(checkId, cancellationToken);
        }
        catch
        {
            await RecordFailureAsync(checkId, BuildFailureMessage(
                ErrorCodes.Unexpected, "再実行の受付後に応答の生成へ失敗しました。再実行してください。"));
            throw;
        }

        StartBackgroundRun(checkId, prepared: null, lease);
        return accepted;
    }

    private async Task<AppException> BuildRerunRejectedAsync(Guid checkId, CancellationToken cancellationToken)
    {
        var latest = await GetDetailRequiredAsync(checkId, cancellationToken);
        var reason = latest.Summary.Status == SubsidiaryCheckStatus.Completed
            ? "このチェックは完了（completed）済みのため再実行できません（確定した判定結果は保護されます）。"
            : "このチェックは現在実行中のため再実行できません。"
              + $"完了しない場合は{(int)SubsidiaryCheckService.ProcessingStaleAfter.TotalMinutes}分経過後に再実行できます。";
        return new AppException(ErrorCodes.InvalidRequest, 400, reason);
    }

    // ------------------------------------------------------------
    // バックグラウンド実行
    // ------------------------------------------------------------

    private void StartBackgroundRun(Guid checkId, ManualRunInput? prepared, AiCheckSlotLease lease)
    {
        var scopeFactory = _scopeFactory;
        var logger = _logger;
        var owned = lease.HandOffToBackground();

        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<ManualCheckService>();
                    await service.RunBackgroundAsync(checkId, prepared);
                }
                catch (Exception ex)
                {
                    // RunBackgroundAsync は全経路を failed 記録に落とすため、ここへ来るのは
                    // スコープ生成・解決の失敗（プロセス停止時等）だけ。processing のまま残り、
                    // ProcessingStaleAfter 経過後の rerun（孤児回復パス）で回復する。
                    logger.LogError(ex,
                        "手入力チェックのバックグラウンド実行を開始できませんでした（checkId: {CheckId}）", checkId);
                }
                finally
                {
                    owned.Dispose();
                }
            });
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }

    private async Task RunBackgroundAsync(Guid checkId, ManualRunInput? prepared)
    {
        var input = prepared;
        if (input is null)
        {
            try
            {
                input = await LoadRunInputAsync(checkId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "手入力チェックの再実行準備に失敗しました（checkId: {CheckId}）", checkId);
                var message = ex is AppException app
                    ? BuildFailureMessage(app.Error, app.Message)
                    : BuildFailureMessage(ErrorCodes.Unexpected, "再実行してください。");
                await RecordFailureAsync(checkId, message);
                return;
            }
        }

        await RunAiAndCompareAsync(checkId, input);
    }

    /// <summary>保存済みレコードから AI 実行入力（画像＋手入力スナップショット）を読み出す（rerun 用）。</summary>
    private async Task<ManualRunInput> LoadRunInputAsync(Guid checkId)
    {
        var current = await GetDetailRequiredAsync(checkId);
        var stored = await _repository.GetImagesWithDataAsync(checkId);
        return new ManualRunInput(ToAiImages(stored), current.Input);
    }

    /// <summary>AI 抽出 → 突合 → 結果記録（バックグラウンドタスク上で動く。全失敗を failed 記録に落とす）。</summary>
    private async Task RunAiAndCompareAsync(Guid checkId, ManualRunInput input)
    {
        try
        {
            var systemPrompt = ManualCheckPromptBuilder.BuildSystemPrompt();
            var userPrompt = ManualCheckPromptBuilder.BuildUserPrompt(input.Manual.Fields);

            // AI 呼出スロットは副資材チェックと共有する（同時実行1件・順番待ち/呼出タイムアウト保護）。
            var slot = await _slots.RunUnderAiCallSlotAsync(token => _aiClient.AnalyzeImagesAsync(
                input.Images, systemPrompt, userPrompt, ManualCheckPromptBuilder.RecommendedMaxTokens, token));

            switch (slot.Outcome)
            {
                case AiCallSlotOutcome.QueueTimedOut:
                    _logger.LogWarning(
                        "手入力チェックの AI 実行が順番待ちタイムアウトしました（checkId: {CheckId}）", checkId);
                    await RecordFailureAsync(checkId, BuildFailureMessage(
                        ErrorCodes.AiCallFailed,
                        "AI 実行の順番待ちがタイムアウトしました。時間をおいて再実行してください。"));
                    return;
                case AiCallSlotOutcome.CallTimedOut:
                    _logger.LogWarning(
                        "手入力チェックの AI 呼出がタイムアウトしました（checkId: {CheckId}）", checkId);
                    await RecordFailureAsync(checkId, BuildFailureMessage(
                        ErrorCodes.AiCallFailed, "AI 呼出がタイムアウトしました。再実行してください。"));
                    return;
            }

            var parsed = ManualCheckResponseParser.Parse(slot.ResponseText!);
            if (!parsed.Success)
            {
                _logger.LogWarning(
                    "手入力チェックの AI 応答を解析できませんでした（checkId: {CheckId}）: {Error}",
                    checkId, parsed.Error);
                await RecordFailureAsync(checkId, BuildFailureMessage(
                    ErrorCodes.AiResponseUnparseable, parsed.Error));
                return;
            }

            var comparison = ManualCheckComparer.Compare(input.Manual, parsed.Extraction);

            var updated = await _repository.UpdateResultAsync(
                checkId, SubsidiaryCheckStatus.Completed, comparison.Judgment,
                comparison.FieldCount, comparison.MatchCount, comparison.MismatchCount, comparison.WarnCount,
                ManualCheckRepository.SerializeResults(comparison.Results),
                _aiClient.ChatModel, errorMessage: null);
            if (updated == 0)
            {
                _logger.LogWarning(
                    "手入力チェックは既に completed のため結果を破棄しました（checkId: {CheckId}）", checkId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "手入力チェックの AI 実行に失敗しました（checkId: {CheckId}）", checkId);
            var message = ex is AppException app
                ? BuildFailureMessage(app.Error, app.Message)
                : BuildFailureMessage(ErrorCodes.Unexpected, "再実行してください。");
            await RecordFailureAsync(checkId, message);
        }
    }

    // ------------------------------------------------------------
    // 取得・画像配信
    // ------------------------------------------------------------

    /// <summary>履歴一覧（作成日時降順）。</summary>
    public Task<ManualCheckPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default)
        => _repository.ListAsync(page, pageSize, cancellationToken);

    /// <summary>詳細を取得する。未存在は 404（UNDX-DATA-005）。</summary>
    public async Task<ManualCheckDetail> GetDetailRequiredAsync(
        Guid checkId, CancellationToken cancellationToken = default) =>
        await _repository.GetDetailAsync(checkId, cancellationToken)
        ?? throw new AppException(ErrorCodes.SubsidiaryCheckNotFound, 404);

    /// <summary>
    /// 受付枠を予約したスコープを返す（副資材チェックと共有＝機能横断の受付上限）。上限到達時は 429。
    /// <b>画像バッファ確保前に呼ぶこと</b>（メモリ有界化。既存 <see cref="SubsidiaryCheckService"/> と同じ）。
    /// </summary>
    public AiCheckSlotLease ReserveAiCheckSlotOrThrow() => _slots.ReserveAiCheckSlotOrThrow();

    /// <summary>
    /// 画像配信枠を取得する（副資材チェックと共有）。<c>using</c> で応答本文の書き出しまでを覆うこと。
    /// </summary>
    public Task<ImageDownloadSlotLease> AcquireImageDownloadSlotAsync(
        CancellationToken cancellationToken = default) =>
        _slots.AcquireImageDownloadSlotAsync(cancellationToken);

    /// <summary>タグ画像バイナリを取得する（必ず配信枠の内側で呼ぶこと）。未存在は 404。</summary>
    public async Task<SubsidiaryCheckImagePayload> GetImageRequiredAsync(
        Guid checkId, Guid imageId, CancellationToken cancellationToken = default)
        => await _repository.GetImageAsync(checkId, imageId, cancellationToken)
           ?? throw new AppException(ErrorCodes.SubsidiaryCheckNotFound, 404,
               "指定された画像が見つかりません。");

    // ------------------------------------------------------------
    // 検証
    // ------------------------------------------------------------

    /// <summary>タグ画像の枚数・形式・サイズ（各上限＋合計上限）を検証する（既存の画像検証を共用）。</summary>
    public static void ValidateTagImages(IReadOnlyList<SubsidiaryCheckImageUpload> tagImages)
    {
        if (tagImages.Count < MinTagImages)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageMissing, 400,
                $"タグ画像を {MinTagImages}〜{MaxTagImages} 枚アップロードしてください（指定: {tagImages.Count} 枚）。");
        }

        if (tagImages.Count > MaxTagImages)
        {
            throw new AppException(ErrorCodes.SubsidiaryImageTooMany, 400,
                $"タグ画像は {MaxTagImages} 枚以内にしてください（指定: {tagImages.Count} 枚）。");
        }

        foreach (var image in tagImages)
        {
            SubsidiaryCheckService.ValidateImage(image);
        }

        SubsidiaryCheckService.EnsureTotalSizeWithinLimit(tagImages.Sum(i => i.Data.LongLength));
    }

    /// <summary>
    /// 手入力の妥当性を検証する。突合対象が1件もない・必須項目の未入力・上限超過・
    /// 未知のアイコンコードは 400（UNDX-REQ-001）で拒否する。
    /// </summary>
    public static void ValidateInput(ManualCheckInput input)
    {
        if (input.Fields.Count == 0)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "入力項目がありません。");
        }

        if (input.Fields.Count > MaxInputFields)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                $"入力項目は {MaxInputFields} 個以内にしてください（指定: {input.Fields.Count} 個）。");
        }

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasAnyValue = false;
        foreach (var field in input.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key))
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400, "項目キーが空の入力があります。");
            }

            // key/label は AI プロンプトへ渡るため長さを制限する（増幅・注入防止）。
            if (field.Key.Trim().Length > MaxKeyLength)
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"項目キーは {MaxKeyLength} 文字以内にしてください（{field.Key}）。");
            }

            if (!seenKeys.Add(field.Key.Trim()))
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"項目キーが重複しています（{field.Key}）。");
            }

            if ((field.Label?.Length ?? 0) > MaxLabelLength)
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"項目ラベルは {MaxLabelLength} 文字以内にしてください（{field.Key}）。");
            }

            if (!ManualFieldCompareMode.IsKnown(field.Compare))
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"未知の突合方式です（{field.Key}: {field.Compare}）。");
            }

            // 必須項目の未入力チェックはフォーム側（タグパターンの required）で行う。
            // 入力スナップショット（ManualInputField）は required を持たず、サーバは構造的検証
            // （キー・長さ・上限・「最低1項目に値あり」）に専念する。
            ValidateFieldCardinalityAndLength(field);
            hasAnyValue |= FieldHasValue(field);
        }

        if (!hasAnyValue)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                "少なくとも1つの項目に値を入力してください（突合対象がありません）。");
        }
    }

    private static bool FieldHasValue(ManualInputField field) =>
        field.Compare == ManualFieldCompareMode.Ratio
            ? field.Ratios.Any(r => !string.IsNullOrWhiteSpace(r.Material) || r.Percent is not null)
            : field.Values.Any(v => !string.IsNullOrWhiteSpace(v));

    private static void ValidateFieldCardinalityAndLength(ManualInputField field)
    {
        if (field.Values.Count > MaxValuesPerField || field.Ratios.Count > MaxValuesPerField)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                $"項目「{field.Label}」の入力数が上限（{MaxValuesPerField}）を超えています。");
        }

        foreach (var value in field.Values)
        {
            if ((value?.Length ?? 0) > MaxValueLength)
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"項目「{field.Label}」の入力値が {MaxValueLength} 文字を超えています。");
            }

            // 洗濯表示は選択式のため、未知のアイコンコードは改ざん等とみなして拒否する。
            if (field.Compare == ManualFieldCompareMode.CareIcon
                && !string.IsNullOrWhiteSpace(value)
                && !CareIconCatalog.IsKnownCode(value))
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"未知の洗濯表示アイコンコードです（{field.Label}: {value}）。");
            }
        }

        foreach (var ratio in field.Ratios)
        {
            if ((ratio.Material?.Length ?? 0) > MaxValueLength)
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"項目「{field.Label}」の材質名が {MaxValueLength} 文字を超えています。");
            }
        }
    }

    // ------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------

    private static string BuildFailureMessage(ErrorCodeInfo error, string? detail)
    {
        var head = $"{error.Code}: {error.Summary}";
        return string.IsNullOrWhiteSpace(detail) || detail.Trim() == error.Summary
            ? head
            : $"{head} {detail.Trim()}";
    }

    private async Task RecordFailureAsync(Guid checkId, string errorMessage)
    {
        try
        {
            var updated = await _repository.UpdateResultAsync(
                checkId, SubsidiaryCheckStatus.Failed, judgment: null,
                fieldCount: 0, matchCount: 0, mismatchCount: 0, warnCount: 0,
                resultsJson: null, _aiClient.ChatModel, errorMessage, CancellationToken.None);
            if (updated == 0)
            {
                _logger.LogWarning(
                    "手入力チェックは既に completed のため失敗記録を破棄しました（checkId: {CheckId}）", checkId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "手入力チェックの失敗記録に失敗しました（checkId: {CheckId}）", checkId);
        }
    }

    private static IReadOnlyList<SubsidiaryCheckImagePayload> BuildImageRecords(
        IReadOnlyList<SubsidiaryCheckImageUpload> tagImages) =>
        tagImages.Select((image, index) => new SubsidiaryCheckImagePayload(
            SubsidiaryCheckImageKind.Tag, image.FileName, image.ContentType, image.Data, index)).ToList();

    private static IReadOnlyList<AiImageInput> ToAiImages(IReadOnlyList<SubsidiaryCheckImageUpload> tagImages)
        => BuildAiImages(tagImages.Select(i => (i.Data, i.ContentType)).ToList());

    private static IReadOnlyList<AiImageInput> ToAiImages(IReadOnlyList<SubsidiaryCheckImagePayload> tagImages)
        => BuildAiImages(tagImages.Select(i => (i.Data, i.ContentType)).ToList());

    private static IReadOnlyList<AiImageInput> BuildAiImages(
        IReadOnlyList<(byte[] Data, string ContentType)> tagImages)
        => tagImages.Select((image, index) => new AiImageInput(
            image.Data,
            // media type は入力検証が大文字小文字非依存で受理するため、AI へ渡す前に png/jpeg へ正規化する
            // （PNG を JPEG と宣言して恒久失敗するのを防ぐ。SubsidiaryCheckService と同じ方針）。
            image.ContentType.Trim().ToLowerInvariant(),
            ManualCheckPromptBuilder.BuildImageLabel(index + 1, tagImages.Count))).ToList();
}
