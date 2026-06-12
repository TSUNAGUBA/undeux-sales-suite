using Dapper;
using Npgsql;
using UndeuxSales.Core;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.Queries;

// =====================================================================================
// 在庫アクションフラグ（発注停止候補・値下げ候補）のリクエスト/レスポンスとリポジトリ。
// SoT は public スキーマの inventory_action_flag（ユーザーの判断記録。mart.rebuild() の
// 影響を受けない）。語彙の実装 SoT は Core の InventoryFlagRules。
// =====================================================================================

/// <summary>一括登録の対象 SKU（自然キー4組 + 付与時点の判定状態）。</summary>
/// <remarks>CurrentStatus は付与時の在庫健全性（healthy/caution/stagnant/dormant）。
/// 閾値変更後に「現在は判定対象外」を表示で検知するための記録として保存する。</remarks>
public sealed record InventoryFlagItemKey(
    string GyotaiCode,
    string ShohinKigou,
    string HinbanCode,
    string TanpinCode,
    string CurrentStatus);

/// <summary>POST /api/inventory-flags/bulk のリクエスト。</summary>
public sealed record InventoryFlagBulkRequest(
    string? FlagType,
    string? Note,
    IReadOnlyList<InventoryFlagItemKey>? Items);

/// <summary>一括登録の結果（新規件数と、既存フラグのためスキップした件数）。</summary>
public sealed record InventoryFlagBulkResult(int Created, int Skipped);

/// <summary>POST /api/inventory-flags/status のリクエスト（一括状態変更）。</summary>
public sealed record InventoryFlagStatusRequest(
    IReadOnlyList<long>? Ids,
    string? Status,
    string? Note);

/// <summary>POST /api/inventory-flags/delete のリクエスト（明示取消）。</summary>
public sealed record InventoryFlagDeleteRequest(IReadOnlyList<long>? Ids);

/// <summary>フラグ種別×対応状況の件数1行。</summary>
public sealed record InventoryFlagSummaryRow(string FlagType, string Status, int Count);

/// <summary>GET /api/inventory-flags/summary のレスポンス。</summary>
/// <remarks>OrphanCount は最新週スナップショットに存在しない SKU のフラグ件数
/// （完売・取扱終了などで明細に現れない＝対応完了の確認候補）。</remarks>
public sealed record InventoryFlagSummary(
    IReadOnlyList<InventoryFlagSummaryRow> Rows,
    int OrphanCount,
    DateOnly? LatestWeek);

/// <summary>在庫アクションフラグの読み書きリポジトリ。</summary>
public sealed class InventoryFlagRepository
{
    /// <summary>一括登録の1リクエスト上限（フロントのページサイズ上限 200 に余裕を持たせた値）。</summary>
    public const int MaxBulkItems = 500;

    private readonly IDbConnectionFactory _connectionFactory;

