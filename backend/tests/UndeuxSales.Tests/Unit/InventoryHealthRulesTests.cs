using UndeuxSales.Core;

namespace UndeuxSales.Tests.Unit;

/// <summary>
/// 在庫健全性ルール（InventoryHealthRules）の境界値テスト。
/// SQL 側の判定 CASE（InventoryHealthSql.StatusCaseExpression）は本クラスの Classify と
/// 同一論理であるため、境界の期待値はここで固定する（変更時は SQL と本テストを同時に更新）。
/// </summary>
public sealed class InventoryHealthRulesTests
{
    // ---- Classify: 境界値（在庫日数 45 / 60、消化率 0.75、出荷ゼロ 8週） ----

    [Fact]
    public void Classify_StockZero_IsHealthyRegardlessOfOthers()
    {
        Assert.Equal(InventoryHealthRules.StatusHealthy,
            InventoryHealthRules.Classify(stock: 0, stockDays: 999, sellThroughRate: 0, zeroSalesWeeks: 99));
    }

    [Theory]
    [InlineData(45.0, InventoryHealthRulesTestsStatus.Healthy)] // ちょうど45日は健全（超で注意）
    [InlineData(45.1, InventoryHealthRulesTestsStatus.Caution)]
    [InlineData(60.0, InventoryHealthRulesTestsStatus.Caution)] // ちょうど60日は滞留でない（超で滞留）
    [InlineData(60.1, InventoryHealthRulesTestsStatus.Stagnant)]
    public void Classify_StockDaysBoundaries(double stockDays, InventoryHealthRulesTestsStatus expected)
    {
        // 消化率は滞留条件を満たす値（0.75未満）に固定し、在庫日数の境界だけを検証する。
        var actual = InventoryHealthRules.Classify(
            stock: 10, stockDays: stockDays, sellThroughRate: 0.5, zeroSalesWeeks: 0);
        Assert.Equal(ToStatusCode(expected), actual);
    }

    [Theory]
    [InlineData(0.74, InventoryHealthRulesTestsStatus.Stagnant)] // 0.75未満 → 滞留
    [InlineData(0.75, InventoryHealthRulesTestsStatus.Caution)]  // ちょうど0.75は滞留でない（45日超なので注意）
    public void Classify_SellThroughBoundary(double sellThroughRate, InventoryHealthRulesTestsStatus expected)
    {
        var actual = InventoryHealthRules.Classify(
            stock: 10, stockDays: 61, sellThroughRate: sellThroughRate, zeroSalesWeeks: 0);
        Assert.Equal(ToStatusCode(expected), actual);
    }

    [Theory]
    [InlineData(7, InventoryHealthRulesTestsStatus.Healthy)] // 7週は不動でない
    [InlineData(8, InventoryHealthRulesTestsStatus.Dormant)] // 8週ちょうどから不動
    public void Classify_ZeroSalesWeeksBoundary(int zeroSalesWeeks, InventoryHealthRulesTestsStatus expected)
    {
        var actual = InventoryHealthRules.Classify(
            stock: 10, stockDays: 10, sellThroughRate: 0.9, zeroSalesWeeks: zeroSalesWeeks);
        Assert.Equal(ToStatusCode(expected), actual);
    }

    [Fact]
    public void Classify_DormantTakesPrecedenceOverStagnant()
    {
        // 滞留条件（60日超×0.75未満）も満たすが、不動（8週ゼロ）が優先される。
        Assert.Equal(InventoryHealthRules.StatusDormant,
            InventoryHealthRules.Classify(stock: 10, stockDays: 100, sellThroughRate: 0.1, zeroSalesWeeks: 8));
    }

    // ---- Recommend: 状態×発注残×経過週数 ----

    [Fact]
    public void Recommend_Healthy_ReturnsNull()
    {
        Assert.Null(InventoryHealthRules.Recommend(InventoryHealthRules.StatusHealthy, 0m, 0));
    }

    [Fact]
    public void Recommend_Caution_ReturnsObserve()
    {
        Assert.Equal(InventoryHealthRules.ActionObserve,
            InventoryHealthRules.Recommend(InventoryHealthRules.StatusCaution, 0m, 0));
    }

    [Theory]
    [InlineData(0.1, "reduce-order")]       // 発注残あり滞留は発注抑制を最優先
    [InlineData(0.0, "markdown-candidate")] // 発注残なし滞留は値下げ候補
    public void Recommend_Stagnant(double orderQuantity, string expected)
    {
        Assert.Equal(expected,
            InventoryHealthRules.Recommend(InventoryHealthRules.StatusStagnant, (decimal)orderQuantity, 0));
    }

    [Theory]
    [InlineData(0.1, 8, "reduce-order")]        // 発注残あり不動は経過週数より発注抑制を優先
    [InlineData(0.0, 16, "clearance")]          // 16週以上 → 処分・値引き販売の検討
    [InlineData(0.0, 15, "markdown-candidate")] // 12〜15週 → 値下げ候補
    [InlineData(0.0, 12, "markdown-candidate")]
    [InlineData(0.0, 11, "review-shelf")]       // 8〜11週 → 売場・棚割の再点検
    [InlineData(0.0, 8, "review-shelf")]
    public void Recommend_Dormant(double orderQuantity, int zeroSalesWeeks, string expected)
    {
        Assert.Equal(expected,
            InventoryHealthRules.Recommend(
                InventoryHealthRules.StatusDormant, (decimal)orderQuantity, zeroSalesWeeks));
    }

