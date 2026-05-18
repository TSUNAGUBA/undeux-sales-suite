using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using UndeuxSales.Core.Models;
using UndeuxSales.Core.Parsing;
using UndeuxSales.Infrastructure.Database;
using UndeuxSales.Infrastructure.Import;

// ============================================================
//  UndeuxSales DataLoader
//  小売提供の初期DBダンプ（mysqldump 形式 .gz）を PostgreSQL へ一括投入する。
//  冪等: 初期投入が完了済みなら再実行時はスキップする。
//
//  環境変数:
//    ConnectionStrings__Default / UNDEUX_DB_CONNECTION : 接続文字列（必須）
//    UNDEUX_DUMP_PATH   : ダンプファイル or 格納ディレクトリ（既定: refference を探索）
//    UNDEUX_SCHEMA_PATH : schema.sql のパス（既定: db/schema.sql を探索）
//    UNDEUX_FORCE_RELOAD: "true" の場合、投入済みでも再投入する
// ============================================================

using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    }));
var logger = loggerFactory.CreateLogger("DataLoader");

try
{
    var connectionString =
        Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? Environment.GetEnvironmentVariable("UNDEUX_DB_CONNECTION");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        logger.LogError(
            "接続文字列が未設定です。ConnectionStrings__Default を設定してください。");
        return 1;
    }

    var dumpPath = ResolveDumpPath(Environment.GetEnvironmentVariable("UNDEUX_DUMP_PATH"));
    var schemaPath = ResolveSchemaPath(Environment.GetEnvironmentVariable("UNDEUX_SCHEMA_PATH"));
    var forceReload = string.Equals(
        Environment.GetEnvironmentVariable("UNDEUX_FORCE_RELOAD"), "true",
        StringComparison.OrdinalIgnoreCase);

    logger.LogInformation("ダンプファイル: {DumpPath}", dumpPath);
    logger.LogInformation("スキーマファイル: {SchemaPath}", schemaPath);

    await using var connectionFactory = new NpgsqlConnectionFactory(connectionString);

    // スキーマ適用（冪等）
    var schemaSql = await File.ReadAllTextAsync(schemaPath);
    await using (var connection = await connectionFactory.OpenConnectionAsync())
    {
        await SchemaInitializer.ApplyAsync(connection, schemaSql);
    }

    logger.LogInformation("スキーマを適用しました。");

    // 冪等性: 初期投入が完了済みならスキップ
    var batchRepository = new ImportBatchRepository(connectionFactory);
    if (!forceReload && await batchRepository.HasCompletedInitialDumpAsync())
    {
        logger.LogInformation(
            "初期DBダンプは取込済みです。スキップします"
            + "（再投入する場合は UNDEUX_FORCE_RELOAD=true）。");
        return 0;
    }

    // ダンプを取込
    logger.LogInformation("初期DBダンプの取込を開始します（数分かかる場合があります）...");
    var importService = new SalesImportService(
        connectionFactory, loggerFactory.CreateLogger<SalesImportService>());

    using var fileStream = File.OpenRead(dumpPath);
    using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
    using var reader = new StreamReader(gzipStream, Encoding.UTF8);

    var records = MySqlDumpReader.ReadRecords(reader);
    var result = await importService.ImportAsync(
        records, ImportSourceType.InitialDump, Path.GetFileName(dumpPath));

    logger.LogInformation(
        "初期投入が完了しました: {Rows:N0} 行 / {Weeks} 週（{Min} 〜 {Max}）。",
        result.RowCount, result.WeekCount, result.MinImportDate, result.MaxImportDate);
    return 0;
}
catch (Exception ex)
{
    logger.LogError(ex, "初期投入が失敗しました。");
    return 1;
}

// ------------------------------------------------------------
// パス解決ヘルパー
// ------------------------------------------------------------
static string ResolveDumpPath(string? configured)
{
    if (!string.IsNullOrWhiteSpace(configured))
    {
        if (File.Exists(configured))
        {
            return configured;
        }

        if (Directory.Exists(configured))
        {
            return FindGzipDump(configured)
                ?? throw new FileNotFoundException(
                    $"ディレクトリ内に .gz ダンプが見つかりません: {configured}");
        }

        throw new FileNotFoundException($"指定された取込パスが存在しません: {configured}");
    }

    foreach (var directory in EnumerateCandidateDirectories("refference"))
    {
        var found = FindGzipDump(directory);
        if (found is not null)
        {
            return found;
        }
    }

    throw new FileNotFoundException(
        "取込対象の .gz ダンプが見つかりません。UNDEUX_DUMP_PATH を指定してください。");
}

static string ResolveSchemaPath(string? configured)
{
    if (!string.IsNullOrWhiteSpace(configured))
    {
        if (File.Exists(configured))
        {
            return configured;
        }

        throw new FileNotFoundException($"指定されたスキーマファイルが存在しません: {configured}");
    }

    foreach (var directory in EnumerateCandidateDirectories("db"))
    {
        var candidate = Path.Combine(directory, "schema.sql");
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    throw new FileNotFoundException(
        "schema.sql が見つかりません。UNDEUX_SCHEMA_PATH を指定してください。");
}

// リポジトリルートを上位へ遡りつつ、指定名のディレクトリ候補を列挙する。
static IEnumerable<string> EnumerateCandidateDirectories(string directoryName)
{
    yield return Path.Combine("/", directoryName);
    yield return directoryName;

    var probe = AppContext.BaseDirectory;
    for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(probe); depth++)
    {
        yield return Path.Combine(probe, directoryName);
        probe = Path.GetDirectoryName(probe);
    }
}

static string? FindGzipDump(string directory)
    => Directory.EnumerateFiles(directory, "*.gz").OrderBy(path => path).FirstOrDefault();
