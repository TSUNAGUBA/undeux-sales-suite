using System.Globalization;
using UndeuxSales.Core.Models;

namespace UndeuxSales.Core.Parsing;

/// <summary>週次CSV取込における1行のエラー。</summary>
/// <param name="RowNumber">ファイル先頭からの行番号（ヘッダー行を1とする）。</param>
/// <param name="Message">エラー内容。</param>
public sealed record CsvRowError(int RowNumber, string Message);

/// <summary>週次CSVの解析結果。</summary>
public sealed class CsvParseResult
{
    /// <summary>正常に解析できたレコード。</summary>
    public List<SalesRecord> Records { get; } = new();

    /// <summary>行単位のエラー。</summary>
    public List<CsvRowError> Errors { get; } = new();

    /// <summary>エラー行が1件以上あるかどうか。</summary>
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// 週次売上参照CSVを解析し <see cref="SalesRecord"/> に変換する。
/// ヘッダー行は列名（snake_case。DBカラム名に一致）で構成する。
/// 構造的エラー（ヘッダー欠落・必須列不足）は <see cref="AppException"/> を送出し、
/// 行単位のエラーは <see cref="CsvParseResult.Errors"/> に蓄積する。
/// </summary>
public static class SalesCsvReader
{
    /// <summary>ヘッダーに必須の列名。</summary>
    public static readonly IReadOnlyList<string> RequiredColumns = new[]
    {
        "import_date", "customer_code", "gyotai_code", "chohyo_kubun_name",
        "department", "hinban_code", "tanpin_code", "hinmei", "shohin_kigou",
        "color", "size",
        "toshu_uriage_count1", "toshu_uriage_count2", "toshu_uriage_count3",
        "toshu_uriage_count4", "toshu_uriage_count5", "toshu_uriage_count6",
        "toshu_uriage_count7",
        "uriage_count_zenshu", "uriage_count_2shumae", "uriage_count_3shumae",
        "uriage_count_4shumae",
        "zaikosu", "ruikei_uriage_count", "ruikei_nohin_count", "hatchu_count",
        "donyu_date", "zainiti", "genka", "baika", "kisetsu", "sakizuke_count",
    };

    // 業務キーを構成し、空であってはならない列。
    private static readonly string[] KeyTextColumns =
    {
        "customer_code", "gyotai_code", "hinban_code", "tanpin_code",
        "shohin_kigou", "donyu_date",
    };

    /// <summary>CSVを解析する。</summary>
    public static CsvParseResult Parse(TextReader reader)
    {
        using var rows = CsvReader.ReadRows(reader).GetEnumerator();

        if (!rows.MoveNext())
        {
            throw new AppException(ErrorCodes.ImportFileEmpty, 422);
        }

        var headerIndex = BuildHeaderIndex(rows.Current);

        var result = new CsvParseResult();
        var rowNumber = 1; // ヘッダーを1行目とする

        while (rows.MoveNext())
        {
            rowNumber++;
            try
            {
                result.Records.Add(MapRow(rows.Current, headerIndex));
            }
            catch (CsvRowException ex)
            {
                result.Errors.Add(new CsvRowError(rowNumber, ex.Message));
            }
        }

        if (result.Records.Count == 0 && result.Errors.Count == 0)
        {
            throw new AppException(ErrorCodes.ImportFileEmpty, 422);
        }

        return result;
    }

    private static Dictionary<string, int> BuildHeaderIndex(string[] header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            var name = header[i].Trim();
            if (name.Length > 0 && !map.ContainsKey(name))
            {
                map[name] = i;
            }
        }

        var missing = RequiredColumns.Where(c => !map.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            throw new AppException(ErrorCodes.ImportFormatInvalid, 422,
                $"必須列が不足しています: {string.Join(", ", missing)}");
        }

        return map;
    }

