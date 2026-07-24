using UndeuxSales.Core.Rag;

namespace UndeuxSales.Tests.Unit;

/// <summary>RagChunker（チャンク分割）の単体テスト。</summary>
public sealed class RagChunkerTests
{
    [Fact]
    public void Split_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(RagChunker.Split(null));
        Assert.Empty(RagChunker.Split(string.Empty));
        Assert.Empty(RagChunker.Split("   \n\n  "));
    }

    [Fact]
    public void Split_ShortText_ReturnsSingleChunk()
    {
        var chunks = RagChunker.Split("しまむらグループの取引理念は公正・適正・合理性です。");
        var chunk = Assert.Single(chunks);
        Assert.Contains("公正・適正・合理性", chunk);
    }

    [Fact]
    public void Split_RemovesPageMarkers_AndKeepsHeadings()
    {
        var chunks = RagChunker.Split("# 見出し\n<!-- page 1 -->\n本文です。");
        var joined = string.Join("\n", chunks);
        Assert.DoesNotContain("<!-- page", joined);
        Assert.Contains("# 見出し", joined);
        Assert.Contains("本文です。", joined);
    }

    [Fact]
    public void Split_LongText_ProducesBoundedChunks()
    {
        var paragraph = string.Concat(Enumerable.Repeat("これは検証用の文章です。", 30)); // 約360文字
        var text = string.Join("\n\n", Enumerable.Repeat(paragraph, 20));
        var chunks = RagChunker.Split(text);

        Assert.True(chunks.Count > 1);
        foreach (var chunk in chunks)
        {
            // 上限＋オーバーラップ引継ぎぶんまでを許容する。
            Assert.InRange(chunk.Length, 1, RagChunker.MaxChars + RagChunker.OverlapChars + 2);
        }
    }

    [Fact]
    public void Split_OversizedSingleBlock_IsForceSplit()
    {
        var text = string.Concat(Enumerable.Repeat("あ", RagChunker.MaxChars * 3));
        var chunks = RagChunker.Split(text);
        Assert.True(chunks.Count >= 3);
    }

    [Fact]
    public void Split_IsDeterministic()
    {
        var text = "# タイトル\n\n" + string.Join("\n\n", Enumerable.Range(1, 50).Map());
        var first = RagChunker.Split(text);
        var second = RagChunker.Split(text);
        Assert.Equal(first, second);
    }
}

internal static class ChunkerTestExtensions
{
    /// <summary>番号付きの段落列を生成する（決定性テスト用）。</summary>
    public static IEnumerable<string> Map(this IEnumerable<int> numbers)
        => numbers.Select(n => $"段落{n}: しまむらの納品は商品センター経由と直流の2方式があります。値札の取り付けは規定に従います。");
}

/// <summary>HashingVectorizer（ローカル埋め込み）の単体テスト。</summary>
public sealed class HashingVectorizerTests
{
    [Fact]
    public void Embed_ReturnsFixedDimension_AndUnitNorm()
    {
        var vector = HashingVectorizer.Embed("Web-EDI の運用手順について");
        Assert.Equal(HashingVectorizer.Dim, vector.Length);

        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        Assert.InRange(norm, 0.999, 1.001);
    }

    [Fact]
    public void Embed_EmptyText_ReturnsZeroVector()
    {
        var vector = HashingVectorizer.Embed("  ");
        Assert.All(vector, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Embed_IsDeterministic()
    {
        var a = HashingVectorizer.Embed("納品数量の訂正はWeb-EDIで行います");
        var b = HashingVectorizer.Embed("納品数量の訂正はWeb-EDIで行います");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Embed_SimilarTextScoresHigherThanUnrelatedText()
    {
        var query = HashingVectorizer.Embed("値札の取り付け方法を教えてください");
        var related = HashingVectorizer.Embed("値札の取り付けは「お取引の基準 値札取付編」の規定に従って行います。");
        var unrelated = HashingVectorizer.Embed("今日の天気は晴れで、気温は20度まで上がる見込みです。");

        var relatedScore = HashingVectorizer.CosineSimilarity(query, related);
        var unrelatedScore = HashingVectorizer.CosineSimilarity(query, unrelated);
        Assert.True(relatedScore > unrelatedScore,
            $"related={relatedScore} が unrelated={unrelatedScore} を上回ること");
    }

    [Fact]
    public void NormalizeText_AbsorbsWidthAndCaseVariants()
    {
        // NFKC 正規化により全角英数・半角カナの表記揺れを吸収する。
        Assert.Equal(
            HashingVectorizer.NormalizeText("ＷＥＢ－ＥＤＩ"),
            HashingVectorizer.NormalizeText("WEB-EDI"));
        Assert.Equal(
            HashingVectorizer.NormalizeText("ｼﾏﾑﾗ"),
            HashingVectorizer.NormalizeText("シマムラ"));
    }
}

/// <summary>KnowledgeTaxonomy（分類語彙・整合検証）の単体テスト。</summary>
public sealed class KnowledgeTaxonomyTests
{
    [Theory]
    [InlineData("operator", "manual", null, null, "system", 0)]
    [InlineData("user", "basic", null, null, null, 0)]
    [InlineData("user", "business_type", "01", null, null, 0)]
    [InlineData("user", "department", "01", "5A", null, 0)]
    public void ValidateEntry_ValidCombinations_ReturnNoErrors(
        string scope, string category, string? btc, string? dept, string? domain, int expectedErrors)
    {
        var errors = KnowledgeTaxonomy.ValidateEntry(scope, category, btc, dept, domain);
        Assert.Equal(expectedErrors, errors.Count);
    }

    [Fact]
    public void ValidateEntry_ManualRequiresOperatorScope()
    {
        var errors = KnowledgeTaxonomy.ValidateEntry("user", "manual", null, null, null);
        Assert.Contains(errors, e => e.Contains("manual"));
    }

    [Fact]
    public void ValidateEntry_BusinessTypeCategoryRequiresCode()
    {
        var errors = KnowledgeTaxonomy.ValidateEntry("user", "business_type", null, null, null);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ValidateEntry_DepartmentCategoryRequiresBothCodes()
    {
        var errors = KnowledgeTaxonomy.ValidateEntry("user", "department", "01", null, null);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ValidateEntry_RejectsUnknownValues()
    {
        Assert.NotEmpty(KnowledgeTaxonomy.ValidateEntry("owner", "basic", null, null, null));
        Assert.NotEmpty(KnowledgeTaxonomy.ValidateEntry("user", "misc", null, null, null));
        Assert.NotEmpty(KnowledgeTaxonomy.ValidateEntry("user", "basic", null, null, "sales"));
    }
}
