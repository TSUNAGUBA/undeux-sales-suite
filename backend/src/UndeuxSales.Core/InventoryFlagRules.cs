namespace UndeuxSales.Core;

/// <summary>
/// 在庫アクションフラグ（発注停止候補・値下げ候補）の語彙と正規化の SoT。
/// DB 側の CHECK 制約（db/schema.sql の inventory_action_flag）と同一の値集合を保持する
/// （変更時は両方を更新し、単体テストの全件一致で同期を固定する）。
/// 表示ラベル・色はフロントの表示カタログ（frontend/app/utils/skuStatus.ts）が射影する。
/// </summary>
public static class InventoryFlagRules
{
    // ---- フラグ種別 ----

    /// <summary>発注停止候補（発注残があるまま滞留・不動化した SKU への最優先打ち手）。</summary>
    public const string FlagTypeStopOrder = "stop-order";

    /// <summary>値下げ候補（散布図モードB等で値下げ判断を行う対象）。</summary>
    public const string FlagTypeMarkdown = "markdown";

    public static readonly IReadOnlyList<string> AllFlagTypes =
        new[] { FlagTypeStopOrder, FlagTypeMarkdown };

    // ---- 対応状況 ----

    /// <summary>候補（フラグ付与直後の初期状態）。</summary>
    public const string StatusCandidate = "candidate";

    /// <summary>対応中。</summary>
    public const string StatusInProgress = "in-progress";

    /// <summary>対応済。</summary>
    public const string StatusDone = "done";

    /// <summary>見送り（対応しないと判断した記録）。</summary>
    public const string StatusDismissed = "dismissed";

    /// <summary>
    /// 全状態。遷移は全状態間を許可する（candidate→done の直行や done→in-progress の
    /// 差し戻しを業務上禁止する根拠がないため、ホワイトリスト照合のみ行う）。
    /// </summary>
    public static readonly IReadOnlyList<string> AllStatuses =
        new[] { StatusCandidate, StatusInProgress, StatusDone, StatusDismissed };

    /// <summary>フラグ種別の妥当性を判定する。</summary>
    public static bool IsValidFlagType(string? value)
        => value is not null && AllFlagTypes.Contains(value);

    /// <summary>対応状況の妥当性を判定する。</summary>
    public static bool IsValidStatus(string? value)
        => value is not null && AllStatuses.Contains(value);

    /// <summary>
    /// flagTypes クエリ値をホワイトリスト照合して正規化する（未知値は無視・重複排除。
    /// InventoryHealthRules.NormalizeStatuses と同じグレースフル方式）。
    /// </summary>
    public static IReadOnlyList<string> NormalizeFlagTypes(IEnumerable<string?>? flagTypes)
        => Normalize(flagTypes, AllFlagTypes);

    /// <summary>フラグの対応状況クエリ値をホワイトリスト照合して正規化する（未知値は無視・重複排除）。</summary>
    public static IReadOnlyList<string> NormalizeFlagStatuses(IEnumerable<string?>? statuses)
        => Normalize(statuses, AllStatuses);

    /// <summary>flagged クエリ値を正規化する（any / none 以外は null＝絞込なし。未知値無視の方式）。</summary>
    public static string? NormalizeFlagged(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "any" or "none" ? normalized : null;
    }

    private static IReadOnlyList<string> Normalize(
        IEnumerable<string?>? values, IReadOnlyList<string> whitelist)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        return values
            .Select(v => v?.Trim().ToLowerInvariant())
            .Where(v => v is not null && whitelist.Contains(v))
            .Select(v => v!)
            .Distinct()
            .ToArray();
    }
}
