using Npgsql;

namespace UndeuxSales.Infrastructure.Database;

/// <summary>
/// スキーマDDL（<c>db/schema.sql</c>）をデータベースへ適用する。
/// DDLは <c>CREATE ... IF NOT EXISTS</c> / <c>CREATE OR REPLACE</c> で構成され冪等。
/// </summary>
public static class SchemaInitializer
{
    /// <summary>指定の接続にスキーマDDLを適用する。</summary>
    public static async Task ApplyAsync(
        NpgsqlConnection connection,
        string schemaSql,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schemaSql))
        {
            throw new ArgumentException("スキーマDDLが空です。", nameof(schemaSql));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = schemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
