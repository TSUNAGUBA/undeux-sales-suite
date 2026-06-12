namespace UndeuxSales.Infrastructure.Queries;

/// <summary>コードと表示名の組。</summary>
public sealed record CodeName(string Code, string? Name);

/// <summary>業態コード・表示名・略称の組（業態専用）。</summary>
public sealed record BusinessTypeOption(string Code, string? Name, string? ShortName);

/// <summary>
/// フィルタUIの選択肢一式。
/// 取引先（customer_code）は本アプリのユーザー（メーカー）に対して小売側が振り出した
/// 固有コードで、本アプリ内では常に同じ値となるため選択肢として提供しない。
/// </summary>
public sealed record FilterOptions(
    IReadOnlyList<CodeName> Departments,
    IReadOnlyList<BusinessTypeOption> BusinessTypes,
    IReadOnlyList<CodeName> Seasons,
    IReadOnlyList<DateOnly> Weeks,
    IReadOnlyList<string> Tanawari1);

/// <summary>全社サマリーの主要KPI。</summary>
public sealed record SalesKpi(
    long Quantity,
    long Amount,
    long GrossProfit,
    double GrossProfitRate,
    int ProductCount,
    long CurrentStock,
    double SellThroughRate,
    DateOnly? LatestWeek);

/// <summary>時系列トレンドの1点。</summary>
public sealed record TrendPoint(DateOnly Date, long Quantity, long Amount, long GrossProfit);

/// <summary>全社サマリーのレスポンス。</summary>
public sealed record SummaryResponse(SalesKpi Kpi, IReadOnlyList<TrendPoint> WeeklyTrend);

/// <summary>売上トレンドのレスポンス。</summary>
public sealed record TrendResponse(string Granularity, IReadOnlyList<TrendPoint> Points);

/// <summary>集計軸別の1行。</summary>
public sealed record BreakdownRow(
    string Key,
    string Label,
    long Quantity,
    long Amount,
    long GrossProfit,
    double SharePercent);

/// <summary>集計軸別分析のレスポンス。</summary>
public sealed record BreakdownResponse(
    string Dimension,
    string Metric,
    IReadOnlyList<BreakdownRow> Rows);

/// <summary>在庫・発注の主要KPI（最新週スナップショット基準）。</summary>
public sealed record InventoryKpi(
    long TotalStock,
    decimal TotalOrderQuantity,
    long TotalAdvanceQuantity,
    long CumulativeSales,
    long CumulativeDelivery,
    double SellThroughRate,
    double AverageStockDays,
    DateOnly? LatestWeek);

/// <summary>在庫・発注の部門別1行。</summary>
public sealed record InventoryBreakdownRow(
    string Key,
    string Label,
    long Stock,
    decimal OrderQuantity,
    long AdvanceQuantity,
    double SellThroughRate);

/// <summary>在庫・発注分析のレスポンス。</summary>
public sealed record InventoryResponse(
    InventoryKpi Kpi,
    IReadOnlyList<InventoryBreakdownRow> ByDepartment);

/// <summary>商品別分析の1行。商品マスタが結合できた行のみ MasterProductId 等が設定される。</summary>
/// <remarks>
/// 同一の (hinban_code, tanpin_code) が複数業態 (gyotai_code) で売られているとき行が分裂し、
/// (gyotai_code, shohin_kigou, hinban_code, tanpin_code) が真の行キーになる。フロント側の
/// v-for :key にもこの 4 つを使用すること（hinban-tanpin だけでは衝突する）。
/// </remarks>
public sealed record ProductRow(
    string GyotaiCode,
    string ShohinKigou,
    string HinbanCode,
    string TanpinCode,
    string Hinmei,
    string Kisetsu,
    long SalesQuantity,
    long SalesAmount,
    long GrossProfit,
    long Stock,
    double SellThroughRate,
    double AverageStockDays,
    Guid? MasterProductId,
    string? ProductName,
    string? Brand,
    string? PrimaryImageUrl);

