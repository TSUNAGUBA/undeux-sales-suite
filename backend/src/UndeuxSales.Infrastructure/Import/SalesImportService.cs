using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using UndeuxSales.Core;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.Import;

/// <summary>
/// 売上参照レコードをファクトテーブルへ取り込む共通サービス。
/// 初期DBダンプ取込（DataLoader）と週次CSV取込（API）の双方から利用される。
/// <para>
/// 取込は冪等。業務キーが既存ならば測定値・属性値を更新する（再取込はSoTである
/// 取込ファイルによる訂正とみなす）。取込バッチ履歴は追記専用で保持される。
/// </para>
/// </summary>
public sealed class SalesImportService
{
    private const int MaxErrorMessageLength = 2000;

    // 初期DBダンプ（約160万行）の UPSERT は長時間を要するため十分な上限を設定する。
    private const int ImportCommandTimeoutSeconds = 600;

    // 取込トランザクションを直列化する advisory lock のキー（固定値）。
    private const long ImportAdvisoryLockKey = 0x554E_4458_494D_5054;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<SalesImportService> _logger;

    public SalesImportService(IDbConnectionFactory connectionFactory, ILogger<SalesImportService> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        DapperConfiguration.Initialize();
    }

    /// <summary>レコード列を取り込む。バッチ作成・データ投入・マスタ更新を行う。</summary>
    public async Task<ImportResult> ImportAsync(
        IEnumerable<SalesRecord> records,
        ImportSourceType sourceType,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        // バッチ行は先にコミットし、後続が失敗しても失敗履歴を残せるようにする。
        var batchId = await CreateBatchAsync(connection, sourceType, fileName, cancellationToken);
        _logger.LogInformation(
            "取込バッチ #{BatchId} を開始しました（種別={Source}, ファイル={File}）。",
            batchId, sourceType, fileName);

        try
        {
            var result = await LoadAsync(connection, batchId, records, cancellationToken);
            _logger.LogInformation(
                "取込バッチ #{BatchId} が完了しました（{Rows} 行 / {Weeks} 週）。",
                batchId, result.RowCount, result.WeekCount);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取込バッチ #{BatchId} が失敗しました。", batchId);
            await MarkBatchFailedAsync(connection, batchId, ex.Message, cancellationToken);
            throw;
        }
    }

