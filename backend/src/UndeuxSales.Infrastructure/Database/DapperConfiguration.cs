using Dapper;

namespace UndeuxSales.Infrastructure.Database;

/// <summary>
/// Dapper のグローバル設定。スネークケースのDB列名（<c>import_date</c>）を
/// パスカルケースのプロパティ（<c>ImportDate</c>）へ対応付ける。
/// </summary>
public static class DapperConfiguration
{
    private static readonly object SyncRoot = new();
    private static bool _initialized;

    /// <summary>Dapper のマッピング設定を1度だけ適用する（冪等）。</summary>
    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            DefaultTypeMap.MatchNamesWithUnderscores = true;
            SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
            _initialized = true;
        }
    }
}
