using Dapper;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.Import;

/// <summary>取込バッチ履歴の参照リポジトリ。</summary>
public sealed class ImportBatchRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ImportBatchRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        DapperConfiguration.Initialize();
    }

    /// <summary>取込バッチ履歴を新しい順に取得する。</summary>
    public async Task<IReadOnlyList<ImportBatchInfo>> ListRecentAsync(
        int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<ImportBatchInfo>(new CommandDefinition("""
            SELECT id, source_type, file_name, status, row_count, week_count,
                   min_import_date, max_import_date, error_message,
                   started_at, completed_at
            FROM import_batch
            ORDER BY started_at DESC, id DESC
            LIMIT @limit;
            """,
            new { limit },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    /// <summary>初期DBダンプの取込が完了済みかどうかを判定する（DataLoader の冪等性判定用）。</summary>
    public async Task<bool> HasCompletedInitialDumpAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
            SELECT EXISTS (
                SELECT 1 FROM import_batch
                WHERE source_type = 'initial_dump' AND status = 'completed'
            );
            """,
            cancellationToken: cancellationToken));
    }
}
