using Npgsql;

namespace UndeuxSales.Infrastructure.Database;

/// <summary>PostgreSQL 接続を払い出すファクトリ。</summary>
public interface IDbConnectionFactory
{
    /// <summary>接続プールから接続を1つ開いて返す。呼び出し側が破棄責任を持つ。</summary>
    Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
