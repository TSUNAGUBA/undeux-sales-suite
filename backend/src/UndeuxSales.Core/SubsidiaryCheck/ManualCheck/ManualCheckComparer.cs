using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UndeuxSales.Core.SubsidiaryCheck.ManualCheck;

/// <summary>手入力チェックの突合結果（項目結果＋集計＋総合判定）。</summary>
public sealed record ManualCheckComparison(
    IReadOnlyList<ManualFieldResult> Results,
    int FieldCount,
    int MatchCount,
    int MismatchCount,
    int WarnCount,
    string Judgment);

/// <summary>
/// 手入力値（正）とタグ画像からの AI 抽出値を項目単位で突き合わせる純粋ロジック。
/// <para>
/// 基本は文字列突合（<see cref="CompareText"/>）。組成は割合つきの成分突合
/// （<see cref="CompareRatio"/>）、洗濯表示はアイコンコードの<b>並び順を含む</b>列突合
/// （<see cref="CompareCareIcon"/>）という特殊比較を行う。文字列正規化は NFKC（全角→半角・
/// 互換文字の統一）＋空白除去＋大文字化で、全角数字「１１０」と「110」等の表記揺れを吸収する。
/// </para>
/// <para>
/// AI が判読できなかった項目（<see cref="ExtractedField.Readable"/>=false）・抽出結果に存在しない項目は、
/// 相違（mismatch）ではなく要確認（warn/missing）へ倒す（AI の読取失敗を相違と断じない。
/// 既存副資材チェックの「warn=目視確認」方針と同じ・原則4）。最終判断は担当者が行う前提の補助。
/// </para>
/// </summary>
public static class ManualCheckComparer
{
    /// <summary>組成の各行から末尾の割合（例:「綿 75%」）を分離する正規表現（NFKC 済み前提）。</summary>
    private static readonly Regex RatioPattern = new(
        @"^\s*(?<material>.*?)[\s:：=]*(?<percent>\d+(?:\.\d+)?)\s*%?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>手入力とタグ抽出を突き合わせ、項目結果・集計・総合判定を返す。</summary>
    public static ManualCheckComparison Compare(ManualCheckInput input, ManualCheckExtraction extraction)
    {
        var extractedByKey = new Dictionary<string, ExtractedField>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in extraction.Fields)
        {
            // 同一キーが重複した場合は最初の1件を採用する（AI 応答の揺れに対する防御）。
            if (!string.IsNullOrWhiteSpace(field.Key) && !extractedByKey.ContainsKey(field.Key.Trim()))
            {
                extractedByKey[field.Key.Trim()] = field;
            }
        }

        var results = new List<ManualFieldResult>();
        foreach (var field in input.Fields)
        {
            // 入力が空の項目は突合対象にしない（任意項目の未入力は「検査していない」扱い）。
            if (!HasValue(field))
            {
                continue;
            }

            extractedByKey.TryGetValue(field.Key, out var extracted);
            results.Add(CompareField(field, extracted));
        }

        var match = results.Count(r => r.Verdict == ManualCheckVerdict.Match);
        var mismatch = results.Count(r => r.Verdict == ManualCheckVerdict.Mismatch);
        // missing（タグに見当たらず）は要確認として warn に含めて集計する。
        var warn = results.Count(r =>
            r.Verdict is ManualCheckVerdict.Warn or ManualCheckVerdict.Missing);

        return new ManualCheckComparison(
            results, results.Count, match, mismatch, warn, Decide(mismatch, warn));
    }

    /// <summary>相違があれば fail、要確認があれば warn、それ以外は pass（既存判定と同じ語彙）。</summary>
    public static string Decide(int mismatchCount, int warnCount) =>
        mismatchCount > 0 ? SubsidiaryCheckSeverity.Fail
        : warnCount > 0 ? SubsidiaryCheckSeverity.Warn
        : SubsidiaryCheckSeverity.Pass;

    private static bool HasValue(ManualInputField field) =>
        field.Compare == ManualFieldCompareMode.Ratio
            ? field.Ratios.Any(r => !string.IsNullOrWhiteSpace(r.Material) || r.Percent is not null)
            : field.Values.Any(v => !string.IsNullOrWhiteSpace(v));

    private static ManualFieldResult CompareField(ManualInputField field, ExtractedField? extracted) =>
        field.Compare switch
        {
            ManualFieldCompareMode.Ratio => CompareRatio(field, extracted),
            ManualFieldCompareMode.CareIcon => CompareCareIcon(field, extracted),
            _ => CompareText(field, extracted),
        };

    // ------------------------------------------------------------
    // 文字列突合（text / number 共通。number も NFKC で全角数字を吸収する）
    // ------------------------------------------------------------

