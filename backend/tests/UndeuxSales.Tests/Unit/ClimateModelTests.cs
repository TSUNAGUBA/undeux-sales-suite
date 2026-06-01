using UndeuxSales.Core;
using UndeuxSales.Core.Models;

namespace UndeuxSales.Tests.Unit;

public sealed class ClimateModelTests
{
    [Theory]
    [InlineData("standard", TemperatureArea.Standard)]
    [InlineData("cold", TemperatureArea.Cold)]
    [InlineData("warm", TemperatureArea.Warm)]
    public void ParseArea_KnownKeys_ReturnsArea(string key, TemperatureArea expected)
    {
        Assert.Equal(expected, ClimateModel.ParseArea(key));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("STANDARD")]
    [InlineData("tokyo")]
    public void ParseArea_UnknownKeys_ReturnsNull(string? key)
    {
        Assert.Null(ClimateModel.ParseArea(key));
    }

    [Theory]
    [InlineData(TemperatureArea.Standard, "東京")]
    [InlineData(TemperatureArea.Cold, "札幌")]
    [InlineData(TemperatureArea.Warm, "那覇")]
    public void CityName_ReturnsReferenceCity(TemperatureArea area, string expected)
    {
        Assert.Equal(expected, ClimateModel.CityName(area));
    }

    [Theory]
    [InlineData(TemperatureArea.Standard)]
    [InlineData(TemperatureArea.Cold)]
    [InlineData(TemperatureArea.Warm)]
    public void Daily_MaxIsAtLeastAvgIsAtLeastMin(TemperatureArea area)
    {
        // 1年を通じて 最高 >= 平均 >= 最低 が常に成り立つことを確認する。
        for (var month = 1; month <= 12; month++)
        {
            var reading = ClimateModel.Daily(area, new DateOnly(2026, month, 15));
            Assert.True(reading.Maximum >= reading.Average,
                $"{area} {month}月: max {reading.Maximum} < avg {reading.Average}");
            Assert.True(reading.Average >= reading.Minimum,
                $"{area} {month}月: avg {reading.Average} < min {reading.Minimum}");
        }
    }

    [Fact]
    public void Daily_SummerWarmerThanWinter_ForStandard()
    {
        var august = ClimateModel.Daily(TemperatureArea.Standard, new DateOnly(2026, 8, 15));
        var january = ClimateModel.Daily(TemperatureArea.Standard, new DateOnly(2026, 1, 15));

        Assert.True(august.Average > january.Average);
    }

    [Fact]
    public void Daily_WarmAreaWarmerThanColdArea_InWinter()
    {
        var naha = ClimateModel.Daily(TemperatureArea.Warm, new DateOnly(2026, 1, 15));
        var sapporo = ClimateModel.Daily(TemperatureArea.Cold, new DateOnly(2026, 1, 15));

        // 那覇（温暖）は札幌（寒冷）より冬でも明確に暖かい。
        Assert.True(naha.Average > sapporo.Average + 15);
    }

    [Fact]
    public void Daily_MidMonth_ApproximatesMonthlyNormal()
    {
        // 月の中央付近は月別平年値に近い（東京1月の日平均 ≈ 5.4℃）。
        var reading = ClimateModel.Daily(TemperatureArea.Standard, new DateOnly(2026, 1, 15));
        Assert.InRange(reading.Average, 4.0, 7.0);
    }

    [Fact]
    public void Week_AggregatesMondayToSunday_AverageWithinMinMax()
    {
        // 取込日 2026-05-18（月）→ 対象週 2026-05-11〜05-17。
        var week = ClimateModel.Week(TemperatureArea.Standard, new DateOnly(2026, 5, 18));

        Assert.True(week.Maximum >= week.Average);
        Assert.True(week.Average >= week.Minimum);
        // 5月中旬の東京は概ね 15〜25℃ の範囲。
        Assert.InRange(week.Average, 13.0, 26.0);
    }

    [Fact]
    public void Range_OverFullYear_MaxIsSummerHighMinIsWinterLow()
    {
        var year = ClimateModel.Range(
            TemperatureArea.Standard, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        // 年間の最高は真夏の日最高（>=30℃ 近辺）、最低は真冬の日最低（<=2℃ 近辺）。
        Assert.True(year.Maximum > year.Average);
        Assert.True(year.Minimum < year.Average);
        Assert.InRange(year.Average, 13.0, 19.0); // 東京の年平均 ≈ 15.8℃
    }

    [Fact]
    public void Range_ReversedBounds_IsHandledLikeForwardRange()
    {
        var forward = ClimateModel.Range(
            TemperatureArea.Warm, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        var reversed = ClimateModel.Range(
            TemperatureArea.Warm, new DateOnly(2026, 3, 31), new DateOnly(2026, 3, 1));

        Assert.Equal(forward.Average, reversed.Average, 3);
        Assert.Equal(forward.Maximum, reversed.Maximum, 3);
        Assert.Equal(forward.Minimum, reversed.Minimum, 3);
    }
}
