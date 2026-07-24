using UndeuxSales.Api.Configuration;
using UndeuxSales.Core.Rag;

namespace UndeuxSales.Tests.Unit;

/// <summary>
/// SeedKnowledge 埋め込みリソース（manifest.json ＋ 本文 .md）の整合検証。
/// マニフェストと実リソースの不一致・語彙違反をビルド時テストで検出する。
/// </summary>
public sealed class SeedKnowledgeResourceTests
{
    [Fact]
    public void LoadSeedSources_LoadsAllManifestEntries()
    {
        var sources = KnowledgeSeedHostedService.LoadSeedSources();

        Assert.True(sources.Count >= 25, $"シードは25件以上を想定（実際: {sources.Count} 件）");
        Assert.Equal(sources.Count, sources.Select(s => s.Slug).Distinct().Count());

        foreach (var source in sources)
        {
            Assert.False(string.IsNullOrWhiteSpace(source.Title), $"{source.Slug}: title が空");
            Assert.False(string.IsNullOrWhiteSpace(source.Content), $"{source.Slug}: content が空");
            Assert.Empty(KnowledgeTaxonomy.ValidateEntry(
                KnowledgeTaxonomy.ScopeOperator, source.Category,
                source.BusinessTypeCode, source.DeptCode, source.BizDomain));
        }
    }

    [Fact]
    public void LoadSeedSources_CoversRequiredCategoriesAndDomains()
    {
        var sources = KnowledgeSeedHostedService.LoadSeedSources();

        // マニュアル＋しまむらグループ＋業態（5業態）＋基本情報が揃っていること。
        Assert.Contains(sources, s => s.Category == KnowledgeTaxonomy.CategoryManual);
        Assert.Contains(sources, s => s.Category == KnowledgeTaxonomy.CategoryGroup);
        Assert.Contains(sources, s => s.Category == KnowledgeTaxonomy.CategoryBasic);

        var businessTypes = sources
            .Where(s => s.Category == KnowledgeTaxonomy.CategoryBusinessType)
            .Select(s => s.BusinessTypeCode)
            .ToHashSet();
        Assert.Superset(new HashSet<string?> { "01", "02", "04", "05", "06" }, businessTypes);

        // 業務チャットの3部署タグがすべて存在すること。
        var domains = sources.Select(s => s.BizDomain).Where(d => d is not null).ToHashSet();
        Assert.Superset(new HashSet<string?> { "system", "quality", "logistics" }, domains);
    }
}
