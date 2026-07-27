using System.Text.Json;

namespace UndeuxSales.Core.SubsidiaryCheck;

/// <summary>AI 応答の解析結果。失敗は例外ではなく戻り値で表現する（呼出側が failed 記録に変換する。原則4）。</summary>
/// <param name="Success">有効な指摘を1件以上解析できたか。</param>
/// <param name="Findings">正規化済みの指摘（失敗時は空）。</param>
/// <param name="Error">失敗理由（成功時は null）。</param>
public sealed record SubsidiaryCheckParseResult(
    bool Success,
    IReadOnlyList<SubsidiaryCheckFinding> Findings,
    string? Error)
{
    public static SubsidiaryCheckParseResult Ok(IReadOnlyList<SubsidiaryCheckFinding> findings) =>
        new(true, findings, null);

    public static SubsidiaryCheckParseResult Failure(string error) =>
        new(false, Array.Empty<SubsidiaryCheckFinding>(), error);
}

/// <summary>
/// AI 応答テキスト → 指摘（findings）の解析。
/// <para>
/// 出力契約（JSON のみ）に違反した応答にも耐えるよう、コードフェンス・前後の説明文を許容して
/// 最初のバランスした JSON オブジェクトを抽出し、大文字小文字非依存でデシリアライズする。
/// 未知の category は 'content'、未知の severity は 'warn' に正規化し（安全側＝要確認へ倒す）、
/// title / detail が空の指摘は除外する。
/// </para>
/// </summary>
public static class SubsidiaryCheckResponseParser
{
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>応答テキストを解析し、正規化済みの指摘を返す。</summary>
    public static SubsidiaryCheckParseResult Parse(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return SubsidiaryCheckParseResult.Failure("AI 応答が空です。");
        }

        var json = ExtractFirstJsonObject(responseText);
        if (json is null)
        {
            return SubsidiaryCheckParseResult.Failure("AI 応答から JSON オブジェクトを抽出できませんでした。");
        }

        ParsedResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ParsedResponse>(json, DeserializeOptions);
        }
        catch (JsonException ex)
        {
            return SubsidiaryCheckParseResult.Failure($"AI 応答の JSON を解析できませんでした: {ex.Message}");
        }

        if (parsed?.Findings is not { Count: > 0 })
        {
            return SubsidiaryCheckParseResult.Failure("AI 応答に findings が含まれていません。");
        }

        var findings = parsed.Findings
            .Where(f => !string.IsNullOrWhiteSpace(f.Title) && !string.IsNullOrWhiteSpace(f.Detail))
            .Select(f => new SubsidiaryCheckFinding(
                NormalizeCategory(f.Category),
                NormalizeSeverity(f.Severity),
                f.Title!.Trim(),
                f.Detail!.Trim(),
                NullIfBlank(f.Suggestion),
                NullIfBlank(f.Evidence)))
            .ToList();

        return findings.Count == 0
            ? SubsidiaryCheckParseResult.Failure("AI 応答に有効な指摘（title / detail のある findings）がありません。")
            : SubsidiaryCheckParseResult.Ok(findings);
    }

    /// <summary>
    /// テキスト中の最初のバランスした JSON オブジェクトを抽出する。
    /// 文字列リテラル内の波括弧・エスケープを考慮する（コードフェンスや前後の説明文はそのまま読み飛ばされる）。
    /// </summary>
    private static string? ExtractFirstJsonObject(string text)
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

    private static string NormalizeCategory(string? category)
    {
        var normalized = (category ?? string.Empty).Trim().ToLowerInvariant();
        return SubsidiaryCheckCategory.IsKnown(normalized) ? normalized : SubsidiaryCheckCategory.Content;
    }

    private static string NormalizeSeverity(string? severity)
    {
        var normalized = (severity ?? string.Empty).Trim().ToLowerInvariant();
        return SubsidiaryCheckSeverity.IsKnown(normalized) ? normalized : SubsidiaryCheckSeverity.Warn;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>デシリアライズ用の中間 DTO（AI 出力の揺れを許容するため全フィールド nullable）。</summary>
    private sealed record ParsedResponse(List<ParsedFinding>? Findings);

    private sealed record ParsedFinding(
        string? Category,
        string? Severity,
        string? Title,
        string? Detail,
        string? Suggestion,
        string? Evidence);
}
