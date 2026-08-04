namespace UndeuxSales.Core.SubsidiaryCheck;

/// <summary>
/// AI 応答テキストから最初のバランスした JSON オブジェクトを抽出する共有ロジック。
/// <para>
/// 出力契約（JSON のみ）に違反してコードフェンスや前後の説明文が付いた応答にも耐えるよう、
/// 文字列リテラル内の波括弧・エスケープを考慮して最初の <c>{...}</c> を切り出す。
/// 副資材チェック（findings 抽出）と手入力チェック（タグ項目抽出）の両パーサで共用する（原則3）。
/// </para>
/// </summary>
public static class JsonBlockExtractor
{
    /// <summary>
    /// テキスト中の最初のバランスした JSON オブジェクトを抽出する。見つからなければ null。
    /// 文字列リテラル内の波括弧・エスケープを考慮する（コードフェンスや前後の説明文は読み飛ばされる）。
    /// </summary>
    public static string? ExtractFirstJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text.Substring(start, i - start + 1);
                    }

                    break;
            }
        }

        return null;
    }
}
