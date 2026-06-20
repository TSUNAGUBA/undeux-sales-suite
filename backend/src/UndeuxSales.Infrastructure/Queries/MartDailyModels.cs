namespace UndeuxSales.Infrastructure.Queries;

/// <summary>日次系列の1点（特定品番の日次分析用）。気温は標準エリア＝東京。</summary>
/// <remarks>売上は sales_weekly の曜日別7列を実日付へ展開した派生（QueryDailyTrend と同方式）。
/// 気温は mart.dim_climate（area=standard＝東京）の実測を優先し、未カバー日は標準気候へフォールバック。</remarks>
public sealed record DailySeriesPoint(
    DateOnly Date,
    long Quantity,
    long Amount,
    long GrossProfit,
    double TempAvg);

/// <summary>GET /api/mart/daily-series のレスポンス（気温参照都市は常に東京＝standard）。</summary>
public sealed record DailySeriesResponse(
    string AreaCity,
    IReadOnlyList<DailySeriesPoint> Points);

/// <summary>
/// 帳票区分（売発注/情報発注）の前週比較による SKU 遷移区分。
/// new=当週新規（前週に無し）, unchanged=区分変化なし, sale-to-info=売→情, info-to-sale=情→売,
/// dropped=情売→無（前週の売上参照には在るが当週に無い）。
/// </summary>
public static class ChohyoTransitionKind
{
    public const string New = "new";
    public const string Unchanged = "unchanged";
    public const string SaleToInfo = "sale-to-info";
    public const string InfoToSale = "info-to-sale";
    public const string Dropped = "dropped";
}

/// <summary>帳票区分遷移の1行（SKU＝業態×記号×品番3桁×単品4桁）。</summary>
public sealed record ChohyoTransitionRow(
    string GyotaiCode,
    string ShohinKigou,
    string HinbanCode,
    string TanpinCode,
    string Hinmei,
    // 当週の帳票区分（空白正規化済み。当週に無い場合は空）。
    string CurrentKubun,
    // 前週の帳票区分（空白正規化済み。前週に無い場合は空）。
    string PreviousKubun,
    string Transition,
    long Quantity,
    long Stock);

/// <summary>GET /api/mart/chohyo-transition のレスポンス。</summary>
public sealed record ChohyoTransitionResponse(
    DateOnly? CurrentWeek,
    DateOnly? PreviousWeek,
    IReadOnlyList<ChohyoTransitionRow> Rows,
    // 行数が上限（2000）で切り詰められたか。品番未指定（すべて）等で多数該当する場合に true。
    bool Truncated);
