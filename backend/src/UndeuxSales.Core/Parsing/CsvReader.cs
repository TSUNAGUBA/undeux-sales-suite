using System.Text;

namespace UndeuxSales.Core.Parsing;

/// <summary>
/// RFC 4180 に準拠した最小限のCSVリーダー。
/// ダブルクォート括り・<c>""</c> エスケープ・CRLF/LF 改行に対応する。
/// </summary>
public static class CsvReader
{
    /// <summary>
    /// CSVテキストを行（フィールド文字列配列）の列として読み出す。
    /// 完全な空行はスキップする。
    /// </summary>
    public static IEnumerable<string[]> ReadRows(TextReader reader)
    {
        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        var sawAnyChar = false;
        int ch;

        while ((ch = reader.Read()) != -1)
        {
            var c = (char)ch;

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    sawAnyChar = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    sawAnyChar = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (sawAnyChar)
                    {
                        yield return row.ToArray();
                    }

                    row.Clear();
                    sawAnyChar = false;
                    break;
                default:
                    field.Append(c);
                    sawAnyChar = true;
                    break;
            }
        }

        if (sawAnyChar || field.Length > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }
}