/// <summary>商品別分析のページ。</summary>
public sealed record ProductPage(
    IReadOnlyList<ProductRow> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>クロス集計（行×列マトリクス）で使用可能なディメンション。</summary>
/// <remarks>
/// プレフィックスで時間軸（time）かカテゴリ軸（category）を区別する。
/// 時間軸は <c>sw.import_date</c> から導出し、カテゴリ軸は <see cref="BreakdownDimension"/> と
/// 同等の SQL 式（商品=複数列連結など）を使う。
/// </remarks>
public enum CrosstabDimension
{
    // 時間軸
    /// <summary>年（YYYY）。</summary>
    TimeYear,
    /// <summary>四半期（YYYY-Q1 等）。</summary>
    TimeQuarter,
    /// <summary>月（YYYY-MM）。</summary>
    TimeMonth,
    // カテゴリ軸
    /// <summary>部門。</summary>
    CategoryDepartment,
    /// <summary>業態。</summary>
    CategoryBusinessType,
    /// <summary>季節区分。</summary>
    CategorySeason,
    /// <summary>品番3桁（hinban_code 単独）。</summary>
    CategoryHinban,
    /// <summary>単品（品番-単品）。</summary>
    CategoryProduct,
    /// <summary>カラー。</summary>
    CategoryColor,
    /// <summary>サイズ。</summary>
    CategorySize,
    /// <summary>帳票区分名。</summary>
    CategoryChohyoKubun,
    /// <summary>棚割1。</summary>
    CategoryTanawari1,
    /// <summary>棚割2。</summary>
    CategoryTanawari2,
    /// <summary>商品記号。</summary>
    CategoryShohinKigo,
}

/// <summary>クロス集計のディメンション情報（行・列のメタ情報）。</summary>
/// <param name="Key">フロント側の文字列キー（例 "time:year", "category:department"）。</param>
/// <param name="Category">"time" または "category"。</param>
/// <param name="Label">表示ラベル。</param>
/// <param name="IsTimeAxis">
/// 時間軸ディメンション（年・四半期・月）かどうか。フロント側で在庫系メトリクスの可否判定に使う。
/// バックエンドの <c>TimeDimensions</c> 判定と一致しており、フロント側の文字列前方一致判定の
/// 重複実装を避けるため、API レスポンスから渡す（SoT 統一）。
/// </param>
public sealed record CrosstabDimensionInfo(string Key, string Category, string Label, bool IsTimeAxis);

/// <summary>
/// クロス集計の1セルの値。在庫系（stockDays / stock / sellThroughRate）は時間軸を含む組合せでは
/// <c>null</c>（最新週スナップショットに基づくため）。気温系（tempAvg / tempMax / tempMin）は
/// 逆に時間軸とエリア種別が指定された場合のみ設定され、それ以外は <c>null</c>。
/// </summary>
/// <remarks>
/// 気温は売上行の集計ではなく、時間軸バケット（年/四半期/月）の期間に対する標準気候
/// （<see cref="UndeuxSales.Core.ClimateModel"/>）から決まる。時間軸が行の場合は行ラベル、
/// 列の場合は列ラベルの期間から算出し、同一時間バケット内の全セルで同じ値になる。
/// </remarks>
public sealed record CrosstabCellValues(
    long? Amount,
    long? Quantity,
    long? GrossProfit,
    double? SharePercent,
    double? StockDays,
    double? SellThroughRate,
    long? Stock,
    double? TempAvg = null,
    double? TempMax = null,
    double? TempMin = null);

/// <summary>クロス集計の1セル。</summary>
public sealed record CrosstabCell(CrosstabCellValues Values);

/// <summary>クロス集計（マトリクス）のレスポンス。</summary>
/// <param name="RowDimension">行ディメンション情報。</param>
/// <param name="ColumnDimension">列ディメンション情報。</param>
/// <param name="RowLabels">行ラベルの順序付きリスト（最大 100 件で切り詰め）。</param>
/// <param name="ColumnLabels">列ラベルの順序付きリスト（最大 100 件で切り詰め）。</param>
/// <param name="Cells">[行ラベル][列ラベル] = CrosstabCell。空セルは省略。</param>
/// <param name="RowTotals">行ごとの合計（最終列に表示）。表示行の和と一致する。</param>
/// <param name="ColumnTotals">列ごとの合計（最終行に表示）。表示列の和と一致する。</param>
/// <param name="GrandTotal">
/// 全体合計（右下セル）。切り詰め後の表示セル・行合計・列合計と完全に整合する。
/// </param>
/// <param name="LatestWeek">在庫スナップショット基準週（時間軸絡みでない場合に設定）。</param>
/// <param name="AvailableMetrics">時間軸が含まれる場合は在庫系を除いたメトリクスキー一覧。</param>
/// <param name="RowTruncated">行ラベル数が 100 を超え切り詰められた場合 true。</param>
/// <param name="ColumnTruncated">列ラベル数が 100 を超え切り詰められた場合 true。</param>
public sealed record CrosstabMatrixResponse(
    CrosstabDimensionInfo RowDimension,
    CrosstabDimensionInfo ColumnDimension,
    IReadOnlyList<string> RowLabels,
    IReadOnlyList<string> ColumnLabels,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, CrosstabCell>> Cells,
    IReadOnlyDictionary<string, CrosstabCell> RowTotals,
    IReadOnlyDictionary<string, CrosstabCell> ColumnTotals,
    CrosstabCell GrandTotal,
    string? LatestWeek,
    IReadOnlyList<string> AvailableMetrics,
    bool RowTruncated,
    bool ColumnTruncated);

// ============================================================
// 商品マスタ（m_product / m_product_sku）の参照モデル
// ============================================================

/// <summary>商品マスタの代表画像情報（SKU+image_index の代表1枚）。</summary>
/// <remarks>
/// 末尾の集計フィールド（売上数量・平均在庫日数・季節・店頭在庫数）は sales_weekly を
/// 自然キー（業態×商品記号×品番）で結合した実績値。売上数量は全期間合計、店頭在庫数（zaikosu）と
/// 平均在庫日数（在日 zainiti の平均）は最新取込週スナップショット基準。マスタにのみ存在し
/// 売上実績が無い商品は 0 / 空文字となる。倉庫在庫は売上参照データに店頭/倉庫の区分が無いため提供しない。
/// </remarks>
public sealed record MasterProductSummary(
    Guid ProductId,
    string BusinessCategoryCd,
    string BusinessCategorySign,
    int DivisionCd,
    string DivisionName,
    string ProductName,
    string? Brand,
    string ProductSign,
    string? Manager,
    string ProductTypeCrd,
    int SkuCount,
    int ColorCount,
    int SizeCount,
    int? MinSalesPrice,
    int? MaxSalesPrice,
    string? PrimaryImageUrl,
    long SalesQuantity,
    double AverageStockDays,
    string Kisetsu,
    long StoreStock);

/// <summary>商品マスタの SKU 1件（同一 SKU の画像は IReadOnlyList で持つ）。</summary>
public sealed record MasterProductSku(
    Guid SkuItemId,
    string UnitCd,
    string ColorName,
    string SizeName,
    int SalesPrice,
    int CostPrice,
    IReadOnlyList<MasterProductSkuImage> Images);

/// <summary>SKU の画像1件。</summary>
public sealed record MasterProductSkuImage(
    Guid ImageId,
    int ImageIndex,
    string? ImageFileName,
    string ImageUrl);

/// <summary>商品マスタ詳細（親 + SKU 一覧）。</summary>
public sealed record MasterProductDetail(
    MasterProductSummary Summary,
    IReadOnlyList<MasterProductSku> Skus);

/// <summary>商品マスタ一覧のページ。</summary>
public sealed record MasterProductPage(
    IReadOnlyList<MasterProductSummary> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>商品マスタ専用フィルタの選択肢一式。</summary>
public sealed record MasterFilterOptions(
    IReadOnlyList<BusinessTypeOption> BusinessTypes,
    IReadOnlyList<CodeName> Divisions,
    IReadOnlyList<string> Brands,
    IReadOnlyList<string> Managers);

// ============================================================
// 商品軸の分析
// ============================================================

/// <summary>商品単位の期間内 KPI。</summary>
/// <remarks>
/// 取引先（customer_code）は本アプリでは常に同じ値（メーカー固有コード）のため、
/// 旧 StoreCount = COUNT(DISTINCT customer_code) は常に 1 となり指標として無意味だった。
/// よって本 KPI からは除外している。
/// </remarks>
public sealed record ProductAnalyticsKpi(
    long Quantity,
    long Amount,
    long GrossProfit,
    double GrossProfitRate,
    long CurrentStock,
    double SellThroughRate,
    double AverageStockDays,
    DateOnly? LatestWeek);

/// <summary>SKU別の売上集計（色・サイズ別）。</summary>
public sealed record ProductSkuPerformance(
    string UnitCd,
    string ColorName,
    string SizeName,
    int SalesPrice,
    string? PrimaryImageUrl,
    long Quantity,
    long Amount,
    long GrossProfit,
    long Stock,
    double SharePercent);

/// <summary>業態別の売上集計。</summary>
public sealed record ProductBusinessTypePerformance(
    string BusinessCategoryCd,
    string? DisplayName,
    string? ShortName,
    long Quantity,
    long Amount,
    long GrossProfit,
    double SharePercent);

/// <summary>
/// 商品分析のレスポンス（指定商品の包括的な売上分析）。
/// 取引先別売上は customer_code が常に同じ値のため意味を持たず、提供しない。
/// </summary>
public sealed record ProductAnalyticsResponse(
    MasterProductSummary Product,
    ProductAnalyticsKpi Kpi,
    IReadOnlyList<TrendPoint> WeeklyTrend,
    IReadOnlyList<ProductSkuPerformance> BySku,
    IReadOnlyList<ProductBusinessTypePerformance> ByBusinessType);

// ============================================================
// ランキング分析（単軸ランキング + 期間比較 + ABC/複合スコアの素材提供）
// ============================================================

/// <summary>
/// ランキング1件・1期間ぶんの集計値。
/// <para>
/// フロー指標（数量・金額・粗利）は期間内集計。在庫スナップショット指標（在庫数・消化率・在日）は
/// その期間の最新取込週基準であり、対象キーがその週に存在しない場合は <c>null</c>。
/// </para>
/// <para>
/// 粗利率・構成比・複合スコア・累積構成比・ABC ランク・順位は、ユーザーが対話的に選ぶ
/// 並び替え指標／重みに依存する「表示射影」であるため、フロント側で本値から算出する
/// （クロス集計の表示モード切替と同じ思想。SoT は本集計値、順位等はその射影）。
/// </para>
/// </summary>
public sealed record RankingMetricValues(
    long Quantity,
    long Amount,
    long GrossProfit,
    long? Stock,
    double? SellThroughRate,
    double? StockDays);

/// <summary>
/// ランキング1行。<see cref="Current"/> が主期間、<see cref="Comparison"/> が比較期間。
/// </summary>
/// <param name="Key">集計キー（ディメンションのユニーク識別子。商品は業態|記号|品番|単品の連結）。</param>
/// <param name="Label">表示ラベル（未設定値は "(未設定)"）。</param>
/// <param name="Current">
/// 主期間の集計値。主期間に存在しないキー（比較期間にのみ存在＝圏外転落）では <c>null</c>。
/// </param>
/// <param name="Comparison">
/// 比較期間の集計値。比較未指定、または比較期間に存在しないキー（＝新規）では <c>null</c>。
/// </param>
public sealed record RankingRow(
    string Key,
    string Label,
    RankingMetricValues? Current,
    RankingMetricValues? Comparison);

/// <summary>
/// ランキング分析のレスポンス。順位・累積構成比・ABC・複合スコアはフロント側で算出するため、
/// 本レスポンスは集計素材（行ごとの主期間／比較期間の指標）と利用可能指標のメタ情報を返す。
/// </summary>
/// <param name="Dimension">集計軸（<see cref="UndeuxSales.Core.Models.BreakdownDimension"/> の名前）。</param>
/// <param name="Rows">
/// 集計行。主期間の売上金額（比較時は主／比較の大きい方）の降順で、最大件数まで返す。
/// フロントは選択された指標・複合スコアで再ソートして順位を確定する。
/// </param>
/// <param name="LatestWeek">主期間の在庫スナップショット基準週（データなしは null）。</param>
/// <param name="ComparisonLatestWeek">比較期間の在庫スナップショット基準週（比較未指定／データなしは null）。</param>
/// <param name="AvailableMetrics">
/// 並び替え・複合スコアに利用可能な指標キー一覧。スナップショット指標（stock / sellThroughRate /
/// stockDays）は主期間に最新週が存在する場合のみ含む。
/// </param>
/// <param name="Truncated">対象キー数が上限を超え、件数が切り詰められた場合 true。</param>
public sealed record RankingResponse(
    string Dimension,
    IReadOnlyList<RankingRow> Rows,
    string? LatestWeek,
    string? ComparisonLatestWeek,
    IReadOnlyList<string> AvailableMetrics,
    bool Truncated);

// ============================================================
// 分析（散布図・回帰／重回帰シミュレーション）の素材
//
// 順位・複合スコア（ランキング）と同様、回帰係数・予測・象限分類は
// ユーザーが対話的に変える「表示射影」であるため、バックエンドは集計素材
// （週次系列・SKU別消化率/値引き率）のみを返し、回帰・予測はフロント（utils/regression）で算出する。
// 気温は売上行の集計ではなく標準気候（ClimateModel）から決まる派生値。
// ============================================================

/// <summary>週次系列の1点（売上フロー指標 + その週・エリアの標準気温 + 当週在庫スナップショット）。</summary>
/// <param name="Week">取込日（月曜）。表す週は前週 月〜日。</param>
/// <param name="Quantity">当週売上数量。</param>
/// <param name="Amount">当週売上金額。</param>
/// <param name="GrossProfit">当週粗利。</param>
/// <param name="TempAvg">週平均気温（℃）。</param>
/// <param name="TempMax">週最高気温（℃）。</param>
/// <param name="TempMin">週最低気温（℃）。</param>
/// <param name="Stock">当週の店頭在庫数（在庫スナップショットの時点値）。</param>
/// <param name="StockDays">当週の在日（平均）。</param>
/// <param name="SellThroughRate">当週の消化率（0..1。累計売上数 ÷ 累計納品数、分母0は0）。</param>
public sealed record WeeklySeriesPoint(
    DateOnly Week,
    long Quantity,
    long Amount,
    long GrossProfit,
    double TempAvg,
    double TempMax,
    double TempMin,
    long Stock,
    double StockDays,
    double SellThroughRate);

/// <summary>
/// 週次系列のレスポンス。散布図（気温×売上数量）と重回帰シミュレーションの素材。
/// </summary>
/// <param name="Area">エリア種別（"standard"/"cold"/"warm"）。</param>
/// <param name="AreaCity">参照観測地点名（"東京"/"札幌"/"那覇"）。</param>
/// <param name="Points">週次の系列（取込日昇順）。</param>
public sealed record WeeklySeriesResponse(
    string Area,
    string AreaCity,
    IReadOnlyList<WeeklySeriesPoint> Points);

/// <summary>消化率×値引き率 散布図の1点（型番＝業態×記号×品番 単位）。</summary>
/// <param name="Key">一意キー（業態|記号|品番）。</param>
/// <param name="Label">表示ラベル（品番3桁）。</param>
/// <param name="BusinessType">業態コード。</param>
/// <param name="SellThroughRate">消化率（%、最新週: 累計売上数 ÷ 累計納品数）。</param>
/// <param name="MarkdownRate">値引き率（%、1 − 平均売価 ÷ マスタ定価。0..100 にクランプ）。</param>
/// <param name="Quantity">期間内売上数量（バブルサイズ用）。</param>
/// <param name="Season">季節区分（商品次元の属性。未設定は null）。</param>
/// <param name="Stock">店頭在庫数（最新週スナップショット）。</param>
/// <param name="StockDays">平均在庫日数＝在日（最新週スナップショットの平均）。</param>
public sealed record MarkdownScatterPoint(
    string Key,
    string Label,
    string BusinessType,
    double SellThroughRate,
    double MarkdownRate,
    long Quantity,
    string? Season,
    long Stock,
    double StockDays);

/// <summary>
/// 消化率×値引き率 散布図のレスポンス。値引き率はマスタ定価（m_product_sku.sales_price）が
/// 必要なため、マスタ未登録の型番は対象外。象限分類はフロントで行う表示射影。
/// </summary>
/// <param name="LatestWeek">消化率の基準週（データなしは null）。</param>
/// <param name="Points">型番単位の点。</param>
public sealed record MarkdownScatterResponse(
    string? LatestWeek,
    IReadOnlyList<MarkdownScatterPoint> Points);

// ============================================================
// 商品導入管理（mart）
// 導入日（sales_weekly.donyu_date）を dim_sku.attributes->>'donyu' に保持し、
// 商品（業態×記号×品番）単位で導入状況を一覧する。
// ============================================================

/// <summary>商品導入管理の1行（商品＝業態×記号×品番 単位）。</summary>
/// <param name="ChannelCode">業態コード。</param>
/// <param name="ChannelName">業態表示名（マスタ未登録は null）。</param>
/// <param name="DepartmentCode">部門コード。</param>
/// <param name="DepartmentName">部門名（商品マスタ由来。未登録は null）。</param>
/// <param name="ProductSign">商品記号。</param>
/// <param name="ProductCode">品番CD（服種）。</param>
/// <param name="ProductName">商品名。</param>
/// <param name="Brand">ブランド（商品マスタ由来）。</param>
/// <param name="Manager">担当者（商品マスタ由来）。</param>
/// <param name="DonyuDate">導入日（SKU の最も早い導入日。未設定・不正値は null）。</param>
/// <param name="SkuCount">SKU 数。</param>
/// <param name="SalesQuantity">全期間の売上数量。</param>
/// <param name="PrimaryImageUrl">代表画像URL（無ければ null）。</param>
public sealed record MartIntroductionRow(
    string ChannelCode,
    string? ChannelName,
    string? DepartmentCode,
    string? DepartmentName,
    string ProductSign,
    string ProductCode,
    string? ProductName,
    string? Brand,
    string? Manager,
    DateOnly? DonyuDate,
    int SkuCount,
    long SalesQuantity,
    string? PrimaryImageUrl);

/// <summary>商品導入管理一覧のページ。</summary>
public sealed record MartIntroductionPage(
    IReadOnlyList<MartIntroductionRow> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>
/// 商品導入管理のフィルタ選択肢（mart の商品次元から導出）。
/// 業態・部門の選択肢は既存の <c>/api/filters</c>（<see cref="FilterOptions"/>）を再利用する。
/// </summary>
/// <param name="Brands">ブランドの実値（NULL/空は除外、昇順）。</param>
/// <param name="Managers">担当者の実値（NULL/空は除外、昇順）。</param>
/// <param name="Hinbans">服種＝品番CD の実値（昇順）。</param>
public sealed record MartIntroductionOptions(
    IReadOnlyList<string> Brands,
    IReadOnlyList<string> Managers,
    IReadOnlyList<string> Hinbans);