    private static ManualFieldResult CompareText(ManualInputField field, ExtractedField? extracted)
    {
        var expectedValues = field.Values.Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim()).ToList();
        var expectedDisplay = string.Join(" / ", expectedValues);
        var expectedItems = field.Multiple ? expectedValues : new List<string>();

        var unreadable = TryUnreadableResult(field, extracted, expectedDisplay, expectedItems);
        if (unreadable is not null)
        {
            return unreadable;
        }

        var extractedValues = ExtractedValues(extracted!);
        var extractedDisplay = extracted!.Text?.Trim() is { Length: > 0 } text
            ? text
            : string.Join(" / ", extractedValues);
        var extractedItems = field.Multiple ? extractedValues : new List<string>();

        var expectedKey = NormalizeText(field.Multiple ? string.Join("\n", expectedValues) : expectedDisplay);
        var extractedKey = NormalizeText(
            extracted.Text is { Length: > 0 }
                ? extracted.Text
                : string.Join("\n", extractedValues));

        var (verdict, detail) = expectedKey == extractedKey
            ? (ManualCheckVerdict.Match, "手入力とタグの記載が一致しています。")
            : (ManualCheckVerdict.Mismatch,
                $"手入力「{expectedDisplay}」に対し、タグの読取値「{extractedDisplay}」が一致しません。");

