using System.Globalization;

namespace UndeuxSales.Core.Parsing;

/// <summary>気温日次CSV（db/climate_daily.csv）の1行。<paramref name="AreaCode"/> は standard/cold/warm。</summary>
/// <param name="AreaCode">エリア種別の DB 表現（standard=東京 / cold=札幌 / warm=那覇）。</param>
/// <param name="Date">観測日。</param>
/// <param name="TempAvg">日平均気温（℃）。</param>
/// <param name="TempMax">日最高気温（℃）。</param>
/// <param name="TempMin">日最低気温（℃）。</param>
public sealed record ClimateDailyRecord(
    string AreaCode, DateOnly Date, double TempAvg, double TempMax, double TempMin);

/// <summary>
/// 気温日次CSV（<c>db/climate_daily.csv</c>）を読み出す。
/// <para>
/// 形式: <c>年月日,曜日,平均気温(℃),最高気温(℃),最低気温(℃),エリア</c><br/>
/// 例: <c>2024年1月1日,月,9.2,12.5,4.4,東京</c>
/// </para>
/// <para>
/// エリア（東京/札幌/那覇）は <see cref="ClimateModel"/> のエリア種別（standard/cold/warm）へ対応付ける。
/// 既知3地点以外のエリア行は無視する（前方互換）。日付・気温が解析不能な行は <see cref="FormatException"/> を送出する
/// （投入側はこれを捕捉し、非ブロッキングに退避する想定）。RFC4180 の解析は <see cref="CsvReader"/> を再利用する。
/// </para>
/// </summary>
public static class ClimateCsvReader
{
    private const int ColumnCount = 6;

    /// <summary>CSVテキストを気温日次レコードの列として読み出す。</summary>
    public static IEnumerable<ClimateDailyRecord> ReadRecords(TextReader reader)
    {
        var lineNumber = 0;
        foreach (var row in CsvReader.ReadRows(reader))
        {
            lineNumber++;

            // ヘッダ行（1行目が "年月日" 始まり）はスキップする。
            if (lineNumber == 1 && row.Length > 0
                && row[0].Trim().TrimStart('\uFEFF') == "年月日")
            {
                continue;
            }

            if (row.Length < ColumnCount)
            {
                throw new FormatException(
                    $"気温CSVの {lineNumber} 行目の列数が不足しています（{row.Length} 列、期待 {ColumnCount} 列）。");
            }

            // 既知3地点以外のエリアは無視する（前方互換: 大阪等が追加されても落とさない）。
            var area = ClimateModel.AreaForCity(row[5]);
            if (area is null)
            {
                continue;
            }

            yield return new ClimateDailyRecord(
                ClimateModel.AreaCode(area.Value),
                ParseJapaneseDate(row[0], lineNumber),
                ParseTemp(row[2], lineNumber, "平均気温"),
                ParseTemp(row[3], lineNumber, "最高気温"),
                ParseTemp(row[4], lineNumber, "最低気温"));
        }
    }

    /// <summary>"2024年1月1日" 形式を <see cref="DateOnly"/> に解析する。</summary>
    private static DateOnly ParseJapaneseDate(string value, int lineNumber)
    {
        var text = value.Trim().TrimStart('\uFEFF');
        var yearIdx = text.IndexOf('年');
        var monthIdx = text.IndexOf('月');
        var dayIdx = text.IndexOf('日');
        if (yearIdx > 0 && monthIdx > yearIdx && dayIdx > monthIdx
            && int.TryParse(text[..yearIdx], out var year)
            && int.TryParse(text[(yearIdx + 1)..monthIdx], out var month)
            && int.TryParse(text[(monthIdx + 1)..dayIdx], out var day)
            && year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day is >= 1 and <= 31)
        {
            try
            {
                return new DateOnly(year, month, day);
            }
            catch (ArgumentOutOfRangeException)
            {
                // 月内日数を超える日（例: 2月30日）など。
            }
        }

        throw new FormatException(
            $"気温CSVの {lineNumber} 行目の日付 '{value}' を解析できません（期待形式: YYYY年M月D日）。");
    }

    /// <summary>気温文字列（"9.2" / "11" / "-3.2"）を double に解析する。</summary>
    private static double ParseTemp(string value, int lineNumber, string field)
    {
        if (double.TryParse(
                value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var temp))
        {
            return temp;
        }

        throw new FormatException(
            $"気温CSVの {lineNumber} 行目の{field} '{value}' を解析できません。");
    }
}
