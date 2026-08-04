using UndeuxSales.Core.SubsidiaryCheck;
using UndeuxSales.Core.SubsidiaryCheck.ManualCheck;

namespace UndeuxSales.Tests.Unit;

/// <summary>手入力チェックの比較エンジン（ManualCheckComparer）の単体テスト。</summary>
public sealed class ManualCheckComparerTests
{
    // ---- ビルダ ----

    private static ManualInputField Text(string key, string value) =>
        new(key, key, ManualFieldValueType.String, false, ManualFieldCompareMode.Text,
            new[] { value }, Array.Empty<RatioComponent>());

    private static ManualInputField MultiText(string key, params string[] values) =>
        new(key, key, ManualFieldValueType.String, true, ManualFieldCompareMode.Text,
            values, Array.Empty<RatioComponent>());

    private static ManualInputField Ratio(string key, params (string Material, decimal? Percent)[] comps) =>
        new(key, key, ManualFieldValueType.String, true, ManualFieldCompareMode.Ratio,
            Array.Empty<string>(), comps.Select(c => new RatioComponent(c.Material, c.Percent)).ToList());

    private static ManualInputField Care(string key, params string[] codes) =>
        new(key, key, ManualFieldValueType.String, true, ManualFieldCompareMode.CareIcon,
            codes, Array.Empty<RatioComponent>());

    private static ExtractedField Ex(string key, string? text, bool readable = true, params string[] items) =>
        new(key, text, items, readable);

    private static ManualCheckInput Input(params ManualInputField[] fields) => new(null, "", fields);

    private static ManualCheckExtraction Extract(params ExtractedField[] fields) => new(fields);

    private static ManualFieldResult ResultFor(ManualCheckComparison c, string key) =>
        c.Results.Single(r => r.Key == key);

    // ---- 文字列突合 ----

    [Fact]
    public void Text_ExactMatch_IsMatch()
    {
        var c = ManualCheckComparer.Compare(
            Input(Text("productNumber", "WC8945CB")),
            Extract(Ex("productNumber", "WC8945CB")));

        Assert.Equal(ManualCheckVerdict.Match, ResultFor(c, "productNumber").Verdict);
        Assert.Equal(SubsidiaryCheckSeverity.Pass, c.Judgment);
    }

    [Theory]
    [InlineData("ＷＣ8945ＣＢ", "WC8945CB")] // 全角ラテン → NFKC で半角化
    [InlineData("wc8945cb", "WC8945CB")]     // 大文字小文字非依存
    [InlineData("１１０", "110")]             // 全角数字 → 半角
    [InlineData(" 110 ", "110")]              // 前後空白の除去
    public void Text_NormalizedEquivalents_AreMatch(string manual, string extracted)
    {
        var c = ManualCheckComparer.Compare(
            Input(Text("size", manual)), Extract(Ex("size", extracted)));

        Assert.Equal(ManualCheckVerdict.Match, ResultFor(c, "size").Verdict);
    }

    [Fact]
    public void Text_Different_IsMismatch_AndFailJudgment()
    {
        var c = ManualCheckComparer.Compare(
            Input(Text("size", "110")), Extract(Ex("size", "120")));

        Assert.Equal(ManualCheckVerdict.Mismatch, ResultFor(c, "size").Verdict);
        Assert.Equal(SubsidiaryCheckSeverity.Fail, c.Judgment);
        Assert.Equal(1, c.MismatchCount);
    }

    [Fact]
    public void Text_MissingFromTag_IsMissing_AndWarnJudgment()
    {
        // 抽出結果に該当キーが無い → missing（要確認扱い。相違にはしない）。
        var c = ManualCheckComparer.Compare(
            Input(Text("phone", "0120-542543")), Extract());

        Assert.Equal(ManualCheckVerdict.Missing, ResultFor(c, "phone").Verdict);
        Assert.Null(ResultFor(c, "phone").Extracted);
        Assert.Equal(SubsidiaryCheckSeverity.Warn, c.Judgment);
        Assert.Equal(1, c.WarnCount);
    }

    [Fact]
    public void Text_Unreadable_IsWarn()
    {
        var c = ManualCheckComparer.Compare(
            Input(Text("seller", "株式会社しまむら")),
            Extract(Ex("seller", null, readable: false)));

        Assert.Equal(ManualCheckVerdict.Warn, ResultFor(c, "seller").Verdict);
    }

