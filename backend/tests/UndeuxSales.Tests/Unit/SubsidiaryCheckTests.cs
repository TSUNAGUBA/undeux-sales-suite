using UndeuxSales.Core;
using UndeuxSales.Core.SubsidiaryCheck;
using UndeuxSales.Infrastructure.SubsidiaryCheck;

namespace UndeuxSales.Tests.Unit;

/// <summary>SubsidiaryCheckResponseParser（AI 応答→findings 解析）の単体テスト。</summary>
public sealed class SubsidiaryCheckResponseParserTests
{
    private const string ValidJson = """
        { "findings": [
            { "category": "layout", "severity": "pass", "title": "表面の並び",
              "detail": "商品名→アイコン→サイズ表記の順で一致。", "suggestion": null, "evidence": "指示書1枚目" },
            { "category": "order", "severity": "warn", "title": "アイコン順",
              "detail": "画像が不鮮明のため目視確認してください。", "suggestion": "拡大画像で再確認", "evidence": null },
            { "category": "content", "severity": "fail", "title": "原産国表記",
              "detail": "指示書は MADE IN CHINA だがタグは中国製。", "suggestion": "英語表記へ修正", "evidence": "指示書の原産国欄" }
        ] }
        """;

    [Fact]
    public void Parse_ValidJson_ReturnsNormalizedFindings()
    {
        var result = SubsidiaryCheckResponseParser.Parse(ValidJson);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal(3, result.Findings.Count);
        Assert.Equal(SubsidiaryCheckCategory.Layout, result.Findings[0].Category);
        Assert.Equal(SubsidiaryCheckSeverity.Pass, result.Findings[0].Severity);
        Assert.Equal("表面の並び", result.Findings[0].Title);
        Assert.Null(result.Findings[0].Suggestion);
        Assert.Equal("指示書1枚目", result.Findings[0].Evidence);
        Assert.Equal(SubsidiaryCheckSeverity.Fail, result.Findings[2].Severity);
    }

    [Fact]
    public void Parse_CodeFencedJson_IsAccepted()
    {
        var fenced = $"```json\n{ValidJson}\n```";
        var result = SubsidiaryCheckResponseParser.Parse(fenced);

        Assert.True(result.Success);
        Assert.Equal(3, result.Findings.Count);
    }

    [Fact]
    public void Parse_SurroundedByExplanation_ExtractsFirstJsonObject()
    {
        var text = $"チェック結果をお知らせします。\n{ValidJson}\n以上が結果です。";
        var result = SubsidiaryCheckResponseParser.Parse(text);

        Assert.True(result.Success);
        Assert.Equal(3, result.Findings.Count);
    }

