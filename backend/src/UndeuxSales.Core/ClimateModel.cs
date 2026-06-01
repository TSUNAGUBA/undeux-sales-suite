using UndeuxSales.Core.Models;

namespace UndeuxSales.Core;

/// <summary>気温の1観測値（平均・最高・最低、単位は℃）。</summary>
/// <param name="Average">日平均気温（範囲集計では各日平均の平均）。</param>
/// <param name="Maximum">日最高気温（範囲集計では各日最高の最大）。</param>
/// <param name="Minimum">日最低気温（範囲集計では各日最低の最小）。</param>
public readonly record struct TemperatureReading(double Average, double Maximum, double Minimum);

/// <summary>
/// 日本の標準的な気候（気象庁 1991–2020 平年値）に基づく気温モデル。
/// <para>
/// 小売の売上参照データ（2018–2026）は実測の気温列を持たないため、エリア種別
/// （標準=東京 / 寒冷=札幌 / 温暖=那覇）ごとの月別平年値を日単位に補間して
/// 標準的な気温を与える「サンプル気温データ」を提供する。気象庁サイトの自動取得は
/// 制限されているため、公開されている月別平年値（℃）を参照値として定数で保持する。
/// </para>
/// <para>
/// 週の定義は売上参照ファイルと同一（月曜〜日曜。<see cref="WeekCalendar.WeekRange"/>）。
/// 値は決定的（同じ入力には常に同じ出力）であり、外部依存・乱数を持たない。
/// </para>
/// </summary>
public static class ClimateModel
{
    // 月別平年値（1=1月 .. 12=12月）。出典: 気象庁 平年値（1991–2020）。
    // 値は標準的な日本の気候を表すサンプル/参照データとして用いる。
    private static readonly double[] TokyoMean =
        { 5.4, 6.1, 9.4, 14.3, 18.8, 21.9, 25.7, 26.9, 23.3, 18.0, 12.5, 7.7 };
    private static readonly double[] TokyoMax =
        { 9.8, 10.9, 14.2, 19.4, 23.6, 26.1, 29.9, 31.3, 27.5, 21.9, 16.7, 12.0 };
    private static readonly double[] TokyoMin =
        { 1.2, 2.1, 5.0, 9.8, 14.6, 18.5, 22.4, 23.5, 20.3, 14.8, 8.8, 3.8 };

    private static readonly double[] SapporoMean =
        { -3.2, -2.7, 1.1, 7.3, 13.0, 17.0, 21.1, 22.3, 18.6, 12.1, 5.2, -0.9 };
    private static readonly double[] SapporoMax =
        { -0.6, 0.1, 4.0, 11.5, 17.3, 21.1, 24.9, 26.4, 22.8, 16.4, 8.7, 2.0 };
    private static readonly double[] SapporoMin =
        { -6.0, -5.6, -1.9, 3.3, 8.8, 13.4, 17.9, 19.1, 14.2, 7.5, 1.8, -3.6 };

    private static readonly double[] NahaMean =
        { 17.3, 17.5, 19.1, 21.5, 24.2, 27.2, 29.1, 29.0, 27.9, 25.5, 22.5, 19.0 };
    private static readonly double[] NahaMax =
        { 19.8, 20.2, 21.8, 24.3, 27.0, 29.8, 31.9, 31.8, 30.6, 28.1, 25.0, 21.6 };
    private static readonly double[] NahaMin =
        { 14.6, 14.8, 16.6, 19.3, 22.1, 25.2, 26.8, 26.6, 25.5, 23.1, 19.9, 16.3 };

    /// <summary>エリア種別の参照観測地点名（表示用）。</summary>
    public static string CityName(TemperatureArea area) => area switch
    {
        TemperatureArea.Standard => "東京",
        TemperatureArea.Cold => "札幌",
        TemperatureArea.Warm => "那覇",
        _ => "東京",
    };

    /// <summary>フロント表現（"standard"/"cold"/"warm"）からエリア種別を解釈する。不正値は <c>null</c>。</summary>
    public static TemperatureArea? ParseArea(string? value) => value switch
    {
        "standard" => TemperatureArea.Standard,
        "cold" => TemperatureArea.Cold,
        "warm" => TemperatureArea.Warm,
        _ => null,
    };

    /// <summary>指定日の気温（月別平年値を日単位に線形補間）。</summary>
    public static TemperatureReading Daily(TemperatureArea area, DateOnly date)
    {
        var (mean, max, min) = Normals(area);
        return new TemperatureReading(
            Interpolate(mean, date),
            Interpolate(max, date),
            Interpolate(min, date));
    }

    /// <summary>
    /// 取込日（月曜）に対応する売上対象週（月〜日）の気温。
    /// 週平均=各日平均の平均、週最高=各日最高の最大、週最低=各日最低の最小。
    /// </summary>
    public static TemperatureReading Week(TemperatureArea area, DateOnly importDate)
    {
        var (start, end) = WeekCalendar.WeekRange(importDate);
        return Range(area, start, end);
    }

    /// <summary>
    /// 期間 [start, end]（両端含む）の気温集計。平均=各日平均の平均、最高=各日最高の最大、
    /// 最低=各日最低の最小。クロス集計の時間バケット（年/四半期/月）の代表気温に用いる。
    /// </summary>
    public static TemperatureReading Range(TemperatureArea area, DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var (mean, max, min) = Normals(area);
        double sumMean = 0;
        double maxOut = double.NegativeInfinity;
        double minOut = double.PositiveInfinity;
        var count = 0;

        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var dayMean = Interpolate(mean, d);
            var dayMax = Interpolate(max, d);
            var dayMin = Interpolate(min, d);
            sumMean += dayMean;
            if (dayMax > maxOut) maxOut = dayMax;
            if (dayMin < minOut) minOut = dayMin;
            count++;
        }

        if (count == 0)
        {
            return new TemperatureReading(0, 0, 0);
        }

        return new TemperatureReading(sumMean / count, maxOut, minOut);
    }

    private static (double[] Mean, double[] Max, double[] Min) Normals(TemperatureArea area) => area switch
    {
        TemperatureArea.Standard => (TokyoMean, TokyoMax, TokyoMin),
        TemperatureArea.Cold => (SapporoMean, SapporoMax, SapporoMin),
        TemperatureArea.Warm => (NahaMean, NahaMax, NahaMin),
        _ => (TokyoMean, TokyoMax, TokyoMin),
    };

    /// <summary>
    /// 月別平年値（各月の中央＝15日付近を代表値とみなす）を日単位に線形補間する。
    /// 隣接する月の中央値の間を按分し、12月→1月は循環させる（季節の連続性を保つ）。
    /// </summary>
    private static double Interpolate(double[] monthly, DateOnly date)
    {
        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        // 月内の連続位置 [month-1, month)。月初=月index、月末≈月index+1。
        var position = (date.Month - 1) + (date.Day - 0.5) / daysInMonth;
        // 各月の代表値は月の中央（+0.5）にあるとみなし、アンカー基準へずらす。
        var anchor = position - 0.5;
        var lower = (int)Math.Floor(anchor);
        var frac = anchor - lower;
        var a = monthly[((lower % 12) + 12) % 12];
        var b = monthly[(((lower + 1) % 12) + 12) % 12];
        return a + (b - a) * frac;
    }
}
