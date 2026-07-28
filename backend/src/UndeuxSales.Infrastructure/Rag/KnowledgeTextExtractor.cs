using System.Text;
using UglyToad.PdfPig;

namespace UndeuxSales.Infrastructure.Rag;

/// <summary>ファイル抽出結果。RequiresAiDescription=true は画像（OCR 不可）で AI 説明が必要。</summary>
public sealed record ExtractedText(string Text, bool RequiresAiDescription);

/// <summary>
/// アップロードファイルからの AI 参照用テキスト抽出。
/// 対応拡張子: .txt / .md（UTF-8、失敗時 Shift_JIS フォールバック）、.pdf（PdfPig）、
/// .jpg / .jpeg / .png（テキスト抽出不可 → AI による内容説明の対象）。
/// </summary>
public static class KnowledgeTextExtractor
{
    /// <summary>許可する拡張子（小文字）。</summary>
    public static readonly IReadOnlyList<string> AllowedExtensions = new[]
    {
        ".txt", ".md", ".pdf", ".jpg", ".jpeg", ".png",
    };

    static KnowledgeTextExtractor()
    {
        // Shift_JIS（cp932）デコードに必要。複数回呼んでも安全。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool IsAllowed(string fileName) =>
        AllowedExtensions.Contains(GetExtension(fileName));

    public static bool IsImage(string fileName) =>
        GetExtension(fileName) is ".jpg" or ".jpeg" or ".png";

    /// <summary>拡張子から Content-Type を決める（ダウンロード応答用）。</summary>
    public static string ResolveContentType(string fileName) => GetExtension(fileName) switch
    {
        ".txt" => "text/plain; charset=utf-8",
        ".md" => "text/markdown; charset=utf-8",
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };

    /// <summary>画像の Vertex AI（Gemini）用 media type。</summary>
    public static string ResolveImageMediaType(string fileName) =>
        GetExtension(fileName) == ".png" ? "image/png" : "image/jpeg";

    /// <summary>
    /// ファイルからテキストを抽出する。抽出失敗（破損 PDF 等）は例外を送出する
    /// （呼出側が index_state=failed として登録は継続する）。
    /// </summary>
    public static ExtractedText Extract(string fileName, byte[] data)
    {
        var extension = GetExtension(fileName);
        return extension switch
        {
            ".txt" or ".md" => new ExtractedText(DecodeText(data), false),
            ".pdf" => new ExtractedText(ExtractPdf(data), false),
            ".jpg" or ".jpeg" or ".png" => new ExtractedText(string.Empty, true),
            _ => throw new NotSupportedException($"未対応の拡張子です: {extension}"),
        };
    }

    private static string GetExtension(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant();

    /// <summary>UTF-8（BOM 許容）優先で厳格デコードし、失敗時は Shift_JIS で読む。</summary>
    private static string DecodeText(byte[] data)
    {
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            var offset = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            return strictUtf8.GetString(data, offset, data.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(932).GetString(data);
        }
    }

    private static string ExtractPdf(byte[] data)
    {
        var builder = new StringBuilder();
        using var document = PdfDocument.Open(data);
        var pageNumber = 0;
        foreach (var page in document.GetPages())
        {
            pageNumber++;
            var text = (page.Text ?? string.Empty).Trim();
            builder.Append("\n\n<!-- page ").Append(pageNumber).Append(" -->\n");
            builder.Append(text);
        }

        return builder.ToString().Trim();
    }
}
