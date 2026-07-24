using System.Text;

namespace UndeuxSales.Core.Rag;

/// <summary>
/// ローカル決定的埋め込み（文字 n-gram ハッシュベクトル）。実装 SoT。
/// <para>
/// 外部の埋め込み API に依存せず、日本語テキストの字句的な類似性をベクトル空間で
/// 表現する。文字バイグラム＋トライグラムを特徴量とし、feature hashing（符号付き）で
/// 固定次元へ射影、sublinear TF（1+log）で重み付けし L2 正規化する。
/// 同一入力からは常に同一のベクトルが得られる（再インデックスの冪等性・ADR-012）。
/// </para>
/// <para>
/// knowledge.chunk_embedding は (chunk_id, model) キーのため、将来外部埋め込みモデル
/// （pgvector 等）を導入しても本モデルと共存・段階移行できる（DD-04 §3.3）。
/// </para>
/// </summary>
public static class HashingVectorizer
{
    /// <summary>埋め込みモデル識別子（knowledge.chunk_embedding.model に格納）。</summary>
    public const string ModelName = "local-hash-v1";

    /// <summary>ベクトル次元数。</summary>
    public const int Dim = 384;

    /// <summary>テキストを L2 正規化済みベクトルへ変換する。空入力はゼロベクトル。</summary>
    public static float[] Embed(string? text)
    {
        var vector = new float[Dim];
        var normalized = NormalizeText(text);
        if (normalized.Length == 0)
        {
            return vector;
        }

        var counts = new Dictionary<ulong, int>();
        CountNgrams(normalized, 2, counts);
        CountNgrams(normalized, 3, counts);

        foreach (var (hash, count) in counts)
        {
            var index = (int)((hash >> 1) % Dim);
            var sign = (hash & 1UL) == 0UL ? 1f : -1f;
            var weight = 1f + MathF.Log(count);
            vector[index] += sign * weight;
        }

        return L2Normalize(vector);
    }

    /// <summary>コサイン類似度（検証・テスト用。検索時は SQL 側 knowledge.cosine_similarity を使用）。</summary>
    public static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        var n = Math.Min(a.Count, b.Count);
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
        }

        for (var i = 0; i < a.Count; i++)
        {
            na += a[i] * a[i];
        }

        for (var i = 0; i < b.Count; i++)
        {
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>NFKC 正規化＋小文字化＋空白圧縮（全角/半角・大文字小文字の表記揺れを吸収）。</summary>
    internal static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var lastWasSpace = true;
        foreach (var c in normalized)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                builder.Append(c);
                lastWasSpace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static void CountNgrams(string text, int n, Dictionary<ulong, int> counts)
    {
        if (text.Length < n)
        {
            // n-gram が取れない短文は全体を1特徴として扱う。
            var h = Fnv1a(text.AsSpan(), n);
            counts[h] = counts.TryGetValue(h, out var c) ? c + 1 : 1;
            return;
        }

        for (var i = 0; i + n <= text.Length; i++)
        {
            var span = text.AsSpan(i, n);
            // 空白のみの n-gram は情報を持たないため除外する。
            if (span.IsWhiteSpace())
            {
                continue;
            }

            var hash = Fnv1a(span, n);
            counts[hash] = counts.TryGetValue(hash, out var c) ? c + 1 : 1;
        }
    }

    /// <summary>FNV-1a 64bit（n をシードに混ぜ、同一文字列でも n-gram 長別に別特徴とする）。</summary>
    private static ulong Fnv1a(ReadOnlySpan<char> span, int n)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis ^ (ulong)n;
        foreach (var c in span)
        {
            hash ^= c;
            hash *= prime;
        }

        return hash;
    }

    private static float[] L2Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var v in vector)
        {
            sum += v * v;
        }

        if (sum <= 0)
        {
            return vector;
        }

        var norm = (float)Math.Sqrt(sum);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }

        return vector;
    }
}
