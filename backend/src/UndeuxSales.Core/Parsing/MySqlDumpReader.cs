using System.Globalization;
using UndeuxSales.Core.Models;

namespace UndeuxSales.Core.Parsing;

/// <summary>
/// mysqldump 形式のダンプから <c>sales</c> テーブルの <c>INSERT</c> 文を読み取り、
/// <see cref="SalesRecord"/> に変換する。初期DBダンプ取込で使用する。
/// </summary>
public static class MySqlDumpReader
{
    private const string InsertPrefix = "INSERT INTO `sales` VALUES ";

    /// <summary>ダンプ1行の列数（id 〜 updated_at の 37 列）。</summary>
    public const int ExpectedFieldCount = 37;

    /// <summary>
    /// ダンプを行単位で読み、<c>sales</c> の INSERT 文に含まれる全レコードを遅延列挙する。
    /// </summary>
    public static IEnumerable<SalesRecord> ReadRecords(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!line.StartsWith(InsertPrefix, StringComparison.Ordinal)) continue;

            foreach (var fields in SqlTupleScanner.ScanTuples(line, InsertPrefix.Length))
            {
                yield return MapToRecord(fields);
            }
        }
    }

    /// <summary>ダンプ1タプル（37列）を <see cref="SalesRecord"/> に変換する。</summary>
    public static SalesRecord MapToRecord(IReadOnlyList<string?> f)
    {
        if (f.Count != ExpectedFieldCount)
        {
            throw new FormatException(
                $"INSERT 文の列数が想定（{ExpectedFieldCount}）と異なります: 実際 {f.Count}。");
        }

        // 列順: 0:id 1:import_date 2:customer_code 3:gyotai_code 4:chohyo_kubun_name
        //       5:department 6:hinban_code 7:tanpin_code 8:hinmei 9:shohin_kigou
        //       10:color 11:size 12:tanawari1 13:tanawari2 14-20:toshu_uriage_count1-7
        //       21:zenshu 22-24:2-4shumae 25:zaikosu 26:ruikei_uriage 27:ruikei_nohin
        //       28:hatchu_count 29:donyu_date 30:zainiti 31:genka 32:baika 33:kisetsu
        //       34:sakizuke_count 35:created_at 36:updated_at
        return new SalesRecord
        {
            ImportDate = ParseDate(f[1], "import_date"),
            CustomerCode = ReqText(f[2], "customer_code"),
            GyotaiCode = ReqText(f[3], "gyotai_code"),
            ChohyoKubunName = ReqText(f[4], "chohyo_kubun_name"),
            Department = ReqText(f[5], "department"),
            HinbanCode = ReqText(f[6], "hinban_code"),
            TanpinCode = ReqText(f[7], "tanpin_code"),
            Hinmei = ReqText(f[8], "hinmei"),
            ShohinKigou = ReqText(f[9], "shohin_kigou"),
            Color = ReqText(f[10], "color"),
            Size = ReqText(f[11], "size"),
            Tanawari1 = f[12],
            Tanawari2 = f[13],
            ToshuUriageCount1 = ParseInt(f[14], "toshu_uriage_count1"),
            ToshuUriageCount2 = ParseInt(f[15], "toshu_uriage_count2"),
            ToshuUriageCount3 = ParseInt(f[16], "toshu_uriage_count3"),
            ToshuUriageCount4 = ParseInt(f[17], "toshu_uriage_count4"),
            ToshuUriageCount5 = ParseInt(f[18], "toshu_uriage_count5"),
            ToshuUriageCount6 = ParseInt(f[19], "toshu_uriage_count6"),
            ToshuUriageCount7 = ParseInt(f[20], "toshu_uriage_count7"),
            UriageCountZenshu = ParseInt(f[21], "uriage_count_zenshu"),
            UriageCount2Shumae = ParseInt(f[22], "uriage_count_2shumae"),
            UriageCount3Shumae = ParseInt(f[23], "uriage_count_3shumae"),
            UriageCount4Shumae = ParseInt(f[24], "uriage_count_4shumae"),
            Zaikosu = ParseInt(f[25], "zaikosu"),
            RuikeiUriageCount = ParseInt(f[26], "ruikei_uriage_count"),
            RuikeiNohinCount = ParseInt(f[27], "ruikei_nohin_count"),
            HatchuCount = ParseDecimal(f[28], "hatchu_count"),
            DonyuDate = ReqText(f[29], "donyu_date"),
            Zainiti = ParseInt(f[30], "zainiti"),
            Genka = ParseInt(f[31], "genka"),
            Baika = ParseInt(f[32], "baika"),
            Kisetsu = ReqText(f[33], "kisetsu"),
            SakizukeCount = ParseInt(f[34], "sakizuke_count"),
            SourceCreatedAt = ParseNullableTimestamp(f[35]),
            SourceUpdatedAt = ParseNullableTimestamp(f[36]),
        };
    }

    private static string ReqText(string? value, string column)
        => value ?? throw new FormatException($"列 {column} は NULL 不可です。");

    private static DateOnly ParseDate(string? value, string column)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new FormatException($"列 {column} の日付が空です。");
        }

        return DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static int ParseInt(string? value, string column)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"列 {column} の数値 '{value}' を解析できません。");
    }

    private static decimal ParseDecimal(string? value, string column)
    {
        if (string.IsNullOrEmpty(value)) return 0m;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"列 {column} の数値 '{value}' を解析できません。");
    }

    private static DateTime? ParseNullableTimestamp(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None);
    }
}