    // ---- NormalizeStatuses: ホワイトリスト・未知値無視・重複排除 ----

    [Fact]
    public void NormalizeStatuses_Null_ReturnsEmpty()
    {
        Assert.Empty(InventoryHealthRules.NormalizeStatuses(null));
    }

    [Fact]
    public void NormalizeStatuses_FiltersUnknownAndDeduplicates()
    {
        var actual = InventoryHealthRules.NormalizeStatuses(
            new[] { " Stagnant ", "DORMANT", "stagnant", "unknown", "", null });
        Assert.Equal(new[] { "stagnant", "dormant" }, actual);
    }

    // ---- BuildActions: フィード生成ルール ----

    [Fact]
    public void BuildActions_AllClear_ReturnsSingleInfo()
    {
        var actions = InventoryHealthRules.BuildActions(EmptyInput());

        var item = Assert.Single(actions);
        Assert.Equal("all-clear", item.Code);
        Assert.Equal("info", item.Severity);
        Assert.Equal(InventoryHealthRules.TabItems, item.TargetTab);
    }

    [Fact]
    public void BuildActions_DormantDetected_IsDangerWithCount()
    {
        var actions = InventoryHealthRules.BuildActions(EmptyInput() with
        {
            DormantCount = 47,
            DormantStock = 16916,
        });

        var dormant = Assert.Single(actions, a => a.Code == "dormant-detected");
        Assert.Equal("danger", dormant.Severity);
        Assert.Equal(47, dormant.Count);
        Assert.Equal(InventoryHealthRules.TabDormant, dormant.TargetTab);
        Assert.DoesNotContain(actions, a => a.Code == "all-clear");
    }

    [Theory]
    [InlineData(0, "warning")] // 発注残のある滞留がなければ警告
    [InlineData(5, "danger")]  // 発注残のある滞留があれば危険（仕入れを止めるだけで改善する最優先対象）
    public void BuildActions_StagnantSeverityDependsOnOrderBacklog(int withOrderCount, string expectedSeverity)
    {
        var actions = InventoryHealthRules.BuildActions(EmptyInput() with
        {
            StagnantCount = 10,
            StagnantWithOrderCount = withOrderCount,
        });

        var stagnant = Assert.Single(actions, a => a.Code == "stagnant-detected");
        Assert.Equal(expectedSeverity, stagnant.Severity);
        Assert.Equal(InventoryHealthRules.TabStagnant, stagnant.TargetTab);
    }

    [Theory]
    [InlineData(0.10, 0.12, true)]  // +2.0pt 悪化 → 警告
    [InlineData(0.10, 0.105, false)] // +0.5pt は閾値（1pt）未満 → 出さない
    public void BuildActions_WorsenedRatio(double previous, double current, bool expected)
    {
        var actions = InventoryHealthRules.BuildActions(EmptyInput() with
        {
            StagnantDormantStockRatio = current,
            PreviousStagnantDormantStockRatio = previous,
        });

        Assert.Equal(expected, actions.Any(a => a.Code == "health-worsened"));
    }

    [Fact]
    public void BuildActions_DepartmentLowSellThrough_RequiresStockShare()
    {
        var input = EmptyInput() with
        {
            TotalStock = 1000,
            TotalSellThroughRate = 0.85,
            Departments = new[]
            {
                // 構成比 50%・消化率が全体より 0.25 低い → 指摘対象
                new InventoryDepartmentHealthInput("部門A", 500, 0.60),
                // 消化率はさらに低いが構成比 1% → 対象外
                new InventoryDepartmentHealthInput("部門B", 10, 0.10),
            },
        };

        var actions = InventoryHealthRules.BuildActions(input);

        var dept = Assert.Single(actions, a => a.Code == "department-low-sell-through");
        Assert.Contains("部門A", dept.Message);
        Assert.Equal(InventoryHealthRules.TabItems, dept.TargetTab);
    }

    private static InventoryActionInput EmptyInput() => new(
        StagnantCount: 0,
        StagnantWithOrderCount: 0,
        DormantCount: 0,
        DormantStock: 0,
        TotalStock: 0,
        StagnantDormantStockRatio: 0,
        PreviousStagnantDormantStockRatio: null,
        TotalSellThroughRate: 0,
        Departments: Array.Empty<InventoryDepartmentHealthInput>());

    private static string ToStatusCode(InventoryHealthRulesTestsStatus status) => status switch
    {
        InventoryHealthRulesTestsStatus.Healthy => InventoryHealthRules.StatusHealthy,
        InventoryHealthRulesTestsStatus.Caution => InventoryHealthRules.StatusCaution,
        InventoryHealthRulesTestsStatus.Stagnant => InventoryHealthRules.StatusStagnant,
        _ => InventoryHealthRules.StatusDormant,
    };
}

/// <summary>InlineData で状態コードを安全に指定するためのテスト用列挙。</summary>
public enum InventoryHealthRulesTestsStatus
{
    Healthy,
    Caution,
    Stagnant,
    Dormant,
}