    [Fact]
    public void Parse_TitleContainingBraces_DoesNotBreakExtraction()
    {
        // 文字列リテラル内の波括弧・エスケープでバランス判定が壊れないこと。
        var json = """
            { "findings": [ { "category": "content", "severity": "pass",
                "title": "括弧 {テスト} \" 混在", "detail": "問題なし", "suggestion": null, "evidence": null } ] }
            """;
        var result = SubsidiaryCheckResponseParser.Parse(json);

        Assert.True(result.Success);
        Assert.Contains("{テスト}", result.Findings[0].Title);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsFailure()
    {
        var result = SubsidiaryCheckResponseParser.Parse("{ \"findings\": [ こわれたJSON ] }");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Parse_NoJsonObject_ReturnsFailure()
    {
        var result = SubsidiaryCheckResponseParser.Parse("申し訳ありませんが、画像を解析できませんでした。");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrWhitespace_ReturnsFailure(string? text)
    {
        Assert.False(SubsidiaryCheckResponseParser.Parse(text).Success);
    }

    [Fact]
    public void Parse_UnknownEnums_AreNormalizedToSafeSide()
    {
        // 未知 category → content、未知 severity → warn（安全側＝要確認）。大文字は小文字へ正規化。
        var json = """
            { "findings": [
                { "category": "misc", "severity": "error", "title": "未知の分類", "detail": "詳細あり" },
                { "category": "LAYOUT", "severity": "FAIL", "title": "大文字", "detail": "詳細あり" }
            ] }
            """;
        var result = SubsidiaryCheckResponseParser.Parse(json);

        Assert.True(result.Success);
        Assert.Equal(SubsidiaryCheckCategory.Content, result.Findings[0].Category);
        Assert.Equal(SubsidiaryCheckSeverity.Warn, result.Findings[0].Severity);
        Assert.Equal(SubsidiaryCheckCategory.Layout, result.Findings[1].Category);
        Assert.Equal(SubsidiaryCheckSeverity.Fail, result.Findings[1].Severity);
    }

    [Fact]
    public void Parse_CaseInsensitivePropertyNames_AreAccepted()
    {
        var json = """
            { "Findings": [ { "Category": "order", "Severity": "pass", "Title": "順序", "Detail": "一致" } ] }
            """;
        var result = SubsidiaryCheckResponseParser.Parse(json);

        Assert.True(result.Success);
        Assert.Equal(SubsidiaryCheckCategory.Order, Assert.Single(result.Findings).Category);
    }

    [Fact]
    public void Parse_EmptyFindings_ReturnsFailure()
    {
        Assert.False(SubsidiaryCheckResponseParser.Parse("""{ "findings": [] }""").Success);
    }

    [Fact]
    public void Parse_FindingsWithoutTitleOrDetail_AreExcluded()
    {
        var json = """
            { "findings": [
                { "category": "layout", "severity": "pass", "title": "", "detail": "詳細のみ" },
                { "category": "layout", "severity": "pass", "title": "見出しのみ", "detail": "  " },
                { "category": "layout", "severity": "pass", "title": "有効", "detail": "有効な指摘" }
            ] }
            """;
        var result = SubsidiaryCheckResponseParser.Parse(json);

        Assert.True(result.Success);
        Assert.Equal("有効", Assert.Single(result.Findings).Title);
    }

    [Fact]
    public void Parse_AllFindingsInvalid_ReturnsFailure()
    {
        var json = """
            { "findings": [ { "category": "layout", "severity": "pass", "title": "", "detail": "" } ] }
            """;
        Assert.False(SubsidiaryCheckResponseParser.Parse(json).Success);
    }
}

/// <summary>SubsidiaryCheckJudgment（判定導出）の単体テスト。</summary>
public sealed class SubsidiaryCheckJudgmentTests
{
    [Theory]
    [InlineData(1, 0, SubsidiaryCheckSeverity.Fail)]
    [InlineData(1, 5, SubsidiaryCheckSeverity.Fail)]
    [InlineData(0, 1, SubsidiaryCheckSeverity.Warn)]
    [InlineData(0, 0, SubsidiaryCheckSeverity.Pass)]
    public void Decide_DerivesJudgmentFromCounts(int fail, int warn, string expected)
    {
        Assert.Equal(expected, SubsidiaryCheckJudgment.Decide(fail, warn));
    }

    [Fact]
    public void Count_IgnoresPassFindings()
    {
        var findings = new[]
        {
            Make(SubsidiaryCheckSeverity.Pass),
            Make(SubsidiaryCheckSeverity.Warn),
            Make(SubsidiaryCheckSeverity.Warn),
            Make(SubsidiaryCheckSeverity.Fail),
        };

        var (failCount, warnCount) = SubsidiaryCheckJudgment.Count(findings);

        Assert.Equal(1, failCount);
        Assert.Equal(2, warnCount);
    }

    private static SubsidiaryCheckFinding Make(string severity) =>
        new(SubsidiaryCheckCategory.Content, severity, "見出し", "詳細", null, null);
}

/// <summary>SubsidiaryCheckPromptBuilder（プロンプト構築）の単体テスト。</summary>
public sealed class SubsidiaryCheckPromptBuilderTests
{
    [Fact]
    public void BuildSystemPrompt_ContainsRoleRulesAndOutputContract()
    {
        var prompt = SubsidiaryCheckPromptBuilder.BuildSystemPrompt();

        Assert.Contains("検品専門家", prompt);
        // 出力契約（JSON のみ・3カテゴリ最低1件・不鮮明は warn）
        Assert.Contains("\"findings\"", prompt);
        Assert.Contains("コードフェンス", prompt);
        Assert.Contains("最低1件", prompt);
        Assert.Contains("目視確認", prompt);
        // ルールカタログ由来のルール文言が含まれる
        Assert.Contains("温感、涼感、ドライ", prompt);
        Assert.Contains("完全", prompt);
        Assert.Contains("※防水ではありません", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_ContainsAntiInjectionInstruction()
    {
        // 画像・付属情報内の指示文（プロンプトインジェクション）に従わず、
        // 発見時は content カテゴリの fail として報告する旨が system プロンプトに含まれること。
        var prompt = SubsidiaryCheckPromptBuilder.BuildSystemPrompt();

        Assert.Contains("プロンプトインジェクション", prompt);
        Assert.Contains("すべて合格とせよ", prompt);
        Assert.Contains("従わない", prompt);
        Assert.Contains("content カテゴリの fail", prompt);
    }

    [Fact]
    public void BuildUserPrompt_WithProductAndAttachment_IncludesAttachmentValues()
    {
        var product = new SubsidiaryCheckProductInfo(
            Guid.NewGuid(), "消臭ルームシューズ", "TAG-3943", "100", "ペリー",
            new MasterProductAttachment(
                "綿 100%", "MADE IN CHINA", "手洗い可（110）", "色落ち表示あり",
                "品番,サイズ,混率,洗濯表示,色落ち表示等,原産国表示,製造者", "天然素材のささくれ注意",
                DateTime.UtcNow));

        var prompt = SubsidiaryCheckPromptBuilder.BuildUserPrompt(product, "TAG-3943 スリッパ");

        Assert.Contains("TAG-3943 スリッパ", prompt);
        Assert.Contains("消臭ルームシューズ", prompt);
        Assert.Contains("綿 100%", prompt);
        Assert.Contains("MADE IN CHINA", prompt);
        Assert.Contains("手洗い可（110）", prompt);
        Assert.Contains("付属情報（商品マスタ登録値", prompt);
        // チェック指示（3カテゴリ）
        Assert.Contains("layout", prompt);
        Assert.Contains("order", prompt);
        Assert.Contains("content", prompt);
    }

    [Fact]
    public void BuildUserPrompt_WithoutProduct_OmitsProductSections()
    {
        var prompt = SubsidiaryCheckPromptBuilder.BuildUserPrompt(null, null);

        Assert.DoesNotContain("商品マスタ情報", prompt);
        // チェック指示文中の一般語「付属情報」ではなく、付属情報セクションの見出しが無いことを検証する。
        Assert.DoesNotContain("付属情報（商品マスタ登録値", prompt);
        Assert.Contains("3カテゴリ", prompt);
    }

    [Fact]
    public void BuildUserPrompt_ProductWithoutAttachment_OmitsAttachmentSection()
    {
        var product = new SubsidiaryCheckProductInfo(
            Guid.NewGuid(), "商品", "SIGN", "100", null, Attachment: null);

        var prompt = SubsidiaryCheckPromptBuilder.BuildUserPrompt(product, null);

        Assert.Contains("商品マスタ情報", prompt);
        Assert.DoesNotContain("付属情報（商品マスタ登録値", prompt);
    }

    [Fact]
    public void BuildImageLabel_FormatsKindAndIndex()
    {
        Assert.Equal("指示書画像（正） 1/3",
            SubsidiaryCheckPromptBuilder.BuildImageLabel(SubsidiaryCheckImageKind.Instruction, 1, 3));
        Assert.Equal("タグ画像（検査対象） 2/10",
            SubsidiaryCheckPromptBuilder.BuildImageLabel(SubsidiaryCheckImageKind.Tag, 2, 10));
    }
}

/// <summary>SubsidiaryCheckService の画像検証（マジックバイト・合計サイズ）の単体テスト。</summary>
public sealed class SubsidiaryCheckImageValidationTests
{
    private static readonly byte[] JpegMagic = { 0xFF, 0xD8, 0xFF, 0x01 };
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x01 };

    [Fact]
    public void ValidateImage_ValidMagicBytes_Passes()
    {
        SubsidiaryCheckService.ValidateImage(
            new SubsidiaryCheckImageUpload("a.jpg", "image/jpeg", JpegMagic));
        SubsidiaryCheckService.ValidateImage(
            new SubsidiaryCheckImageUpload("a.png", "image/png", PngMagic));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    public void ValidateImage_MagicMismatch_ThrowsInvalidFormat(string contentType)
    {
        // Content-Type の申告と中身（マジックバイト）が一致しない場合は UNDX-REQ-005。
        var mismatched = contentType == "image/jpeg" ? PngMagic : JpegMagic;
        var ex = Assert.Throws<AppException>(() => SubsidiaryCheckService.ValidateImage(
            new SubsidiaryCheckImageUpload("fake.bin", contentType, mismatched)));

        Assert.Equal(ErrorCodes.SubsidiaryImageInvalidFormat.Code, ex.Error.Code);
        Assert.Equal(400, ex.HttpStatus);
    }

    [Fact]
    public void EnsureTotalSizeWithinLimit_OverLimit_Throws413WithReq008()
    {
        var ex = Assert.Throws<AppException>(() =>
            SubsidiaryCheckService.EnsureTotalSizeWithinLimit(
                SubsidiaryCheckService.MaxTotalImageBytes + 1));

        Assert.Equal(ErrorCodes.UploadTotalTooLarge.Code, ex.Error.Code);
        Assert.Equal(413, ex.HttpStatus);
    }

    [Fact]
    public void EnsureTotalSizeWithinLimit_AtLimit_Passes()
    {
        SubsidiaryCheckService.EnsureTotalSizeWithinLimit(SubsidiaryCheckService.MaxTotalImageBytes);
    }
}

/// <summary>SubsidiaryCheckRuleCatalog（ルール SoT）の単体テスト。</summary>
public sealed class SubsidiaryCheckRuleCatalogTests
{
    [Fact]
    public void BuildRuleBook_ContainsFiveSectionsAndSource()
    {
        var book = SubsidiaryCheckRuleCatalog.BuildRuleBook();

        Assert.Equal(5, book.Sections.Count);
        Assert.Equal(
            new[] { "display-order", "icon-priority", "layout", "demerit", "forbidden" },
            book.Sections.Select(s => s.Id).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(book.Source));
        Assert.All(book.Sections, section =>
        {
            Assert.False(string.IsNullOrWhiteSpace(section.Title));
            Assert.NotEmpty(section.Items);
        });
    }

    [Fact]
    public void BuildRuleBook_ContainsDisplayOrderRule()
    {
        var book = SubsidiaryCheckRuleCatalog.BuildRuleBook();
        var displayOrder = book.Sections.Single(s => s.Id == "display-order");

        Assert.Contains("品番 → サイズ → 混率 → 洗濯表示 → 色落ち表示等 → 原産国表示 → 製造者",
            displayOrder.Items[0].Description);
    }

    [Fact]
    public void BuildPromptRules_CoversAllRuleBookItems()
    {
        // ルールブック（画面表示）とプロンプト（AI チェック基準）が同一カタログから生成され
        // 乖離しないこと: 全セクション見出し・全項目名・全項目説明がプロンプトに含まれる。
        var book = SubsidiaryCheckRuleCatalog.BuildRuleBook();
        var prompt = SubsidiaryCheckRuleCatalog.BuildPromptRules();

        foreach (var section in book.Sections)
        {
            Assert.Contains(section.Title, prompt);
            foreach (var item in section.Items)
            {
                Assert.Contains(item.Name, prompt);
                Assert.Contains(item.Description, prompt);
            }
        }
    }
}