        return new ManualFieldResult(
            field.Key, field.Label, field.Compare, verdict,
            expectedDisplay, extractedDisplay, detail, expectedItems, extractedItems);
    }

    // ------------------------------------------------------------
    // 組成（割合つき成分）突合
    // ------------------------------------------------------------

    private static ManualFieldResult CompareRatio(ManualInputField field, ExtractedField? extracted)
    {
        var expectedComponents = field.Ratios
            .Where(r => !string.IsNullOrWhiteSpace(r.Material) || r.Percent is not null)
            .Select(r => (Material: (r.Material ?? string.Empty).Trim(), r.Percent))
            .ToList();
        var expectedItems = expectedComponents.Select(FormatComponent).ToList();
        var expectedDisplay = string.Join(" / ", expectedItems);

        var unreadable = TryUnreadableResult(field, extracted, expectedDisplay, expectedItems);
        if (unreadable is not null)
        {
            return unreadable;
        }

        var extractedComponents = ExtractedValues(extracted!).Select(ParseComponent).ToList();
        var extractedItems = extractedComponents.Select(FormatComponent).ToList();
        var extractedDisplay = string.Join(" / ", extractedItems);

        var expectedKeys = expectedComponents.Select(ComponentKey).ToList();
        var extractedKeys = extractedComponents.Select(ComponentKey).ToList();

        string verdict;
        string detail;
        if (expectedKeys.SequenceEqual(extractedKeys))
        {
            verdict = ManualCheckVerdict.Match;
            detail = "組成（成分・割合・並び順）が一致しています。";
        }
        else if (SameMultiset(expectedKeys, extractedKeys))
        {
            // 成分・割合は一致するが並び順が異なる。組成は割合降順で記載する規定があるため
            // 要確認として提示する（AI の順序読取は誤りうるため相違とは断じない）。
            verdict = ManualCheckVerdict.Warn;
            detail = $"成分・割合は一致しますが並び順が異なります（手入力: {expectedDisplay} / タグ: {extractedDisplay}）。";
        }
        else
        {
            verdict = ManualCheckVerdict.Mismatch;
            detail = $"組成が一致しません（手入力: {expectedDisplay} / タグ: {extractedDisplay}）。";
        }

        return new ManualFieldResult(
            field.Key, field.Label, field.Compare, verdict,
            expectedDisplay, extractedDisplay, detail, expectedItems, extractedItems);
    }

    private static string FormatComponent((string Material, decimal? Percent) c) =>
        c.Percent is { } p
            ? $"{c.Material} {p.ToString("0.##", CultureInfo.InvariantCulture)}%".Trim()
            : c.Material;

    /// <summary>突合キー（材質は正規化、割合は数値化。表記揺れを吸収）。</summary>
    private static string ComponentKey((string Material, decimal? Percent) c) =>
        NormalizeText(c.Material) + "=" + (c.Percent?.ToString("0.##", CultureInfo.InvariantCulture) ?? "");

    private static (string Material, decimal? Percent) ParseComponent(string raw)
    {
        var normalized = raw.Normalize(NormalizationForm.FormKC).Trim();
        var match = RatioPattern.Match(normalized);
        if (match.Success &&
            decimal.TryParse(match.Groups["percent"].Value, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var percent))
        {
            return (match.Groups["material"].Value.Trim(), percent);
        }

        return (normalized, null);
    }

    // ------------------------------------------------------------
    // 洗濯表示（アイコンコード列）突合 — 並び順を含めて検査する
    // ------------------------------------------------------------

    private static ManualFieldResult CompareCareIcon(ManualInputField field, ExtractedField? extracted)
    {
        var expectedCodes = field.Values.Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim()).ToList();
        var expectedItems = expectedCodes.Select(CareIconCatalog.LabelOf).ToList();
        var expectedDisplay = string.Join(" → ", expectedItems);

        var unreadable = TryUnreadableResult(field, extracted, expectedDisplay, expectedItems);
        if (unreadable is not null)
        {
            return unreadable;
        }

        var extractedCodes = ExtractedValues(extracted!).Select(c => c.Trim()).ToList();
        var extractedItems = extractedCodes.Select(CareIconCatalog.LabelOf).ToList();
        var extractedDisplay = string.Join(" → ", extractedItems);

        var expectedKeys = expectedCodes.Select(NormalizeCode).ToList();
        var extractedKeys = extractedCodes.Select(NormalizeCode).ToList();

        string verdict;
        string detail;
        if (expectedKeys.SequenceEqual(extractedKeys))
        {
            verdict = ManualCheckVerdict.Match;
            detail = "洗濯表示アイコンの種類・並び順が一致しています。";
        }
        else if (SameMultiset(expectedKeys, extractedKeys))
        {
            // 種類は一致するが並び順が異なる。アイコンは優先順位順で記載する規定があり、
            // 並び順の相違はチェック対象（相違＝要修正として扱う）。
            verdict = ManualCheckVerdict.Mismatch;
            detail = $"アイコンの種類は一致しますが並び順が異なります（手入力: {expectedDisplay} / タグ: {extractedDisplay}）。";
        }
        else
        {
            verdict = ManualCheckVerdict.Mismatch;
            detail = $"洗濯表示アイコンに過不足があります（手入力: {expectedDisplay} / タグ: {extractedDisplay}）。";
        }

        return new ManualFieldResult(
            field.Key, field.Label, field.Compare, verdict,
            expectedDisplay, extractedDisplay, detail, expectedItems, extractedItems);
    }

    // ------------------------------------------------------------
    // 共通
    // ------------------------------------------------------------

    /// <summary>
    /// 抽出が判読不能／欠落なら要確認系の結果を返す（読めていれば null で、各突合へ進む）。
    /// AI の読取失敗を相違（mismatch）と断じず、missing/warn へ倒す（原則4）。
    /// </summary>
    private static ManualFieldResult? TryUnreadableResult(
        ManualInputField field,
        ExtractedField? extracted,
        string expectedDisplay,
        IReadOnlyList<string> expectedItems)
    {
        if (extracted is null)
        {
            return new ManualFieldResult(
                field.Key, field.Label, field.Compare, ManualCheckVerdict.Missing,
                expectedDisplay, null,
                "タグ画像から該当項目を読み取れませんでした。タグに表示があるか目視で確認してください。",
                expectedItems, Array.Empty<string>());
        }

        var hasContent = !string.IsNullOrWhiteSpace(extracted.Text)
                         || extracted.Items.Any(i => !string.IsNullOrWhiteSpace(i));
        if (!extracted.Readable || !hasContent)
        {
            return new ManualFieldResult(
                field.Key, field.Label, field.Compare, ManualCheckVerdict.Warn,
                expectedDisplay, null,
                "タグ画像が不鮮明などの理由で該当項目を判読できませんでした。目視で確認してください。",
                expectedItems, Array.Empty<string>());
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractedValues(ExtractedField extracted)
    {
        var items = extracted.Items.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToList();
        if (items.Count > 0)
        {
            return items;
        }

        return string.IsNullOrWhiteSpace(extracted.Text)
            ? Array.Empty<string>()
            : new List<string> { extracted.Text.Trim() };
    }

    /// <summary>NFKC 正規化＋空白除去＋大文字化（表記揺れの吸収。突合キー専用）。</summary>
    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static string NormalizeCode(string code) => code.Trim().ToLowerInvariant();

    /// <summary>2つの列が（順序を無視して）同じ多重集合かを判定する。</summary>
    private static bool SameMultiset(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in a)
        {
            counts[item] = counts.GetValueOrDefault(item) + 1;
        }

        foreach (var item in b)
        {
            if (!counts.TryGetValue(item, out var n) || n == 0)
            {
                return false;
            }

            counts[item] = n - 1;
        }

        return counts.Values.All(n => n == 0);
    }
}