    [Fact]
    public void EmptyInputFields_AreSkipped()
    {
        var empty = Text("note", "");
        var filled = Text("size", "110");
        var c = ManualCheckComparer.Compare(Input(empty, filled), Extract(Ex("size", "110")));

        Assert.Single(c.Results);
        Assert.Equal(1, c.FieldCount);
        Assert.Equal("size", c.Results[0].Key);
    }

    // ---- 組成（ratio） ----

    [Fact]
    public void Ratio_SameComponentsAndOrder_IsMatch()
    {
        var c = ManualCheckComparer.Compare(
            Input(Ratio("composition", ("綿", 75m), ("ポリエステル", 23m), ("ポリウレタン", 2m))),
            Extract(Ex("composition", null, true, "綿 75%", "ポリエステル 23%", "ポリウレタン 2%")));

        Assert.Equal(ManualCheckVerdict.Match, ResultFor(c, "composition").Verdict);
    }

    [Fact]
    public void Ratio_SameComponentsDifferentOrder_IsWarn()
    {
        var c = ManualCheckComparer.Compare(
            Input(Ratio("composition", ("綿", 75m), ("ポリエステル", 23m), ("ポリウレタン", 2m))),
            Extract(Ex("composition", null, true, "ポリエステル 23%", "綿 75%", "ポリウレタン 2%")));

        Assert.Equal(ManualCheckVerdict.Warn, ResultFor(c, "composition").Verdict);
    }

    [Fact]
    public void Ratio_DifferentPercent_IsMismatch()
    {
        var c = ManualCheckComparer.Compare(
            Input(Ratio("composition", ("綿", 75m), ("ポリエステル", 25m))),
            Extract(Ex("composition", null, true, "綿 70%", "ポリエステル 30%")));

        Assert.Equal(ManualCheckVerdict.Mismatch, ResultFor(c, "composition").Verdict);
    }

    // ---- 洗濯表示（careIcon）: 並び順を含めて突合 ----

    [Fact]
    public void CareIcon_SameSequence_IsMatch()
    {
        var c = ManualCheckComparer.Compare(
            Input(Care("careLabels", "wash_40", "bleach_none", "iron_mid")),
            Extract(Ex("careLabels", null, true, "wash_40", "bleach_none", "iron_mid")));

        Assert.Equal(ManualCheckVerdict.Match, ResultFor(c, "careLabels").Verdict);
    }

    [Fact]
    public void CareIcon_SameSetDifferentOrder_IsMismatch()
    {
        // アイコンは優先順位順が規定のため、並び順の相違は相違（要修正）として扱う。
        var c = ManualCheckComparer.Compare(
            Input(Care("careLabels", "wash_40", "bleach_none")),
            Extract(Ex("careLabels", null, true, "bleach_none", "wash_40")));

        Assert.Equal(ManualCheckVerdict.Mismatch, ResultFor(c, "careLabels").Verdict);
    }

    [Fact]
    public void CareIcon_MissingOne_IsMismatch()
    {
        var c = ManualCheckComparer.Compare(
            Input(Care("careLabels", "wash_40", "bleach_none", "iron_mid")),
            Extract(Ex("careLabels", null, true, "wash_40", "bleach_none")));

        Assert.Equal(ManualCheckVerdict.Mismatch, ResultFor(c, "careLabels").Verdict);
    }

    // ---- 集計・判定 ----

    [Fact]
    public void Judgment_MismatchDominatesWarn()
    {
        var c = ManualCheckComparer.Compare(
            Input(Text("size", "110"), Text("phone", "0120")),
            Extract(Ex("size", "120"))); // size=mismatch, phone=missing(warn)

        Assert.Equal(1, c.MismatchCount);
        Assert.Equal(1, c.WarnCount);
        Assert.Equal(SubsidiaryCheckSeverity.Fail, c.Judgment);
    }

    [Fact]
    public void AllMatch_IsPass()
    {
        var c = ManualCheckComparer.Compare(
            Input(Text("a", "x"), Text("b", "y")),
            Extract(Ex("a", "x"), Ex("b", "y")));

        Assert.Equal(2, c.MatchCount);
        Assert.Equal(SubsidiaryCheckSeverity.Pass, c.Judgment);
    }

