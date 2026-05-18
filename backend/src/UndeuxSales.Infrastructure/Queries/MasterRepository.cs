using Dapper;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>フィルタUI用のコードマスタ・選択肢を提供するリポジトリ。</summary>
public sealed class MasterRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public MasterRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        DapperConfiguration.Initialize();
    }

    /// <summary>フィルタUIの選択肢一式（部門・取引先・業態・季節・取込日）を取得する。</summary>
    public async Task<FilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var departments = await QueryCodeNamesAsync(connection, "department", cancellationToken);
        var customers = await QueryCodeNamesAsync(connection, "customer", cancellationToken);
        var businessTypes = await QueryCodeNamesAsync(connection, "business_type", cancellationToken);
        var seasons = await QueryCodeNamesAsync(connection, "season", cancellationToken);

        var weeks = (await connection.QueryAsync<DateOnly>(new CommandDefinition("""
            SELECT DISTINCT import_date
            FROM sales_weekly
            ORDER BY import_date;
            """, cancellationToken: cancellationToken))).ToList();

        return new FilterOptions(departments, customers, businessTypes, seasons, weeks);
    }

    private static async Task<IReadOnlyList<CodeName>> QueryCodeNamesAsync(
        Npgsql.NpgsqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        // tableName は呼び出し側固定の定数のみ（外部入力なし）。
        var sql = $"SELECT code, display_name AS name FROM {tableName} ORDER BY code;";
        var rows = await connection.QueryAsync<CodeName>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.ToList();
    }
}
