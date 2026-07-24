using System.Text;
using System.Text.RegularExpressions;

namespace UndeuxSales.Core.Rag;

/// <summary>
/// ナレッジ本文のチャンク分割（決定的・外部依存なし）。
/// <para>
/// Markdown 見出し・ページマーカー（&lt;!-- page N --&gt;）・空行段落を境界として
/// 意味的なまとまりを保ちながら、目標サイズ前後のチャンクへ分割する。
/// 同一入力からは常に同一のチャンク列が得られる（再インデックスの冪等性）。
/// </para>
/// </summary>
public static partial class RagChunker
{
    /// <summary>チャンクの目標文字数。これを超えたら次のチャンクへ切り替える。</summary>
    public const int TargetChars = 900;

    /// <summary>チャンクの上限文字数。単一段落がこれを超える場合は文境界で強制分割する。</summary>
    public const int MaxChars = 1400;

    /// <summary>文脈欠落防止のため前チャンク末尾から引き継ぐ文字数の上限。</summary>
    public const int OverlapChars = 120;

    [GeneratedRegex(@"^<!--\s*page\s+\d+\s*-->\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageMarkerRegex();

    [GeneratedRegex(@"^#{1,6}\s")]
    private static partial Regex HeadingRegex();

    /// <summary>本文をチャンク列に分割する。空・空白のみの入力は空列を返す。</summary>
    public static IReadOnlyList<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var blocks = SplitIntoBlocks(text);
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var block in blocks)
        {
            foreach (var piece in SplitOversizedBlock(block))
            {
                // 現在チャンクに足すと目標超過となる場合はチャンクを確定する。
                if (current.Length > 0 && current.Length + piece.Length + 1 > TargetChars)
                {
                    Flush(chunks, current);
                }

                if (current.Length > 0)
                {
                    current.Append('\n');
                }

                current.Append(piece);

                if (current.Length >= MaxChars)
                {
                    Flush(chunks, current);
                }
            }
        }

        Flush(chunks, current);
        return ApplyOverlap(chunks);
    }

    private static void Flush(List<string> chunks, StringBuilder current)
    {
        var value = current.ToString().Trim();
        if (value.Length > 0)
        {
            chunks.Add(value);
        }

        current.Clear();
    }

    /// <summary>見出し・ページマーカー・空行を境界に段落ブロックへ分解する（マーカー行自体は除去）。</summary>
    private static List<string> SplitIntoBlocks(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<string>();
        var current = new StringBuilder();

        void FlushBlock()
        {
            var value = current.ToString().Trim();
            if (value.Length > 0)
            {
                blocks.Add(value);
            }

            current.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (PageMarkerRegex().IsMatch(line) || line.Length == 0)
            {
                FlushBlock();
                continue;
            }

            if (HeadingRegex().IsMatch(line))
            {
                FlushBlock();
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(line);
        }

        FlushBlock();
        return blocks;
    }

    /// <summary>上限超過ブロックを文境界（。！？改行）で上限以下の断片に分割する。</summary>
    private static IEnumerable<string> SplitOversizedBlock(string block)
    {
        if (block.Length <= MaxChars)
        {
            yield return block;
            yield break;
        }

        var start = 0;
        while (start < block.Length)
        {
            var remaining = block.Length - start;
            if (remaining <= MaxChars)
            {
                yield return block[start..].Trim();
                yield break;
            }

            var window = block.Substring(start, MaxChars);
            var cut = LastSentenceBoundary(window);
            if (cut < MaxChars / 2)
            {
                // 文境界が見つからない（表など）場合は上限で機械的に切る。
                cut = MaxChars;
            }

            yield return block.Substring(start, cut).Trim();
            start += cut;
        }
    }

    private static int LastSentenceBoundary(string window)
    {
        for (var i = window.Length - 1; i >= 0; i--)
        {
            var c = window[i];
            if (c is '。' or '！' or '？' or '\n')
            {
                return i + 1;
            }
        }

        return -1;
    }

    /// <summary>隣接チャンク間で前チャンク末尾を引き継ぎ、文脈の欠落を防ぐ。</summary>
    private static IReadOnlyList<string> ApplyOverlap(List<string> chunks)
    {
        if (chunks.Count <= 1)
        {
            return chunks;
        }

        var result = new List<string>(chunks.Count) { chunks[0] };
        for (var i = 1; i < chunks.Count; i++)
        {
            var prev = chunks[i - 1];
            var tail = prev.Length <= OverlapChars ? prev : prev[^OverlapChars..];
            // 引き継ぎ部分は文の途中から始まらないよう、最初の文境界以降を採用する。
            var boundary = tail.IndexOfAny(new[] { '。', '！', '？', '\n' });
            if (boundary >= 0 && boundary + 1 < tail.Length)
            {
                tail = tail[(boundary + 1)..];
            }

            tail = tail.Trim();
            result.Add(tail.Length > 0 ? $"…{tail}\n{chunks[i]}" : chunks[i]);
        }

        return result;
    }
}
