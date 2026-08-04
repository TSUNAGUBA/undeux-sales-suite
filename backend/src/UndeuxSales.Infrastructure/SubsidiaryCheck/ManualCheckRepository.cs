using System.Text.Json;
using Dapper;
using UndeuxSales.Core.SubsidiaryCheck;
using UndeuxSales.Core.SubsidiaryCheck.ManualCheck;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.SubsidiaryCheck;

/// <summary>
/// 手入力チェック（subsidiary_manual_check / subsidiary_manual_check_image）のデータアクセス。
/// <para>
/// SoT は subsidiary_manual_check（記録系）。input（手入力スナップショット）・results（項目単位の突合結果）は
/// API ワイヤ形式と同一の JSON（camelCase）を jsonb で保持する（非正規化の根拠は schema.sql 参照）。
/// 商品情報の取得は <see cref="SubsidiaryCheckRepository.QueryProductInfoAsync"/> を共用する（原則3）。
/// </para>
/// </summary>
public sealed class ManualCheckRepository
{
    // ページング既定値の SoT はコントローラ。リポジトリは防御的クランプのみ。
    private const int MaxPageSize = 100;

    /// <summary>input / results の jsonb 直列化設定（API 応答（camelCase）と同一のワイヤ形式で保存する）。</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>input / results の jsonb 復元設定（大文字小文字非依存）。</summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string SummaryColumns = """
        c.check_id,
        c.product_id,
        c.product_label,
        c.pattern_id,
        c.pattern_name,
        c.status,
        c.judgment,
        c.field_count,
        c.match_count,
        c.mismatch_count,
        c.warn_count,
        COALESCE(img.image_count, 0)::int AS image_count,
        c.ai_model,
        c.error_message,
        c.created_by,
        c.created_at,
        c.checked_at,
        c.started_at
        """;

    // 対象 check の画像枚数のみを ix_subsidiary_manual_check_image_check 経由で集計する
    // （全行 GROUP BY の派生テーブルを避ける。SubsidiaryCheckRepository と同じ流儀）。
    private const string ImageCountJoin = """
        LEFT JOIN LATERAL (
            SELECT COUNT(*) AS image_count
            FROM subsidiary_manual_check_image i
            WHERE i.check_id = c.check_id
        ) img ON TRUE
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public ManualCheckRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        DapperConfiguration.Initialize();
    }

    /// <summary>入力スナップショットを保存用 JSON（camelCase）へ直列化する。</summary>
    public static string SerializeInput(ManualCheckInput input) =>
        JsonSerializer.Serialize(input, WriteOptions);

    /// <summary>突合結果を保存用 JSON（camelCase）へ直列化する。</summary>
    public static string SerializeResults(IReadOnlyList<ManualFieldResult> results) =>
        JsonSerializer.Serialize(results, WriteOptions);

    /// <summary>チェック履歴の一覧（作成日時降順）をページングで取得する。</summary>
    public async Task<ManualCheckPage> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        parameters.Add("limit", pageSize);
        parameters.Add("offset", (long)(page - 1) * pageSize);

        var rows = (await connection.QueryAsync<SummaryRow>(new CommandDefinition($"""
            SELECT {SummaryColumns},
                   (COUNT(*) OVER ())::int AS total_count,
                   NULL::text AS input_json,
                   NULL::text AS results_json
            FROM subsidiary_manual_check c
            {ImageCountJoin}
            ORDER BY c.created_at DESC, c.check_id
            LIMIT @limit OFFSET @offset;
            """, parameters, cancellationToken: cancellationToken))).ToList();

        var totalCount = rows.Count > 0
            ? rows[0].TotalCount
            : await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*)::int FROM subsidiary_manual_check;", cancellationToken: cancellationToken));

        return new ManualCheckPage(rows.Select(ToSummary).ToList(), totalCount, page, pageSize);
    }

    /// <summary>チェック詳細（サマリ＋入力＋突合結果＋画像メタ＋商品情報）を取得する。未存在は null。</summary>
    public async Task<ManualCheckDetail?> GetDetailAsync(
        Guid checkId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<SummaryRow>(new CommandDefinition($"""
            SELECT {SummaryColumns},
                   0::int AS total_count,
                   c.input::text   AS input_json,
                   c.results::text AS results_json
            FROM subsidiary_manual_check c
            {ImageCountJoin}
            WHERE c.check_id = @checkId;
            """, new { checkId }, cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        var images = (await connection.QueryAsync<SubsidiaryCheckImageInfo>(new CommandDefinition("""
            SELECT image_id, 'tag'::text AS kind, file_name, content_type, size_bytes, sort_order
            FROM subsidiary_manual_check_image
            WHERE check_id = @checkId
            ORDER BY sort_order, image_id;
            """, new { checkId }, cancellationToken: cancellationToken))).ToList();

        var product = row.ProductId is { } productId
            ? await SubsidiaryCheckRepository.QueryProductInfoAsync(connection, productId, cancellationToken)
            : null;

        return new ManualCheckDetail(
            ToSummary(row),
            DeserializeInput(row.InputJson),
            DeserializeResults(row.ResultsJson),
            images,
            product);
    }

    /// <summary>タグ画像1枚のバイナリを取得する（ダウンロード用）。未存在は null。</summary>
    public async Task<SubsidiaryCheckImagePayload?> GetImageAsync(
        Guid checkId, Guid imageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<SubsidiaryCheckImagePayload>(new CommandDefinition("""
            SELECT 'tag'::text AS kind, file_name, content_type, data, sort_order
            FROM subsidiary_manual_check_image
            WHERE check_id = @checkId AND image_id = @imageId;
            """, new { checkId, imageId }, cancellationToken: cancellationToken));
    }

    /// <summary>チェックの全タグ画像バイナリを取得する（AI 再実行用。表示順）。</summary>
    public async Task<IReadOnlyList<SubsidiaryCheckImagePayload>> GetImagesWithDataAsync(
        Guid checkId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SubsidiaryCheckImagePayload>(new CommandDefinition("""
            SELECT 'tag'::text AS kind, file_name, content_type, data, sort_order
            FROM subsidiary_manual_check_image
            WHERE check_id = @checkId
            ORDER BY sort_order, image_id;
            """, new { checkId }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <summary>
    /// チェック本体（status=processing）とタグ画像を1トランザクションで登録する。uuid はアプリ側生成。
    /// </summary>
    public async Task InsertAsync(
        Guid checkId,
        Guid? productId,
        string productLabel,
        Guid? patternId,
        string patternName,
        string createdBy,
        string inputJson,
        IReadOnlyList<SubsidiaryCheckImagePayload> images,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO subsidiary_manual_check
                (check_id, product_id, product_label, pattern_id, pattern_name, status, input, created_by, started_at)
            VALUES
                (@checkId, @productId, @productLabel, @patternId, @patternName, @status, @inputJson::jsonb, @createdBy, now());
            """,
            new
            {
                checkId, productId, productLabel, patternId, patternName,
                status = SubsidiaryCheckStatus.Processing, inputJson, createdBy,
            },
            transaction, cancellationToken: cancellationToken));

        foreach (var image in images)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO subsidiary_manual_check_image
                    (image_id, check_id, file_name, content_type, size_bytes, data, sort_order)
                VALUES
                    (@imageId, @checkId, @fileName, @contentType, @sizeBytes, @data, @sortOrder);
                """,
                new
                {
                    imageId = Guid.NewGuid(),
                    checkId,
                    fileName = image.FileName,
                    contentType = image.ContentType,
                    sizeBytes = image.Data.LongLength,
                    data = image.Data,
                    sortOrder = image.SortOrder,
                },
                transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// AI 実行結果（completed / failed）を反映する。入力・画像・作成情報（記録系）は変更しない。
    /// 状態遷移ガード: 既に completed のチェックは更新しない（並行 rerun 等での巻き戻り防止・原則2）。
    /// </summary>
    /// <returns>更新された行数（0 = completed 保護により破棄）。</returns>
    public async Task<int> UpdateResultAsync(
        Guid checkId,
        string status,
        string? judgment,
        int fieldCount,
        int matchCount,
        int mismatchCount,
        int warnCount,
        string? resultsJson,
        string? aiModel,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE subsidiary_manual_check
            SET status         = @status,
                judgment       = @judgment,
                field_count    = @fieldCount,
                match_count    = @matchCount,
                mismatch_count = @mismatchCount,
                warn_count     = @warnCount,
                results        = @resultsJson::jsonb,
                ai_model       = @aiModel,
                error_message  = @errorMessage,
                checked_at     = now()
            WHERE check_id = @checkId
              AND status <> @completedStatus;
            """,
            new
            {
                checkId, status, judgment, fieldCount, matchCount, mismatchCount, warnCount,
                resultsJson, aiModel, errorMessage,
                completedStatus = SubsidiaryCheckStatus.Completed,
            },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// 再実行の対象を1回の UPDATE で原子的にクレームする（failed、または滞留超過の processing 孤児）。
    /// SubsidiaryCheckRepository.ClaimForRerunAsync と同一方針（compare-and-set。completed は保護）。
    /// </summary>
    /// <returns>クレームできた行数（1 = 実行権を獲得 / 0 = 実行不可）。</returns>
    public async Task<int> ClaimForRerunAsync(
        Guid checkId, DateTime staleBefore, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE subsidiary_manual_check
               SET status        = @processingStatus,
                   started_at    = now(),
                   error_message = NULL
             WHERE check_id = @checkId
               AND (status = @failedStatus
                    OR (status = @processingStatus
                        AND COALESCE(started_at, created_at) < @staleBefore));
            """,
            new
            {
                checkId,
                staleBefore,
                processingStatus = SubsidiaryCheckStatus.Processing,
                failedStatus = SubsidiaryCheckStatus.Failed,
            },
            cancellationToken: cancellationToken));
    }

    private static ManualCheckSummary ToSummary(SummaryRow row) => new(
        row.CheckId,
        row.ProductId,
        row.ProductLabel,
        row.PatternId,
        row.PatternName,
        row.Status,
        row.Judgment,
        row.FieldCount,
        row.MatchCount,
        row.MismatchCount,
        row.WarnCount,
        row.ImageCount,
        row.AiModel,
        row.ErrorMessage,
        row.CreatedBy,
        row.CreatedAt,
        row.CheckedAt,
        row.StartedAt);

    private static ManualCheckInput DeserializeInput(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return new ManualCheckInput(null, string.Empty, Array.Empty<ManualInputField>());
        }

        return JsonSerializer.Deserialize<ManualCheckInput>(inputJson, ReadOptions)
               ?? new ManualCheckInput(null, string.Empty, Array.Empty<ManualInputField>());
    }

    private static IReadOnlyList<ManualFieldResult> DeserializeResults(string? resultsJson)
    {
        if (string.IsNullOrWhiteSpace(resultsJson))
        {
            return Array.Empty<ManualFieldResult>();
        }

        return JsonSerializer.Deserialize<List<ManualFieldResult>>(resultsJson, ReadOptions)
               ?? (IReadOnlyList<ManualFieldResult>)Array.Empty<ManualFieldResult>();
    }

    /// <param name="InputJson">詳細クエリのみ実値（一覧クエリは NULL 固定。転送量を抑える）。</param>
    /// <param name="ResultsJson">同上。</param>
    private sealed record SummaryRow(
        Guid CheckId,
        Guid? ProductId,
        string ProductLabel,
        Guid? PatternId,
        string PatternName,
        string Status,
        string? Judgment,
        int FieldCount,
        int MatchCount,
        int MismatchCount,
        int WarnCount,
        int ImageCount,
        string? AiModel,
        string? ErrorMessage,
        string CreatedBy,
        DateTime CreatedAt,
        DateTime? CheckedAt,
        DateTime? StartedAt,
        int TotalCount,
        string? InputJson,
        string? ResultsJson);
}
