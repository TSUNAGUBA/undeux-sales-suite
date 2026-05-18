using System.Text;

namespace UndeuxSales.Core.Parsing;

/// <summary>
/// SQL の <c>VALUES</c> 句（<c>(...),(...),...</c>）を走査し、各タプルのフィールド値リストに
/// 分解する低レベルスキャナ。文字列リテラル内のエスケープ・区切り文字を正しく扱う。
/// </summary>
public static class SqlTupleScanner
{
    // mysqldump が出力する Ctrl-Z（SUB, U+001A）のエスケープ \Z に対応する文字。
    private const char SubstituteChar = (char)0x1A;

    /// <summary>
    /// <paramref name="text"/> の <paramref name="start"/> 位置以降を走査し、各タプルの
    /// フィールド値を列挙する。文字列はアンエスケープ済み、<c>NULL</c> は <c>null</c>、
    /// 数値などのリテラルは文字列のまま返す。
    /// </summary>
    public static IEnumerable<List<string?>> ScanTuples(string text, int start = 0)
    {
        var i = start;
        var n = text.Length;

        while (i < n)
        {
            while (i < n && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
            if (i >= n || text[i] == ';') yield break;

            if (text[i] != '(')
            {
                throw new FormatException($"タプル開始の '(' が見つかりません（位置 {i}）。");
            }

            i++;
            var fields = new List<string?>();

            while (true)
            {
                while (i < n && char.IsWhiteSpace(text[i])) i++;
                if (i >= n) throw new FormatException("タプルが閉じられていません。");

                if (text[i] == '\'')
                {
                    fields.Add(ScanQuotedString(text, ref i));
                }
                else
                {
                    var literalStart = i;
                    while (i < n && text[i] != ',' && text[i] != ')') i++;
                    if (i >= n) throw new FormatException("タプルが閉じられていません。");

                    var literal = text.Substring(literalStart, i - literalStart).Trim();
                    fields.Add(literal.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : literal);
                }

                while (i < n && char.IsWhiteSpace(text[i])) i++;
                if (i >= n) throw new FormatException("タプルが閉じられていません。");

                if (text[i] == ',')
                {
                    i++;
                    continue;
                }

                if (text[i] == ')')
                {
                    i++;
                    break;
                }

                throw new FormatException(
                    $"タプル内に想定外の文字 '{text[i]}' があります（位置 {i}）。");
            }

            yield return fields;
        }
    }

    private static string ScanQuotedString(string text, ref int i)
    {
        i++; // 開きクォートを消費
        var sb = new StringBuilder();
        var n = text.Length;

        while (i < n)
        {
            var c = text[i];

            if (c == '\\')
            {
                if (i + 1 >= n) throw new FormatException("文字列リテラルが閉じられていません。");

                sb.Append(text[i + 1] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    'b' => '\b',
                    'Z' => SubstituteChar,
                    var other => other, // \\ \' \" および未知のエスケープ
                });
                i += 2;
            }
            else if (c == '\'')
            {
                // '' は1つの ' を表す（クォート二重化エスケープ）
                if (i + 1 < n && text[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i += 2;
                }
                else
                {
                    i++;
                    return sb.ToString();
                }
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        throw new FormatException("文字列リテラルが閉じられていません。");
    }
}
