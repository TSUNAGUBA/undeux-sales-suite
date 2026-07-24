using System.IO.Compression;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
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
//    UNDEUX_DUMP_PATH   : ダンプファイル or 格納ディレクトリ（既定: reference を探索）
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

    // 気温データ（mart.dim_climate）を投入する。売上取込とは独立した参照データで、CSV（リポジトリの SoT）
    // から毎回冪等に再投入する。失敗してもデプロイ全体を止めない（非ブロッキング: 散布図/重回帰は
    // dim_climate が無い週は ClimateModel の標準気候へフォールバックするため、欠落しても動作する）。
    await LoadClimateAsync(connectionFactory, ResolveClimatePath(schemaPath), logger);

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

    foreach (var directory in EnumerateCandidateDirectories("reference"))
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

// ------------------------------------------------------------
// 気温データ（mart.dim_climate）の投入
// ------------------------------------------------------------

// 気温CSVのパスを解決する（UNDEUX_CLIMATE_PATH → schema と同じディレクトリ → db 候補探索の順）。
// 見つからなければ null を返し、投入はスキップする（非ブロッキング）。
static string? ResolveClimatePath(string schemaPath)
{
    var configured = Environment.GetEnvironmentVariable("UNDEUX_CLIMATE_PATH");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return File.Exists(configured) ? configured : null;
    }

    // 既定は schema.sql と同じディレクトリ（compose は db/ を /app/db にマウントする）。
    var schemaDir = Path.GetDirectoryName(Path.GetFullPath(schemaPath));
    if (schemaDir is not null)
    {
        var candidate = Path.Combine(schemaDir, "climate_daily.csv");
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    foreach (var directory in EnumerateCandidateDirectories("db"))
    {
        var candidate = Path.Combine(directory, "climate_daily.csv");
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }

    return null;
}

// 気温CSVを mart.dim_climate へ投入する（TRUNCATE + バイナリ COPY、冪等）。
// 非ブロッキング: いかなる失敗もデプロイを止めず、警告ログのみ残す（CLAUDE.md 原則4）。
static async Task LoadClimateAsync(
    NpgsqlConnectionFactory connectionFactory, string? climatePath, ILogger logger)
{
    if (climatePath is null)
    {
        logger.LogInformation(
            "気温CSV（climate_daily.csv）が見つかりません。気温データの投入をスキップします"
            + "（散布図/重回帰は標準気候へフォールバックします）。");
        return;
    }

    try
    {
        List<ClimateDailyRecord> records;
        using (var stream = File.OpenRead(climatePath))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            records = ClimateCsvReader.ReadRecords(reader).ToList();
        }

        if (records.Count == 0)
        {
            logger.LogWarning(
                "気温CSV {Path} から有効なレコードを取得できませんでした。既存の気温データを温存します。",
                climatePath);
            return;
        }

        // TRUNCATE + COPY を1トランザクションで原子的に行う（再投入で一時的に空にしない）。
        await using var connection = await connectionFactory.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await connection.ExecuteAsync(new CommandDefinition(
            "TRUNCATE mart.dim_climate;", transaction: transaction));

        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY mart.dim_climate (area_code, the_date, temp_avg, temp_max, temp_min) "
            + "FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var record in records)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(record.AreaCode, NpgsqlDbType.Text);
                await writer.WriteAsync(record.Date, NpgsqlDbType.Date);
                await writer.WriteAsync(record.TempAvg, NpgsqlDbType.Double);
                await writer.WriteAsync(record.TempMax, NpgsqlDbType.Double);
                await writer.WriteAsync(record.TempMin, NpgsqlDbType.Double);
            }

            await writer.CompleteAsync();
        }

        await transaction.CommitAsync();
        logger.LogInformation(
            "気温データを投入しました: {Count:N0} 行（{Path}）。", records.Count, climatePath);
    }
    catch (Exception ex)
    {
        logger.LogWarning(
            ex, "気温データの投入に失敗しました（非ブロッキング: 処理を継続します）。");
    }
}