    [Fact]
    public void MultiText_SameSetSameOrder_IsMatch()
    {
        var c = ManualCheckComparer.Compare(
            Input(MultiText("aliases", "A", "B")),
            Extract(Ex("aliases", null, true, "A", "B")));

        Assert.Equal(ManualCheckVerdict.Match, ResultFor(c, "aliases").Verdict);
    }
}

/// <summary>手入力チェックの AI 抽出応答パーサ（ManualCheckResponseParser）の単体テスト。</summary>
public sealed class ManualCheckResponseParserTests
{
    private const string ValidJson = """
        { "fields": [
            { "key": "productNumber", "text": "WC8945CB", "readable": true },
            { "key": "composition", "items": ["綿 75%", "ポリエステル 25%"], "readable": true },
            { "key": "phone", "text": null, "readable": false }
        ] }
        """;

    [Fact]
    public void Parse_ValidJson_ReturnsNormalizedFields()
    {
        var result = ManualCheckResponseParser.Parse(ValidJson);

        Assert.True(result.Success);
        Assert.Equal(3, result.Extraction.Fields.Count);
        var comp = result.Extraction.Fields.Single(f => f.Key == "composition");
        Assert.Equal(2, comp.Items.Count);
        var phone = result.Extraction.Fields.Single(f => f.Key == "phone");
        Assert.False(phone.Readable);
        Assert.Null(phone.Text);
    }

    [Fact]
    public void Parse_CodeFencedJson_IsAccepted()
    {
        var result = ManualCheckResponseParser.Parse($"```json\n{ValidJson}\n```");
        Assert.True(result.Success);
    }

    [Fact]
    public void Parse_Empty_IsFailure()
    {
        Assert.False(ManualCheckResponseParser.Parse("").Success);
        Assert.False(ManualCheckResponseParser.Parse("   ").Success);
        Assert.False(ManualCheckResponseParser.Parse(null).Success);
    }

    [Fact]
    public void Parse_NoFields_IsFailure()
    {
        Assert.False(ManualCheckResponseParser.Parse("""{ "fields": [] }""").Success);
        Assert.False(ManualCheckResponseParser.Parse("""{ "other": 1 }""").Success);
    }

    [Fact]
    public void Parse_ReadableInferredFromContent_WhenOmitted()
    {
        // readable 未指定でも text/items があれば readable=true、無ければ false（安全側）。
        var result = ManualCheckResponseParser.Parse("""
            { "fields": [
                { "key": "a", "text": "x" },
                { "key": "b" }
            ] }
            """);

        Assert.True(result.Success);
        Assert.True(result.Extraction.Fields.Single(f => f.Key == "a").Readable);
        Assert.False(result.Extraction.Fields.Single(f => f.Key == "b").Readable);
    }
}

/// <summary>洗濯表示アイコンマスタ（CareIconCatalog）の単体テスト。</summary>
public sealed class CareIconCatalogTests
{
    [Fact]
    public void All_IconsAreNonEmpty_AndCodesUnique()
    {
        var icons = CareIconCatalog.All;
        Assert.NotEmpty(icons);

        var codes = icons.Select(i => i.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var icon in icons)
        {
            Assert.False(string.IsNullOrWhiteSpace(icon.Code));
            Assert.False(string.IsNullOrWhiteSpace(icon.Label));
            Assert.Contains("<svg", icon.Svg);
            Assert.Contains("</svg>", icon.Svg);
        }
    }

    [Fact]
    public void IsKnownCode_And_LabelOf_Work()
    {
        var first = CareIconCatalog.All[0];
        Assert.True(CareIconCatalog.IsKnownCode(first.Code));
        Assert.True(CareIconCatalog.IsKnownCode(first.Code.ToUpperInvariant())); // 大文字小文字非依存
        Assert.False(CareIconCatalog.IsKnownCode("no_such_icon"));
        Assert.Equal(first.Label, CareIconCatalog.LabelOf(first.Code));
        Assert.Equal("no_such_icon", CareIconCatalog.LabelOf("no_such_icon")); // 未知はコードのまま
    }
}
