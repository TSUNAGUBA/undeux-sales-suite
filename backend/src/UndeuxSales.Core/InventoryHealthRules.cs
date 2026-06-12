namespace UndeuxSales.Core;

/// <summary>
/// 在庫健全性（注意・滞留・不動）の判定ルールと閾値の SoT（単一定義）。
/// 業務定義のドキュメントは <c>.ai-native/domain-context/industry/apparel-inventory-health.md</c>（相互参照）。
/// </summary>
/// <remarks>
/// <para>
/// 閾値を変更する場合は本クラスの定数のみを変更する。SQL には Dapper パラメータとして注入され
/// （<c>InventoryHealthSql</c>）、API レスポンスには <see cref="Thresholds"/> として含まれ、
/// フロントの定義バナー・バケット表示はレスポンス値から描画されるため、値の二重定義は存在しない。
/// </para>
/// <para>
/// SQL 側の判定 CASE（<c>InventoryHealthSql.StatusCaseExpression</c>）は <see cref="Classify"/> と
/// 同一論理である。どちらかを変更する場合は必ず両方を更新し、単体テストの境界値で検証すること。
/// </para>
/// </remarks>
public static class InventoryHealthRules
{
    /// <summary>注意: 在庫日数がこの値を超えると「注意」（滞留の予備軍）。</summary>
    public const double CautionStockDays = 45;

    /// <summary>滞留: 在庫日数がこの値を超え、かつ消化率が <see cref="StagnantSellThroughCeiling"/> 未満。</summary>
    public const double StagnantStockDays = 60;

    /// <summary>滞留: 消化率の上限（これ未満で在庫日数超過なら滞留）。</summary>
    public const double StagnantSellThroughCeiling = 0.75;

    /// <summary>不動: 直近この週数の間、出荷（売上数量）ゼロが続いた在庫あり SKU。</summary>
    public const int DormantLookbackWeeks = 8;

    /// <summary>
    /// 不動経過週数（zero_sales_weeks）の計測上限。性能のため売上ファクトの走査を直近この週数に
    /// 限定する（約160万行のフルスキャン回避）。この週数を超えて売上がない SKU は本値でキャップされる。
    /// </summary>
    public const int DormantScanWeeks = 26;

    /// <summary>不動がこの週数以上続いたら「処分・値引き販売の検討」を推奨。</summary>
    public const int DormantClearanceWeeks = 16;

    /// <summary>不動がこの週数以上続いたら「値下げ候補」を推奨。</summary>
    public const int DormantMarkdownWeeks = 12;

    /// <summary>滞留タブの経過バケット境界（在庫日数）。[60,90,120] → 60〜90 / 90〜120 / 120超。</summary>
    public static readonly IReadOnlyList<int> StagnantAgingBoundaries = new[] { 60, 90, 120 };

    /// <summary>不動タブの経過バケット境界（出荷ゼロ週数）。[8,12,16] → 8〜12 / 12〜16 / 16以上。</summary>
    public static readonly IReadOnlyList<int> DormantAgingBoundaries = new[] { 8, 12, 16 };

    /// <summary>部門アラート: 在庫構成比がこの割合以上の部門のみ消化率低下を指摘する。</summary>
    public const double DepartmentAttentionStockShare = 0.05;

    /// <summary>部門アラート: 全体消化率からこの幅以上低い部門を指摘する。</summary>
    public const double DepartmentAttentionSellThroughGap = 0.10;

    /// <summary>滞留・不動の在庫比率が前週比でこの幅以上悪化したらアクションフィードで警告する。</summary>
    public const double WorseningRatioDelta = 0.01;

    // ---- 状態コード（API の status 値。フロントの表示カタログ utils/skuStatus.ts が射影する） ----
    public const string StatusHealthy = "healthy";
    public const string StatusCaution = "caution";
    public const string StatusStagnant = "stagnant";
    public const string StatusDormant = "dormant";

    /// <summary>statuses クエリパラメータとして受け付ける状態コードの全件。</summary>
    public static readonly IReadOnlyList<string> AllStatuses =
        new[] { StatusHealthy, StatusCaution, StatusStagnant, StatusDormant };

