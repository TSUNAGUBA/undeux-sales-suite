using System.Reflection;
using System.Text.Json;
using UndeuxSales.Infrastructure.Rag;

namespace UndeuxSales.Api.Configuration;

/// <summary>
/// 事前蓄積ナレッジ（SeedKnowledge 埋め込みリソース）を起動時に投入するバックグラウンドサービス。
/// <para>
/// - 冪等: 既存の seed_slug は再投入しない（運用者の編集・削除を巻き戻さない。原則2）。
/// - 非ブロッキング: 失敗してもアプリ起動・API 提供は継続する（原則4）。DB 未準備に備えリトライする。
/// - 統合テスト環境では設定 Rag:SeedOnStartup=false でスキップできる。
/// </para>
/// </summary>
public sealed class KnowledgeSeedHostedService : BackgroundService
{
    private const string ResourcePrefix = "SeedKnowledge";
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<KnowledgeSeedHostedService> _logger;

    public KnowledgeSeedHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<KnowledgeSeedHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Rag:SeedOnStartup", true))
        {
            _logger.LogInformation("Rag:SeedOnStartup=false のためナレッジシードをスキップします。");
            return;
        }

        IReadOnlyList<KnowledgeSeedSource> sources;
        try
        {
            sources = LoadSeedSources();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "シードナレッジ（埋め込みリソース）の読み込みに失敗しました。");
            return;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var seeder = scope.ServiceProvider.GetRequiredService<KnowledgeSeeder>();
                await seeder.SeedAsync(sources, stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ナレッジシードに失敗しました（{Attempt}/{MaxAttempts} 回目）。", attempt, MaxAttempts);
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
            }
        }

        _logger.LogError("ナレッジシードをリトライ上限まで試行しましたが完了しませんでした。API 提供は継続します。");
    }

    /// <summary>埋め込みリソースの manifest.json と本文 .md を読み込む。</summary>
    internal static IReadOnlyList<KnowledgeSeedSource> LoadSeedSources()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.Contains($".{ResourcePrefix}.", StringComparison.Ordinal))
            .ToList();

        var manifestResource = resourceNames.FirstOrDefault(name => name.EndsWith("manifest.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("SeedKnowledge/manifest.json が埋め込まれていません。");

        var manifest = JsonSerializer.Deserialize<List<SeedManifestEntry>>(
            ReadResource(assembly, manifestResource), JsonOptions)
            ?? throw new InvalidOperationException("manifest.json の解析に失敗しました。");

        var sources = new List<KnowledgeSeedSource>(manifest.Count);
        foreach (var entry in manifest)
        {
            var resourceName = resourceNames.FirstOrDefault(name =>
                name.EndsWith($".{ResourcePrefix}.{entry.File}", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"シード本文が見つかりません: {entry.File}");

            var content = ReadResource(assembly, resourceName);
            // 先頭のタイトル見出し（# ...）は entry.Title と重複するため本文からは除く。
            var lines = content.Split('\n');
            if (lines.Length > 0 && lines[0].StartsWith("# ", StringComparison.Ordinal))
            {
                content = string.Join('\n', lines.Skip(1)).TrimStart('\n');
            }

            sources.Add(new KnowledgeSeedSource(
                entry.Slug, entry.Title, entry.Category,
                entry.BusinessTypeCode, entry.DeptCode, entry.BizDomain,
                entry.SourceDocument, content));
        }

        return sources;
    }

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"リソースを開けません: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record SeedManifestEntry(
        string Slug,
        string File,
        string Title,
        string Category,
        string? BusinessTypeCode,
        string? DeptCode,
        string? BizDomain,
        string? SourceDocument);
}
