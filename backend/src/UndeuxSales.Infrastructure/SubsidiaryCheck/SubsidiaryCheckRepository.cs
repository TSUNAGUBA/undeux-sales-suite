using System.Text.Json;
using Dapper;
using UndeuxSales.Core.SubsidiaryCheck;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.SubsidiaryCheck;

/// <summary>
/// チェック画像1枚のバイナリ。INSERT 用の永続化入力と、ダウンロード・AI 再実行用の
/// 読出し結果の両方で共用する（書込・読出でスキーマ形状が同一のため重複 record を作らない。原則3）。
/// </summary>
public sealed record SubsidiaryCheckImagePayload(
    string Kind,
    string FileName,
    string ContentType,
    byte[] Data,
    int SortOrder);

/// <summary>
/// 副資材チェック（subsidiary_check / subsidiary_check_image / m_product_attachment）のデータアクセス。
/// <para>
/// SoT は subsidiary_check（記録系データ）。findings は API ワイヤ形式と同一の JSON（camelCase）を
/// jsonb で保持する（非正規化の根拠は schema.sql のコメント参照）。
/// </para>
/// </summary>
public sealed class SubsidiaryCheckRepository
{
    // ページングの既定値の SoT は SubsidiaryCheckController（DefaultPage / DefaultPageSize）。
    // リポジトリは受け取った値の防御的クランプのみを行う。
    private const int MaxPageSize = 100;

    /// <summary>findings の jsonb 直列化設定（API 応答（camelCase）と同一のワイヤ形式で保存する）。</summary>
    private static readonly JsonSerializerOptions FindingsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>findings の jsonb 復元設定（大文字小文字非依存）。</summary>
    private static readonly JsonSerializerOptions FindingsReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string SummaryColumns = """
        c.check_id,
        c.product_id,
        c.product_label,
        c.status,
        c.judgment,
        c.fail_count,
        c.warn_count,
        COALESCE(jsonb_array_length(c.findings), 0)::int AS finding_count,
        COALESCE(img.instruction_count, 0)::int AS instruction_image_count,
        COALESCE(img.tag_count, 0)::int AS tag_image_count,
        c.ai_model,
        c.error_message,
        c.created_by,
        c.created_at,
        c.checked_at,
        c.started_at
        """;