    // ---- 推奨アクションコード（表示ラベル・CTA はフロントのカタログが射影する） ----
    public const string ActionReduceOrder = "reduce-order";          // 発注抑制・停止の検討
    public const string ActionMarkdownCandidate = "markdown-candidate"; // 値下げ候補（散布図モードBへ）
    public const string ActionReviewShelf = "review-shelf";          // 売場・棚割の再点検
    public const string ActionClearance = "clearance";               // 処分・値引き販売の検討
    public const string ActionObserve = "observe";                   // 経過観察

    // ---- アクションフィードの遷移先タブ ----
    public const string TabDashboard = "dashboard";
    public const string TabItems = "items";
    public const string TabStagnant = "stagnant";
    public const string TabDormant = "dormant";

    /// <summary>
    /// SKU の在庫健全性を分類する。<c>InventoryHealthSql.StatusCaseExpression</c>（SQL CASE）と同一論理。
    /// 在庫ゼロの行は対処対象がないため healthy として扱う（売り切り済み）。
    /// 不動（出荷ゼロ継続）は滞留より優先する。
    /// </summary>
    public static string Classify(long stock, double stockDays, double sellThroughRate, int zeroSalesWeeks)
    {
        if (stock <= 0)
        {
            return StatusHealthy;
        }

        if (zeroSalesWeeks >= DormantLookbackWeeks)
        {
            return StatusDormant;
        }

        if (stockDays > StagnantStockDays && sellThroughRate < StagnantSellThroughCeiling)
        {
            return StatusStagnant;
        }

        return stockDays > CautionStockDays ? StatusCaution : StatusHealthy;
    }

    /// <summary>
    /// 状態と発注残・経過週数から推奨アクションコードを返す（healthy は null）。
    /// 発注残がある滞留・不動は「仕入れを止めるだけで改善する」ため発注抑制を最優先する。
    /// 店舗軸がないデータのため「店間移動」は提案しない（売場・棚割の再点検で代替）。
    /// </summary>
    public static string? Recommend(string status, decimal orderQuantity, int zeroSalesWeeks) => status switch
    {
        StatusCaution => ActionObserve,
        StatusStagnant => orderQuantity > 0 ? ActionReduceOrder : ActionMarkdownCandidate,
        StatusDormant when orderQuantity > 0 => ActionReduceOrder,
        StatusDormant when zeroSalesWeeks >= DormantClearanceWeeks => ActionClearance,
        StatusDormant when zeroSalesWeeks >= DormantMarkdownWeeks => ActionMarkdownCandidate,
        StatusDormant => ActionReviewShelf,
        _ => null,
    };

    /// <summary>statuses クエリ値をホワイトリスト照合して正規化する（未知値は無視・重複排除）。</summary>
    /// <remarks>未知値を 400 にしない理由: 在日バケット（StockDaysSql）と同じく、古いクライアントや
    /// 手書き URL からの値で一覧全体が壊れないグレースフルデグラデーションを優先する。</remarks>
    public static IReadOnlyList<string> NormalizeStatuses(IEnumerable<string?>? statuses)
    {
        if (statuses is null)
        {
            return Array.Empty<string>();
        }

        return statuses
            .Select(s => s?.Trim().ToLowerInvariant())
            .Where(s => s is not null && AllStatuses.Contains(s))
            .Select(s => s!)
            .Distinct()
            .ToArray();
    }

    /// <summary>適用中の閾値（API レスポンスに含め、フロントは本値から定義バナー等を描画する）。</summary>
    public static InventoryThresholds Thresholds() => new(
        CautionStockDays,
        StagnantStockDays,
        StagnantSellThroughCeiling,
        DormantLookbackWeeks,
        DormantScanWeeks,
        StagnantAgingBoundaries,
        DormantAgingBoundaries);