    private static async Task<long> CreateBatchAsync(
        NpgsqlConnection connection, ImportSourceType sourceType, string fileName, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO import_batch (source_type, file_name, status)
            VALUES (@sourceType, @fileName, 'processing')
            RETURNING id;
            """;

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new { sourceType = ToDbSourceType(sourceType), fileName },
            commandTimeout: ImportCommandTimeoutSeconds,
            cancellationToken: ct));
    }

    private async Task<ImportResult> LoadAsync(
        NpgsqlConnection connection, long batchId, IEnumerable<SalesRecord> records, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // 取込の同時実行を直列化する（同一週の並行取込による取込履歴の不整合を防ぐ）。
        // トランザクション終了時に自動解放される。
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(@lockKey);",
            new { lockKey = ImportAdvisoryLockKey },
            transaction: transaction,
            commandTimeout: ImportCommandTimeoutSeconds,
            cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            SalesWeeklySchema.StagingTableDdl(),
            transaction: transaction,
            commandTimeout: ImportCommandTimeoutSeconds,
            cancellationToken: ct));

        var copiedRows = await CopyToStagingAsync(connection, records, ct);
        if (copiedRows == 0)
        {
            throw new AppException(ErrorCodes.ImportFileEmpty, 422);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            SalesWeeklySchema.UpsertFromStagingSql(),
            new { batchId },
            transaction: transaction,
            commandTimeout: ImportCommandTimeoutSeconds,
            cancellationToken: ct));

        await RefreshMastersAsync(connection, transaction, ct);

        var stats = await connection.QuerySingleAsync<StagingStats>(new CommandDefinition($"""
            SELECT COUNT(*)::int                   AS row_count,
                   COUNT(DISTINCT import_date)::int AS week_count,
                   MIN(import_date)                AS min_import_date,
                   MAX(import_date)                AS max_import_date
            FROM {SalesWeeklySchema.StagingTableName};
            """,
            transaction: transaction,
            commandTimeout: ImportCommandTimeoutSeconds,
            cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE import_batch
            SET status = 'completed',
                row_count = @rowCount,
                week_count = @weekCount,
                min_import_date = @minImportDate,
                max_import_date = @maxImportDate,
                completed_at = now()
            WHERE id = @batchId;
            """,
            new
            {
                batchId,
                rowCount = stats.RowCount,
                weekCount = stats.WeekCount,
                minImportDate = stats.MinImportDate,
                maxImportDate = stats.MaxImportDate,
            },
            transaction: transaction,
            commandTimeout: ImportCommandTimeoutSeconds,
            cancellationToken: ct));

        await transaction.CommitAsync(ct);

        return new ImportResult(
            batchId, stats.RowCount, stats.WeekCount, stats.MinImportDate, stats.MaxImportDate);
    }

    private static async Task<int> CopyToStagingAsync(
        NpgsqlConnection connection, IEnumerable<SalesRecord> records, CancellationToken ct)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            SalesWeeklySchema.CopyCommand(), ct);

        var count = 0;
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            await writer.StartRowAsync(ct);

            await writer.WriteAsync(record.ImportDate, NpgsqlDbType.Date, ct);
            await writer.WriteAsync(record.CustomerCode, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(record.GyotaiCode, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(record.ChohyoKubunName, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(record.Department, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(record.HinbanCode, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(record.TanpinCode, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(record.Hinmei, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(record.ShohinKigou, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(record.Color, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(record.Size, NpgsqlDbType.Text, ct);
            await WriteNullableTextAsync(writer, record.Tanawari1, ct);
            await WriteNullableTextAsync(writer, record.Tanawari2, ct);
            await writer.WriteAsync(record.ToshuUriageCount1, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.ToshuUriageCount2, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.ToshuUriageCount3, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.ToshuUriageCount4, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.ToshuUriageCount5, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.ToshuUriageCount6, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.ToshuUriageCount7, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.UriageCountZenshu, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.UriageCount2Shumae, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.UriageCount3Shumae, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.UriageCount4Shumae, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.Zaikosu, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.RuikeiUriageCount, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.RuikeiNohinCount, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.HatchuCount, NpgsqlDbType.Numeric, ct);
            await writer.WriteAsync(record.DonyuDate, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(record.Zainiti, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.Genka, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.Baika, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(record.Kisetsu, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(record.SakizukeCount, NpgsqlDbType.Integer, ct);
            await WriteNullableTimestampAsync(writer, record.SourceCreatedAt, ct);
            await WriteNullableTimestampAsync(writer, record.SourceUpdatedAt, ct);

            count++;
        }

        await writer.CompleteAsync(ct);
        return count;
    }

    private static async Task WriteNullableTextAsync(
        NpgsqlBinaryImporter writer, string? value, CancellationToken ct)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(ct);
        }
        else
        {
            await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
        }
    }

    private static async Task WriteNullableTimestampAsync(
        NpgsqlBinaryImporter writer, DateTime? value, CancellationToken ct)
    {
        if (value is null)
        {
            await writer.WriteNullAsync(ct);
        }
        else
        {
            await writer.WriteAsync(value.Value, NpgsqlDbType.Timestamp, ct);
        }
    }

    private static async Task RefreshMastersAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var staging = SalesWeeklySchema.StagingTableName;

        // 取り込んだ行に含まれるコードのみをマスタへ反映する（既存コードはスキップ）。
        var sql = $"""
            INSERT INTO department (code)
            SELECT DISTINCT department FROM {staging} WHERE department <> ''
            ON CONFLICT (code) DO NOTHING;

            INSERT INTO customer (code)
            SELECT DISTINCT customer_code FROM {staging} WHERE customer_code <> ''
            ON CONFLICT (code) DO NOTHING;

            INSERT INTO business_type (code)
            SELECT DISTINCT gyotai_code FROM {staging} WHERE gyotai_code <> ''
            ON CONFLICT (code) DO NOTHING;

            INSERT INTO season (code)
            SELECT DISTINCT kisetsu FROM {staging} WHERE kisetsu <> ''
            ON CONFLICT (code) DO NOTHING;
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            transaction: transaction,
            commandTimeout: ImportCommandTimeoutSeconds,
            cancellationToken: ct));
    }

    private async Task MarkBatchFailedAsync(
        NpgsqlConnection connection, long batchId, string error, CancellationToken ct)
    {
        // 失敗履歴の記録は補助処理。記録自体の失敗で元エラーを覆い隠さない。
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE import_batch
                SET status = 'failed', error_message = @error, completed_at = now()
                WHERE id = @batchId;
                """,
                new { batchId, error = Truncate(error, MaxErrorMessageLength) },
                commandTimeout: ImportCommandTimeoutSeconds,
                cancellationToken: ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取込バッチ #{BatchId} の失敗状態の記録に失敗しました。", batchId);
        }
    }

    private static string ToDbSourceType(ImportSourceType sourceType) => sourceType switch
    {
        ImportSourceType.InitialDump => "initial_dump",
        ImportSourceType.WeeklyCsv => "weekly_csv",
        _ => throw new ArgumentOutOfRangeException(nameof(sourceType), sourceType, null),
    };

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private sealed record StagingStats(
        int RowCount, int WeekCount, DateOnly? MinImportDate, DateOnly? MaxImportDate);
}
