using UndeuxSales.Core;

namespace UndeuxSales.Tests.Unit;

/// <summary>
/// 在庫アクションフラグ語彙（InventoryFlagRules）のテスト。
/// DB の CHECK 制約（db/schema.sql の inventory_action_flag）と Core 定数の同期を
/// 文字列レベルで固定する（どちらか片方だけ変更するドリフトを検出する）。
/// </summary>
public sealed class InventoryFlagRulesTests
{
    [Fact]
    public void Vocabulary_MatchesSchemaCheckConstraints()
    {
        // db/schema.sql の CHECK (flag_type IN (...)) / CHECK (status IN (...)) と同一集合。
        // 値を追加・変更する場合はスキーマと本テストを同時に更新すること。
        Assert.Equal(new[] { "stop-order", "markdown" }, InventoryFlagRules.AllFlagTypes);
        Assert.Equal(
            new[] { "candidate", "in-progress", "done", "dismissed" },
            InventoryFlagRules.AllStatuses);
    }

    [Theory]
    [InlineData("stop-order", true)]
    [InlineData("markdown", true)]
    [InlineData("unknown", false)]
    [InlineData(null, false)]
    public void IsValidFlagType_Whitelist(string? value, bool expected)
    {
        Assert.Equal(expected, InventoryFlagRules.IsValidFlagType(value));
    }

    [Theory]
    [InlineData("candidate", true)]
    [InlineData("dismissed", true)]
    [InlineData("open", false)]
    [InlineData(null, false)]
    public void IsValidStatus_Whitelist(string? value, bool expected)
    {
        Assert.Equal(expected, InventoryFlagRules.IsValidStatus(value));
    }

    [Fact]
    public void NormalizeFlagTypes_FiltersUnknownAndDeduplicates()
    {
        var actual = InventoryFlagRules.NormalizeFlagTypes(
            new[] { " Stop-Order ", "MARKDOWN", "stop-order", "unknown", "", null });
        Assert.Equal(new[] { "stop-order", "markdown" }, actual);
    }

    [Fact]
    public void NormalizeFlagStatuses_FiltersUnknownAndDeduplicates()
    {
        var actual = InventoryFlagRules.NormalizeFlagStatuses(
            new[] { "Done", "done", "candidate", "bogus", null });
        Assert.Equal(new[] { "done", "candidate" }, actual);
    }

    [Theory]
    [InlineData("any", "any")]
    [InlineData(" None ", "none")]
    [InlineData("ANY", "any")]
    [InlineData("all", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeFlagged_AcceptsOnlyAnyOrNone(string? value, string? expected)
    {
        Assert.Equal(expected, InventoryFlagRules.NormalizeFlagged(value));
    }
}