    public InventoryFlagRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        DapperConfiguration.Initialize();
    }

    /// <summary>
    /// フラグを一括登録する（冪等）。既に同一 SKU×種別のフラグが存在する行はスキップし、
    /// 既存フラグの対応状況を巻き戻さない（ON CONFLICT DO NOTHING。記録系データの保護）。
    /// flagged_week はサーバ側で最新スナップショット週を解決して付与する（クライアント申告にしない）。
    /// </summary>
    public async Task<InventoryFlagBulkResult> BulkUpsertAsync(
        InventoryFlagBulkRequest request, string auditUser, CancellationToken cancellationToken = default)
    {
        if (!InventoryFlagRules.IsValidFlagType(request.FlagType))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                $"flagType '{request.FlagType}' は不正です（{string.Join(" / ", InventoryFlagRules.AllFlagTypes)}）。");
        }
        if (request.Items is null || request.Items.Count == 0)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "items が指定されていません。");
        }
        if (request.Items.Count > MaxBulkItems)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                $"一括登録は {MaxBulkItems} 件までです（指定: {request.Items.Count} 件）。");
        }

        // ペイロード内の重複（同一 SKU の二重指定）はサーバ側で除去して冪等性を単純化する。
        var items = request.Items
            .DistinctBy(i => (i.GyotaiCode, i.ShohinKigou, i.HinbanCode, i.TanpinCode))
            .ToList();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.GyotaiCode) || string.IsNullOrWhiteSpace(item.ShohinKigou)
                || string.IsNullOrWhiteSpace(item.HinbanCode) || string.IsNullOrWhiteSpace(item.TanpinCode))
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    "items に自然キー（gyotaiCode/shohinKigou/hinbanCode/tanpinCode）が空の行があります。");
            }
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var latestWeek = await QueryLatestSnapshotWeekAsync(connection, cancellationToken);
        if (!latestWeek.HasValue)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                "分析 mart が未構築のためフラグを登録できません。全社サマリーから mart を再構築してください。");
        }

        var parameters = new DynamicParameters();
        parameters.Add("gyotai", items.Select(i => i.GyotaiCode).ToArray());
        parameters.Add("shohin", items.Select(i => i.ShohinKigou).ToArray());
        parameters.Add("hinban", items.Select(i => i.HinbanCode).ToArray());
        parameters.Add("tanpin", items.Select(i => i.TanpinCode).ToArray());
        // 未知の判定状態が送られても保存値が壊れないよう、ホワイトリスト外は 'healthy' ではなく
        // そのまま保存せず 'unknown' に正規化する（表示側は未知値を素通しするため安全）。
        parameters.Add("flaggedStatuses", items
            .Select(i => InventoryHealthRules.NormalizeStatuses(new[] { i.CurrentStatus })
                .FirstOrDefault() ?? "unknown")
            .ToArray());
        parameters.Add("flagType", request.FlagType);
        parameters.Add("note", string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim());
        parameters.Add("flaggedWeek", latestWeek.Value);
        parameters.Add("auditUser", auditUser);

        const string sql = """
            INSERT INTO inventory_action_flag
                (gyotai_code, shohin_kigou, hinban_code, tanpin_code, flag_type, note,
                 flagged_week, flagged_status, created_by, updated_by)
            SELECT u.g, u.s, u.h, u.t, @flagType, @note, @flaggedWeek, u.fs, @auditUser, @auditUser
            FROM unnest(@gyotai, @shohin, @hinban, @tanpin, @flaggedStatuses) AS u(g, s, h, t, fs)
            ON CONFLICT (gyotai_code, shohin_kigou, hinban_code, tanpin_code, flag_type)
            DO NOTHING;
            """;
        var created = await connection.ExecuteAsync(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));

        return new InventoryFlagBulkResult(created, items.Count - created);
    }

    /// <summary>
    /// 対応状況を一括変更する（1トランザクション・all-or-nothing）。
    /// 存在しない id が含まれる場合は 404（UNDX-DATA-003）とし、他の行も更新しない
    /// （他ユーザーの削除との競合を呼び出し側が確実に検知できるようにする）。
    /// note は指定時のみ上書きし、未指定（null）なら既存値を保持する。
    /// </summary>
    public async Task UpdateStatusAsync(
        InventoryFlagStatusRequest request, string auditUser, CancellationToken cancellationToken = default)
    {
        var ids = ValidateIds(request.Ids);
        if (!InventoryFlagRules.IsValidStatus(request.Status))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                $"status '{request.Status}' は不正です（{string.Join(" / ", InventoryFlagRules.AllStatuses)}）。");
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string sql = """
            UPDATE inventory_action_flag
            SET status = @status,
                note = COALESCE(@note, note),
                updated_by = @auditUser,
                updated_at = now()
            WHERE id = ANY(@ids);
            """;
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                ids,
                status = request.Status,
                note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
                auditUser,
            },
            transaction, cancellationToken: cancellationToken));

        if (updated != ids.Length)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new AppException(ErrorCodes.FlagNotFound, 404,
                $"指定 {ids.Length} 件のうち {ids.Length - updated} 件が見つかりません。");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// フラグを一括削除する（誤操作の訂正用。1トランザクション・all-or-nothing）。
    /// 対応の終了は削除ではなく status の done / dismissed を正とする（判断記録の保護）。
    /// </summary>
    public async Task DeleteAsync(
        InventoryFlagDeleteRequest request, CancellationToken cancellationToken = default)
    {
        var ids = ValidateIds(request.Ids);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM inventory_action_flag WHERE id = ANY(@ids);",
            new { ids }, transaction, cancellationToken: cancellationToken));

        if (deleted != ids.Length)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new AppException(ErrorCodes.FlagNotFound, 404,
                $"指定 {ids.Length} 件のうち {ids.Length - deleted} 件が見つかりません。");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>フラグ種別×対応状況の件数と、最新週スナップショットに存在しない孤児フラグ件数を返す。</summary>
    public async Task<InventoryFlagSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var rows = (await connection.QueryAsync<InventoryFlagSummaryRow>(new CommandDefinition(
            """
            SELECT flag_type AS flag_type, status AS status, COUNT(*)::int AS count
            FROM inventory_action_flag
            GROUP BY flag_type, status
            ORDER BY flag_type, status;
            """,
            cancellationToken: cancellationToken))).ToList();

        var latestWeek = await QueryLatestSnapshotWeekAsync(connection, cancellationToken);

        int orphanCount;
        if (!latestWeek.HasValue)
        {
            // mart 未構築時は全フラグが明細に現れないが、「孤児」と断定できないため 0 とする
            // （再構築すれば解消する一時状態。summary の利用側は latestWeek=null で判別できる）。
            orphanCount = 0;
        }
        else
        {
            orphanCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                SELECT COUNT(*)::int
                FROM inventory_action_flag f
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM mart.fact_inventory_snapshot inv
                    JOIN mart.dim_date    dd ON dd.date_key    = inv.date_key
                    JOIN mart.dim_product dp ON dp.product_key = inv.product_key
                    JOIN mart.dim_sku     ds ON ds.sku_key     = inv.sku_key
                    WHERE dd.week_monday = @latestWeek
                      AND dp.channel_code = f.gyotai_code
                      AND dp.product_sign = f.shohin_kigou
                      AND dp.product_code = f.hinban_code
                      AND ds.unit_code    = f.tanpin_code);
                """,
                new { latestWeek = latestWeek.Value }, cancellationToken: cancellationToken));
        }

        return new InventoryFlagSummary(rows, orphanCount, latestWeek);
    }

    /// <summary>id 配列の共通検証（null/空/非正値/重複を排除して返す）。</summary>
    private static long[] ValidateIds(IReadOnlyList<long>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "ids が指定されていません。");
        }
        if (ids.Any(id => id <= 0))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "ids に不正な値が含まれています。");
        }
        return ids.Distinct().ToArray();
    }

    /// <summary>在庫スナップショットの最新週（無フィルタ）。mart 未構築なら null。</summary>
    private static async Task<DateOnly?> QueryLatestSnapshotWeekAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
        => await connection.ExecuteScalarAsync<DateOnly?>(new CommandDefinition(
            """
            SELECT MAX(dd.week_monday)
            FROM mart.fact_inventory_snapshot inv
            JOIN mart.dim_date dd ON dd.date_key = inv.date_key;
            """,
            cancellationToken: cancellationToken));
}
