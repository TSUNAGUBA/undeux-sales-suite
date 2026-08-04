using System.Text.Json;

namespace UndeuxSales.Core.SubsidiaryCheck.ManualCheck;

/// <summary>AI 抽出応答の解析結果（失敗は例外ではなく戻り値で表現する・原則4）。</summary>
/// <param name="Success">項目を1件以上解析できたか。</param>
/// <param name="Extraction">正規化済みの抽出結果（失敗時は空）。</param>
/// <param name="Error">失敗理由（成功時は null）。</param>
public sealed record ManualCheckParseResult(
    bool Success,
    ManualCheckExtraction Extraction,
    string? Error)
{
    public static ManualCheckParseResult Ok(ManualCheckExtraction extraction) =>
        new(true, extraction, null);

    public static ManualCheckParseResult Failure(string error) =>
        new(false, new ManualCheckExtraction(Array.Empty<ExtractedField>()), error);
}

/// <summary>
/// タグ読取 AI 応答テキスト → 抽出値（fields）の解析。
/// <para>
/// 出力契約（JSON のみ）に違反した応答にも耐えるよう、コードフェンス・前後の説明文を許容して
/// 最初のバランスした JSON オブジェクト（<see cref="JsonBlockExtractor"/>）を抽出し、
/// 大文字小文字非依存でデシリアライズする。readable 未指定は「読めた」寄りに倒さず、
/// text/items の有無から判定する（安全側）。
/// </para>
/// </summary>
public static class ManualCheckResponseParser
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>応答テキストを解析し、正規化済みの抽出結果を返す。</summary>
    public static ManualCheckParseResult Parse(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return ManualCheckParseResult.Failure("AI 応答が空です。");
        }

        var json = JsonBlockExtractor.ExtractFirstJsonObject(responseText);
        if (json is null)
        {
            return ManualCheckParseResult.Failure("AI 応答から JSON オブジェクトを抽出できませんでした。");
        }

        ParsedResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ParsedResponse>(json, DeserializeOptions);
        }
        catch (JsonException ex)
        {
            return ManualCheckParseResult.Failure($"AI 応答の JSON を解析できませんでした: {ex.Message}");
        }

        if (parsed?.Fields is not { Count: > 0 })
        {
            return ManualCheckParseResult.Failure("AI 応答に fields が含まれていません。");
        }

        var fields = parsed.Fields
            .Where(f => !string.IsNullOrWhiteSpace(f.Key))
            .Select(NormalizeField)
            .ToList();

        return fields.Count == 0
            ? ManualCheckParseResult.Failure("AI 応答に有効な項目（key のある fields）がありません。")
            : ManualCheckParseResult.Ok(new ManualCheckExtraction(fields));
    }

    private static ExtractedField NormalizeField(ParsedField field)
    {
        var text = string.IsNullOrWhiteSpace(field.Text) ? null : field.Text!.Trim();
        var items = (field.Items ?? new List<string?>())
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i!.Trim())
            .ToList();

        var hasContent = text is not null || items.Count > 0;
        // readable が明示されていればそれを尊重しつつ、内容が空なら false へ倒す（安全側）。
        var readable = (field.Readable ?? hasContent) && hasContent;

        return new ExtractedField(field.Key!.Trim(), text, items, readable);
    }

    /// <summary>デシリアライズ用の中間 DTO（AI 出力の揺れを許容するため全フィールド nullable）。</summary>
    private sealed record ParsedResponse(List<ParsedField>? Fields);

    private sealed record ParsedField(
        string? Key,
        string? Text,
        List<string?>? Items,
        bool? Readable);
}