    private static SalesRecord MapRow(string[] cells, Dictionary<string, int> idx)
    {
        string Text(string column)
            => idx.TryGetValue(column, out var i) && i < cells.Length
                ? cells[i].Trim()
                : string.Empty;

        string? OptionalText(string column)
        {
            var value = Text(column);
            return value.Length == 0 ? null : value;
        }

        int Int(string column)
        {
            var value = Text(column);
            if (value.Length == 0) return 0;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
            {
                return r;
            }

            throw new CsvRowException($"列 {column} の数値 '{value}' が不正です。");
        }

        decimal Decimal(string column)
        {
            var value = Text(column);
            if (value.Length == 0) return 0m;
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var r))
            {
                return r;
            }

            throw new CsvRowException($"列 {column} の数値 '{value}' が不正です。");
        }

        DateTime? OptionalTimestamp(string column)
        {
            var value = Text(column);
            if (value.Length == 0) return null;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                return t;
            }

            throw new CsvRowException($"列 {column} の日時 '{value}' が不正です。");
        }

        var importDateText = Text("import_date");
        if (importDateText.Length == 0)
        {
            throw new CsvRowException("列 import_date は必須です。");
        }

        if (!DateOnly.TryParse(importDateText, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var importDate))
        {
            throw new CsvRowException(
                $"列 import_date の日付 '{importDateText}' が不正です（YYYY-MM-DD 形式で指定してください）。");
        }

        if (!WeekCalendar.IsValidImportDate(importDate))
        {
            throw new CsvRowException(
                $"列 import_date '{importDate:yyyy-MM-dd}' は月曜日ではありません。取込日は月曜日である必要があります。");
        }

        foreach (var keyColumn in KeyTextColumns)
        {
            if (Text(keyColumn).Length == 0)
            {
                throw new CsvRowException($"列 {keyColumn} は必須です（業務キーの構成要素）。");
            }
        }

        return new SalesRecord
        {
            ImportDate = importDate,
            CustomerCode = Text("customer_code"),
            GyotaiCode = Text("gyotai_code"),
            ChohyoKubunName = Text("chohyo_kubun_name"),
            Department = Text("department"),
            HinbanCode = Text("hinban_code"),
            TanpinCode = Text("tanpin_code"),
            Hinmei = Text("hinmei"),
            ShohinKigou = Text("shohin_kigou"),
            Color = Text("color"),
            Size = Text("size"),
            Tanawari1 = OptionalText("tanawari1"),
            Tanawari2 = OptionalText("tanawari2"),
            ToshuUriageCount1 = Int("toshu_uriage_count1"),
            ToshuUriageCount2 = Int("toshu_uriage_count2"),
            ToshuUriageCount3 = Int("toshu_uriage_count3"),
            ToshuUriageCount4 = Int("toshu_uriage_count4"),
            ToshuUriageCount5 = Int("toshu_uriage_count5"),
            ToshuUriageCount6 = Int("toshu_uriage_count6"),
            ToshuUriageCount7 = Int("toshu_uriage_count7"),
            UriageCountZenshu = Int("uriage_count_zenshu"),
            UriageCount2Shumae = Int("uriage_count_2shumae"),
            UriageCount3Shumae = Int("uriage_count_3shumae"),
            UriageCount4Shumae = Int("uriage_count_4shumae"),
            Zaikosu = Int("zaikosu"),
            RuikeiUriageCount = Int("ruikei_uriage_count"),
            RuikeiNohinCount = Int("ruikei_nohin_count"),
            HatchuCount = Decimal("hatchu_count"),
            DonyuDate = Text("donyu_date"),
            Zainiti = Int("zainiti"),
            Genka = Int("genka"),
            Baika = Int("baika"),
            Kisetsu = Text("kisetsu"),
            SakizukeCount = Int("sakizuke_count"),
            SourceCreatedAt = OptionalTimestamp("created_at"),
            SourceUpdatedAt = OptionalTimestamp("updated_at"),
        };
    }

    /// <summary>行単位の解析エラーを表す内部例外。</summary>
    private sealed class CsvRowException : Exception
    {
        public CsvRowException(string message) : base(message)
        {
        }
    }
}