    /// <summary>
    /// 集約値から「今週のアクション」フィードをルールベースで生成する（純関数）。
    /// 重大度順（danger → warning → info）。メッセージは画面表示用の日本語
    /// （既存 API のエラーメッセージ・'(未設定)' ラベル等と同じくサーバ側で日本語を返す方針）。
    /// </summary>
    public static IReadOnlyList<InventoryActionItem> BuildActions(InventoryActionInput input)
    {
        var actions = new List<InventoryActionItem>();

        if (input.DormantCount > 0)
        {
            actions.Add(new InventoryActionItem(
                "dormant-detected", "danger",
                $"不動在庫が {input.DormantCount} SKU（直近{DormantLookbackWeeks}週 出荷ゼロ・在庫 {input.DormantStock:N0} 点）。"
                + "値下げ・処分の判断対象です。",
                TabDormant, input.DormantCount));
        }

        if (input.StagnantCount > 0)
        {
            var orderNote = input.StagnantWithOrderCount > 0
                ? $"うち発注残あり {input.StagnantWithOrderCount} SKU は発注抑制を最優先で検討してください。"
                : "値下げ候補の検討を推奨します。";
            actions.Add(new InventoryActionItem(
                "stagnant-detected",
                input.StagnantWithOrderCount > 0 ? "danger" : "warning",
                $"滞留在庫が {input.StagnantCount} SKU（在庫日数{StagnantStockDays:0}日超×消化率{StagnantSellThroughCeiling:P0}未満）。{orderNote}",
                TabStagnant, input.StagnantCount));
        }

        if (input.PreviousStagnantDormantStockRatio.HasValue)
        {
            var delta = input.StagnantDormantStockRatio - input.PreviousStagnantDormantStockRatio.Value;
            if (delta >= WorseningRatioDelta)
            {
                actions.Add(new InventoryActionItem(
                    "health-worsened", "warning",
                    $"滞留・不動の在庫比率が前週比 +{delta * 100:0.0}pt（現在 {input.StagnantDormantStockRatio:P1}）に悪化しています。",
                    TabStagnant, null));
            }
        }

        var worst = FindAttentionDepartment(input);
        if (worst is not null)
        {
            actions.Add(new InventoryActionItem(
                "department-low-sell-through", "warning",
                $"部門 {worst.Label}: 消化率 {worst.SellThroughRate:P1} が全体（{input.TotalSellThroughRate:P1}）を大きく下回っています。"
                + "在庫一覧で内訳の確認を推奨します。",
                TabItems, null));
        }

        if (input.DormantCount == 0 && input.StagnantCount == 0)
        {
            actions.Add(new InventoryActionItem(
                "all-clear", "info",
                "滞留・不動在庫は検出されていません。在庫状態は良好です。",
                TabItems, null));
        }

        return actions;
    }

    /// <summary>
    /// 消化率低下を指摘すべき部門を選ぶ。在庫構成比 <see cref="DepartmentAttentionStockShare"/> 以上の
    /// 部門のうち、全体消化率より <see cref="DepartmentAttentionSellThroughGap"/> 以上低い最低の部門。
    /// </summary>
    private static InventoryDepartmentHealthInput? FindAttentionDepartment(InventoryActionInput input)
    {
        if (input.TotalStock <= 0)
        {
            return null;
        }

        return input.Departments
            .Where(d => (double)d.Stock / input.TotalStock >= DepartmentAttentionStockShare)
            .Where(d => d.SellThroughRate <= input.TotalSellThroughRate - DepartmentAttentionSellThroughGap)
            .OrderBy(d => d.SellThroughRate)
            .FirstOrDefault();
    }
}

/// <summary>適用中の在庫健全性閾値（API レスポンス用。フロント表示の単一の情報源）。</summary>
public sealed record InventoryThresholds(
    double CautionStockDays,
    double StagnantStockDays,
    double StagnantSellThroughCeiling,
    int DormantLookbackWeeks,
    int DormantScanWeeks,
    IReadOnlyList<int> StagnantAgingBoundaries,
    IReadOnlyList<int> DormantAgingBoundaries);

/// <summary>「今週のアクション」フィードの1件。Count は対象 SKU 数（件数を持たないルールは null）。</summary>
public sealed record InventoryActionItem(
    string Code,
    string Severity,
    string Message,
    string TargetTab,
    int? Count);

/// <summary><see cref="InventoryHealthRules.BuildActions"/> の入力（集約値のスナップショット）。</summary>
public sealed record InventoryActionInput(
    int StagnantCount,
    int StagnantWithOrderCount,
    int DormantCount,
    long DormantStock,
    long TotalStock,
    double StagnantDormantStockRatio,
    double? PreviousStagnantDormantStockRatio,
    double TotalSellThroughRate,
    IReadOnlyList<InventoryDepartmentHealthInput> Departments);

/// <summary>部門アラート判定用の最小入力（部門ラベル・在庫数・消化率）。</summary>
public sealed record InventoryDepartmentHealthInput(string Label, long Stock, double SellThroughRate);
