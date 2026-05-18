using Npgsql;

namespace UndeuxSales.Infrastructure.Database;

/// <summary>
/// <see cref="NpgsqlDataSource"/>（接続プール）を保持し接続を払い出す
/// <see cref="IDbConnectionFactory"/> 実装。アプリ全体でシングルトンとして共有する。
/// </summary>
public sealed class NpgsqlConnectionFactory : IDbConnectionFactory, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("接続文字列が設定されていません。", nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        => await _dataSource.OpenConnectionAsync(cancellationToken);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