    // LATERAL サブクエリで「対象行の check_id のみ」を ix_subsidiary_check_image_check 経由で集計する
    // （全行 GROUP BY の派生テーブルだと、1件を読むだけの詳細取得でも全画像行を走査することになるため）。
    // 改善の主眼は詳細取得（1行）と一覧の画像枚数集計。一覧全体は総件数の COUNT(*) OVER () が
    // LIMIT より下で評価されるため依然 O(N) であり、LATERAL でそこが O(1) になるわけではない。
    private const string ImageCountJoin = """
        LEFT JOIN LATERAL (
            SELECT COUNT(*) FILTER (WHERE i.kind = 'instruction') AS instruction_count,
                   COUNT(*) FILTER (WHERE i.kind = 'tag')         AS tag_count
            FROM subsidiary_check_image i
            WHERE i.check_id = c.check_id
        ) img ON TRUE
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public SubsidiaryCheckRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        DapperConfiguration.Initialize();
    }

    /// <summary>指摘配列を保存用 JSON（camelCase）へ直列化する。</summary>
    public static string SerializeFindings(IReadOnlyList<SubsidiaryCheckFinding> findings) =>
        JsonSerializer.Serialize(findings, FindingsJsonOptions);

    /// <summary>チェック履歴の一覧（作成日時降順）をページングで取得する。</summary>
    public async Task<SubsidiaryCheckPage> ListAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var parameters = new DynamicParameters();
        parameters.Add("limit", pageSize);
        // page は外部入力のため long で乗算し、巨大値でも負の OFFSET（int オーバーフロー）にしない。
        parameters.Add("offset", (long)(page - 1) * pageSize);

        var rows = (await connection.QueryAsync<SummaryRow>(new CommandDefinition($"""
            SELECT {SummaryColumns},
                   (COUNT(*) OVER ())::int AS total_count,
                   NULL::text AS findings_json
            FROM subsidiary_check c
            {ImageCountJoin}
            ORDER BY c.created_at DESC, c.check_id
            LIMIT @limit OFFSET @offset;
            """, parameters, cancellationToken: cancellationToken))).ToList();

        // OFFSET overshoot で本体クエリが空集合の場合でも、実件数を別クエリで取得する
        // （window関数 COUNT(*) OVER () は LIMIT が空だと値を返さないため）。
        var totalCount = rows.Count > 0
            ? rows[0].TotalCount
            : await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*)::int FROM subsidiary_check;", cancellationToken: cancellationToken));

        return new SubsidiaryCheckPage(rows.Select(ToSummary).ToList(), totalCount, page, pageSize);
    }

    /// <summary>
    /// チェック詳細（サマリ＋指摘＋画像メタ＋商品情報）を取得する。未存在は null。
    /// </summary>
    public async Task<SubsidiaryCheckDetail?> GetDetailAsync(
        Guid checkId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        // サマリ行と findings（jsonb）を1クエリで取得する（サマリ→findings の2往復を避ける）。
        var row = await connection.QuerySingleOrDefaultAsync<SummaryRow>(new CommandDefinition($"""
            SELECT {SummaryColumns},
                   0::int AS total_count,
                   c.findings::text AS findings_json
            FROM subsidiary_check c
            {ImageCountJoin}
            WHERE c.check_id = @checkId;
            """, new { checkId }, cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        var images = (await connection.QueryAsync<SubsidiaryCheckImageInfo>(new CommandDefinition("""
            SELECT image_id, kind, file_name, content_type, size_bytes, sort_order
            FROM subsidiary_check_image
            WHERE check_id = @checkId
            ORDER BY kind, sort_order, image_id;
            """, new { checkId }, cancellationToken: cancellationToken))).ToList();

        var product = row.ProductId is { } productId
            ? await QueryProductInfoAsync(connection, productId, cancellationToken)
            : null;

        return new SubsidiaryCheckDetail(
            ToSummary(row), DeserializeFindings(row.FindingsJson), images, product);
    }

    /// <summary>チェック画像1枚のバイナリを取得する（ダウンロード用）。未存在は null。</summary>
    public async Task<SubsidiaryCheckImagePayload?> GetImageAsync(
        Guid checkId, Guid imageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<SubsidiaryCheckImagePayload>(new CommandDefinition("""
            SELECT kind, file_name, content_type, data, sort_order
            FROM subsidiary_check_image
            WHERE check_id = @checkId AND image_id = @imageId;
            """, new { checkId, imageId }, cancellationToken: cancellationToken));
    }

    /// <summary>チェックの全画像バイナリを取得する（AI 再実行用。種別→表示順の順）。</summary>
    public async Task<IReadOnlyList<SubsidiaryCheckImagePayload>> GetImagesWithDataAsync(
        Guid checkId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SubsidiaryCheckImagePayload>(new CommandDefinition("""
            SELECT kind, file_name, content_type, data, sort_order
            FROM subsidiary_check_image
            WHERE check_id = @checkId
            ORDER BY kind, sort_order, image_id;
            """, new { checkId }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <summary>
    /// チェック本体（status=processing）と画像を1トランザクションで登録する。
    /// uuid はアプリ側生成（m_product と同じ流儀）。
    /// </summary>
    public async Task InsertAsync(
        Guid checkId,
        Guid? productId,
        string productLabel,
        string createdBy,
        IReadOnlyList<SubsidiaryCheckImagePayload> images,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // started_at は「AI 実行の開始日時」。新規登録は直後に AI を呼ぶため now() で確定させる
        // （孤児判定・rerun クレームの基準時刻になる）。
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO subsidiary_check
                (check_id, product_id, product_label, status, created_by, started_at)
            VALUES (@checkId, @productId, @productLabel, @status, @createdBy, now());
            """,
            new { checkId, productId, productLabel, status = SubsidiaryCheckStatus.Processing, createdBy },
            transaction, cancellationToken: cancellationToken));

        foreach (var image in images)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO subsidiary_check_image
                    (image_id, check_id, kind, file_name, content_type, size_bytes, data, sort_order)
                VALUES
                    (@imageId, @checkId, @kind, @fileName, @contentType, @sizeBytes, @data, @sortOrder);
                """,
                new
                {
                    imageId = Guid.NewGuid(),
                    checkId,
                    kind = image.Kind,
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
    /// AI 実行結果（completed / failed）を反映する。画像・作成情報（記録系）は変更しない。
    /// <para>
    /// 状態遷移ガード: 既に completed のチェックは更新しない（WHERE 句で除外）。
    /// 並行 rerun 等による「確定済み判定結果の巻き戻り」を DB 層で防ぐ（記録保護・原則2）。
    /// </para>
    /// </summary>
    /// <param name="findingsJson"><see cref="SerializeFindings"/> で直列化した JSON（失敗時は null）。</param>
    /// <returns>更新された行数（0 = completed 保護により破棄。呼出側で警告ログを出す）。</returns>
    public async Task<int> UpdateResultAsync(
        Guid checkId,
        string status,
        string? judgment,
        int failCount,
        int warnCount,
        string? findingsJson,
        string? aiModel,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE subsidiary_check
            SET status        = @status,
                judgment      = @judgment,
                fail_count    = @failCount,
                warn_count    = @warnCount,
                findings      = @findingsJson::jsonb,
                ai_model      = @aiModel,
                error_message = @errorMessage,
                checked_at    = now()
            WHERE check_id = @checkId
              AND status <> @completedStatus;
            """,
            new
            {
                checkId, status, judgment, failCount, warnCount, findingsJson, aiModel, errorMessage,
                completedStatus = SubsidiaryCheckStatus.Completed,
            },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// 再実行（rerun）の対象を1回の UPDATE で原子的にクレーム（占有）する。
    /// <para>
    /// 許可条件（failed、または滞留超過の processing 孤児）を WHERE 句に含めた
    /// compare-and-set であり、read-then-act の TOCTOU を排除する。クレーム成立と同時に
    /// started_at を now() へ進めるため、実行中のチェックは孤児条件（started_at &lt; staleBefore）から
    /// 外れ、別タブ・別ユーザーからの重複起動が構造的に成立しない。
    /// completed は条件に含まれないため確定済みの判定結果は保護される（原則2）。
    /// </para>
    /// <para>
    /// started_at が NULL の行（バックフィル未適用の旧データ）は created_at で代替評価し、
    /// 孤児が永久に回復不能にならないようにする。
    /// </para>
    /// </summary>
    /// <param name="staleBefore">この時刻より前に開始した processing を孤児とみなす（UTC）。</param>
    /// <returns>クレームできた行数（1 = 実行権を獲得 / 0 = 実行不可）。</returns>
    public async Task<int> ClaimForRerunAsync(
        Guid checkId, DateTime staleBefore, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE subsidiary_check
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

    /// <summary>商品情報＋付属情報を取得する（チェック対象商品の突き合わせ用）。未存在は null。</summary>
    public async Task<SubsidiaryCheckProductInfo?> GetProductInfoAsync(
        Guid productId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await QueryProductInfoAsync(connection, productId, cancellationToken);
    }

    /// <summary>
    /// 商品情報＋付属情報を1クエリで取得する（既存接続を使う）。手入力チェック
    /// （<see cref="ManualCheckRepository"/>）の詳細組立でも共用するため internal 公開する（原則3）。
    /// </summary>
    internal static async Task<SubsidiaryCheckProductInfo?> QueryProductInfoAsync(
        Npgsql.NpgsqlConnection connection, Guid productId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ProductInfoRow>(new CommandDefinition("""
            SELECT mp.product_id,
                   mp.product_name,
                   mp.product_sign,
                   mp.product_type_crd,
                   mp.brand,
                   a.composition,
                   a.origin_country,
                   a.care_labels,
                   a.color_fastness_note,
                   a.display_order,
                   a.quality_notes,
                   a.updated_at AS attachment_updated_at
            FROM m_product mp
            LEFT JOIN m_product_attachment a ON a.product_id = mp.product_id
            WHERE mp.product_id = @productId;
            """, new { productId }, cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }

        var attachment = row.AttachmentUpdatedAt is { } updatedAt
            ? new MasterProductAttachment(
                row.Composition, row.OriginCountry, row.CareLabels,
                row.ColorFastnessNote, row.DisplayOrder, row.QualityNotes, updatedAt)
            : null;

        return new SubsidiaryCheckProductInfo(
            row.ProductId, row.ProductName, row.ProductSign, row.ProductTypeCrd, row.Brand, attachment);
    }

    private static SubsidiaryCheckSummary ToSummary(SummaryRow row) => new(
        row.CheckId,
        row.ProductId,
        row.ProductLabel,
        row.Status,
        row.Judgment,
        row.FailCount,
        row.WarnCount,
        row.FindingCount,
        row.InstructionImageCount,
        row.TagImageCount,
        row.AiModel,
        row.ErrorMessage,
        row.CreatedBy,
        row.CreatedAt,
        row.CheckedAt,
        row.StartedAt);

    private static IReadOnlyList<SubsidiaryCheckFinding> DeserializeFindings(string? findingsJson)
    {
        if (string.IsNullOrWhiteSpace(findingsJson))
        {
            return Array.Empty<SubsidiaryCheckFinding>();
        }

        return JsonSerializer.Deserialize<List<SubsidiaryCheckFinding>>(findingsJson, FindingsReadOptions)
               ?? (IReadOnlyList<SubsidiaryCheckFinding>)Array.Empty<SubsidiaryCheckFinding>();
    }

    /// <param name="FindingsJson">詳細クエリのみ実値（一覧クエリは NULL 固定。転送量を抑える）。</param>
    private sealed record SummaryRow(
        Guid CheckId,
        Guid? ProductId,
        string ProductLabel,
        string Status,
        string? Judgment,
        int FailCount,
        int WarnCount,
        int FindingCount,
        int InstructionImageCount,
        int TagImageCount,
        string? AiModel,
        string? ErrorMessage,
        string CreatedBy,
        DateTime CreatedAt,
        DateTime? CheckedAt,
        DateTime? StartedAt,
        int TotalCount,
        string? FindingsJson);

    private sealed record ProductInfoRow(
        Guid ProductId,
        string ProductName,
        string ProductSign,
        string ProductTypeCrd,
        string? Brand,
        string? Composition,
        string? OriginCountry,
        string? CareLabels,
        string? ColorFastnessNote,
        string? DisplayOrder,
        string? QualityNotes,
        DateTime? AttachmentUpdatedAt);
}
