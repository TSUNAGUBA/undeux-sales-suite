using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using UndeuxSales.Core.Models;
using UndeuxSales.Infrastructure.Database;
using UndeuxSales.Infrastructure.Import;

namespace UndeuxSales.Tests.Integration;

/// <summary>
/// 統合テスト用のデータベースフィクスチャ。テストDB（undeux_test）を作成・初期化し、
/// 既知のシードデータを投入したうえで WebApplicationFactory を提供する。
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string TestDatabaseName = "undeux_test";

    private readonly string _adminConnectionString;

    public string ConnectionString { get; }

    public CustomWebApplicationFactory Factory { get; private set; } = null!;

    public DatabaseFixture()
    {
        var host = EnvOrDefault("UNDEUX_TEST_PGHOST", "localhost");
        var port = EnvOrDefault("UNDEUX_TEST_PGPORT", "5432");
        var user = EnvOrDefault("UNDEUX_TEST_PGUSER", "undeux");
        var password = EnvOrDefault("UNDEUX_TEST_PGPASSWORD", "undeux");
        var adminDatabase = EnvOrDefault("UNDEUX_TEST_PGADMINDB", "undeux");

        _adminConnectionString =
            $"Host={host};Port={port};Database={adminDatabase};Username={user};Password={password}";
        ConnectionString =
            $"Host={host};Port={port};Database={TestDatabaseName};Username={user};Password={password}";
    }

    public async Task InitializeAsync()
    {
        // 初期化のどの段で落ちたかを特定できるようにする。ここで例外が出ると
        // コレクション（統合テスト全件）が一括失敗するため、素の例外だけでは原因を追えない。
        try
        {
            await InitializeCoreAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                // 例外は型名＋メッセージに絞り、ToString() の全文は載せない。
                // 目的は出力の重複を避けること: 元例外は InnerException として保持しており、
                // xUnit が内側の型名・メッセージ・スタックトレースを別途そのまま描画する。
                // 注意: これは資格情報の秘匿にはならない（内側の描画も、ここで残す ex.Message も、
                // 接続文字列断片を含みうる）。秘匿は SafeConnectionInfo() が付与する
                // 「接続先」表示にのみ効く。テストDBの資格情報はローカル既定値であり、
                // 秘匿ではなく可読性のための整形と位置づける。
                $"統合テストのDB初期化に失敗しました（段階: {_initStage}、接続先: {SafeConnectionInfo()}）。"
                + $"詳細: {ex.GetType().Name}: {ex.Message}",
                ex);
        }
    }

    /// <summary>初期化の進行段階（失敗時の切り分け用）。</summary>
    private string _initStage = "(未開始)";

    /// <summary>
    /// 接続情報のうち資格情報を除いた部分（ログ・例外メッセージ用）。
    /// 解析自体が失敗しても例外を投げない: この関数は初期化失敗の catch から呼ばれるため、
    /// ここで投げると診断対象である元の例外と段階ラベルを失う。
    /// </summary>
    private string SafeConnectionInfo()
    {
        try
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString);
            return $"Host={builder.Host};Port={builder.Port};Database={builder.Database}";
        }
        catch (Exception ex)
        {
            return $"(接続文字列の解析に失敗: {ex.GetType().Name})";
        }
    }

    private async Task InitializeCoreAsync()
    {
        _initStage = "EnsureTestDatabase";
        await EnsureTestDatabaseAsync();
        _initStage = "ApplySchema(1回目)";
        await ApplySchemaAsync();
        _initStage = "Truncate";
        await TruncateAsync();
        // schema.sql は CREATE 系の他に業態マスタの代表データ（01〜06）の `INSERT ... ON CONFLICT
        // DO NOTHING` を含む。TRUNCATE 後にこの参照データを再投入するため schema.sql を再適用する。
        // 全 DDL は IF NOT EXISTS / IF EXISTS 形式で冪等、INSERT も DO NOTHING なので副作用は無い。
        // ※将来 schema.sql に非冪等な DML を追加するときは、business_type の seed を別ファイルに
        //   切り出し本フィクスチャから個別に呼ぶ形へリファクタすること。
        _initStage = "ApplySchema(2回目・参照データ再投入)";
        await ApplySchemaAsync();
        _initStage = "Seed";
        await SeedAsync();
        _initStage = "WebApplicationFactory 構築";
        Factory = new CustomWebApplicationFactory(ConnectionString);
        _initStage = "完了";
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }
    }

    private async Task EnsureTestDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();

        var exists = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = @name);",
            new { name = TestDatabaseName });

        if (!exists)
        {
            await connection.ExecuteAsync($"CREATE DATABASE {TestDatabaseName};");
        }
    }

    /// <summary>
    /// db/schema.sql をテストDBへ適用する。デプロイ時の再適用（DataLoader 実行）を再現する
    /// リグレッションテストからも呼び出す（例: 空テーブルガードによるシード非復活の検証）。
    /// </summary>
    public async Task ApplySchemaAsync()
    {
        var schemaSql = await File.ReadAllTextAsync(ResolveSchemaPath());
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SchemaInitializer.ApplyAsync(connection, schemaSql);
    }

    private async Task TruncateAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        // m_product_sku は m_product CASCADE で消えるが、両方を明示することで意図を残す。
        // 組織マスタ（m_buyer_section / m_section_department）と knowledge.entry は
        // business_type への FK により CASCADE で消えるが、意図を明示するため列挙する。
        // knowledge.chunk / chunk_embedding は entry の FK CASCADE で消える。
        // 副資材チェック（subsidiary_check / m_product_attachment）は m_product への FK により
        // CASCADE で消えるが、同様に意図を明示する（subsidiary_check_image は check の FK CASCADE）。
        await connection.ExecuteAsync("""
            TRUNCATE m_product_sku, m_product, sales_weekly, import_batch, department,
                     customer, business_type, season, inventory_action_flag,
                     m_buyer_section, m_section_department, m_contact_desk, knowledge.entry,
                     subsidiary_check, subsidiary_check_image, m_product_attachment
                     RESTART IDENTITY CASCADE;
            """);
    }

    private async Task SeedAsync()
    {
        var connectionFactory = new NpgsqlConnectionFactory(ConnectionString);
        await using (connectionFactory)
        {
            var importService = new SalesImportService(
                connectionFactory, NullLogger<SalesImportService>.Instance);
            await importService.ImportAsync(
                SeedData.Records(), ImportSourceType.InitialDump, "seed.test");
        }
    }

    private static string ResolveSchemaPath()
    {
        var probe = AppContext.BaseDirectory;
        for (var depth = 0; depth < 10 && !string.IsNullOrEmpty(probe); depth++)
        {
            var candidate = Path.Combine(probe, "db", "schema.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            probe = Path.GetDirectoryName(probe);
        }

        throw new FileNotFoundException("テスト用に db/schema.sql が見つかりません。");
    }

    private static string EnvOrDefault(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}

/// <summary>統合テストのコレクション定義（DatabaseFixture を共有する）。</summary>
[CollectionDefinition("Api")]
public sealed class ApiTestCollection : ICollectionFixture<DatabaseFixture>;
