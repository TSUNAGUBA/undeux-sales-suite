using UndeuxSales.Core;

namespace UndeuxSales.Tests.Unit;

public sealed class WeekCalendarTests
{
    [Fact]
    public void DailyDate_MondayIndex_ReturnsImportDateMinusSeven()
    {
        var importDate = new DateOnly(2026, 5, 18); // 月曜日

        var result = WeekCalendar.DailyDate(importDate, 1);

        Assert.Equal(new DateOnly(2026, 5, 11), result);
        Assert.Equal(DayOfWeek.Monday, result.DayOfWeek);
    }

    [Fact]
    public void DailyDate_SundayIndex_ReturnsImportDateMinusOne()
    {
        var importDate = new DateOnly(2026, 5, 18);

        var result = WeekCalendar.DailyDate(importDate, 7);

        Assert.Equal(new DateOnly(2026, 5, 17), result);
        Assert.Equal(DayOfWeek.Sunday, result.DayOfWeek);
    }

    [Theory]
    [InlineData(1, "2026-05-11")]
    [InlineData(2, "2026-05-12")]
    [InlineData(3, "2026-05-13")]
    [InlineData(4, "2026-05-14")]
    [InlineData(5, "2026-05-15")]
    [InlineData(6, "2026-05-16")]
    [InlineData(7, "2026-05-17")]
    public void DailyDate_AllIndexes_MapToConsecutiveDays(int dayIndex, string expected)
    {
        var importDate = new DateOnly(2026, 5, 18);

        var result = WeekCalendar.DailyDate(importDate, dayIndex);

        Assert.Equal(DateOnly.Parse(expected), result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-1)]
    public void DailyDate_OutOfRangeIndex_Throws(int dayIndex)
    {
        var importDate = new DateOnly(2026, 5, 18);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => WeekCalendar.DailyDate(importDate, dayIndex));
    }

    [Fact]
    public void IsValidImportDate_Monday_ReturnsTrue()
    {
        Assert.True(WeekCalendar.IsValidImportDate(new DateOnly(2026, 5, 18)));
    }

    [Theory]
    [InlineData("2026-05-19")] // 火
    [InlineData("2026-05-23")] // 土
    [InlineData("2026-05-24")] // 日
    public void IsValidImportDate_NonMonday_ReturnsFalse(string date)
    {
        Assert.False(WeekCalendar.IsValidImportDate(DateOnly.Parse(date)));
    }

    [Fact]
    public void WeekRange_ReturnsMondayToSunday()
    {
        var importDate = new DateOnly(2026, 5, 18);

        var (start, end) = WeekCalendar.WeekRange(importDate);

        Assert.Equal(new DateOnly(2026, 5, 11), start);
        Assert.Equal(new DateOnly(2026, 5, 17), end);
    }
}
