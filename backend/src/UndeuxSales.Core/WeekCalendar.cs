namespace UndeuxSales.Core;

/// <summary>
/// 週次売上参照ファイルの日付ロジック。
/// <para>
/// 取込日（<c>import_date</c>）は月曜日。ファイルには取込日の前日を基準に特定した
/// 1週間（月〜日）のデータが含まれる。日次列のインデックス 1..7 は
/// 取込日の前週 月〜日（取込日-7 〜 取込日-1）に対応する。
/// </para>
/// </summary>
public static class WeekCalendar
{
    /// <summary>1週間の日数。</summary>
    public const int DaysInWeek = 7;

    /// <summary>
    /// 日次列のインデックス（1=月 .. 7=日）から実日付を求める。
    /// </summary>
    /// <param name="importDate">取込日（月曜日）。</param>
    /// <param name="dayIndex">日次インデックス（1〜7）。</param>
    public static DateOnly DailyDate(DateOnly importDate, int dayIndex)
    {
        if (dayIndex is < 1 or > DaysInWeek)
        {
            throw new ArgumentOutOfRangeException(nameof(dayIndex), dayIndex,
                $"dayIndex は 1〜{DaysInWeek} の範囲で指定してください。");
        }

        // dayIndex 1 → importDate-7（月） / dayIndex 7 → importDate-1（日）
        return importDate.AddDays(-DaysInWeek - 1 + dayIndex);
    }

    /// <summary>取込日が月曜日かどうかを判定する。</summary>
    public static bool IsValidImportDate(DateOnly importDate)
        => importDate.DayOfWeek == DayOfWeek.Monday;

    /// <summary>
    /// 取込日に対応する売上対象週の期間（月曜〜日曜）を返す。
    /// </summary>
    public static (DateOnly Start, DateOnly End) WeekRange(DateOnly importDate)
        => (importDate.AddDays(-DaysInWeek), importDate.AddDays(-1));
}
