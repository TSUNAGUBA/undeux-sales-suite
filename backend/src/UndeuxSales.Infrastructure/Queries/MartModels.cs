namespace UndeuxSales.Infrastructure.Queries;

// ============================================================
//  分析 mart（スタースキーマ）の返却モデル
//  docs/star-schema-design.md。フロー指標（数量・金額・粗利）は
//  事前計算済みファクト mart.fact_sales_weekly から、在庫スナップショット系
//  （在庫・消化率・在日）は mart.fact_inventory_snapshot から集計する。
//  在庫・発注分析・商品別分析・クロス集計・ランキング・散布図/重回帰は、
//  既存 sales 系と同一の返却型（QueryModels）を共有して返す（フロント再利用）。
// ============================================================

/// <summary>
/// mart 全社サマリーの主要KPI（週次フロー指標＋最新週の在庫スナップショット）。
/// <see cref="SalesKpi"/> の mart 版。商品数に加え SKU 数も提供する。
/// </summary>
public sealed record MartKpi(
    long Quantity,
    long Amount,
    long GrossProfit,
    double GrossProfitRate,
    int ProductCount,
    int SkuCount,
    long CurrentStock,
    double SellThroughRate,
    DateOnly? LatestWeek);

/// <summary>mart 全社サマリーのレスポンス（KPI＋週次トレンド）。</summary>
public sealed record MartSummaryResponse(MartKpi Kpi, IReadOnlyList<TrendPoint> WeeklyTrend);

/// <summary>mart 集計軸別分析のレスポンス。<see cref="BreakdownRow"/> を sales 系と共有する。</summary>
public sealed record MartBreakdownResponse(string Dimension, IReadOnlyList<BreakdownRow> Rows);

/// <summary>
/// mart（スタースキーマ）の構築状態。フロントの再構築UI・鮮度表示・進捗ポーリングに使う。
/// </summary>
/// <param name="Built">ファクトに行があるか（=分析表示が可能か）。</param>
/// <param name="Status">再構築の状態（idle / running / completed / failed）。</param>
/// <param name="Error">失敗時のエラーメッセージ（それ以外は null）。</param>
/// <param name="StartedAt">直近の再構築開始時刻。</param>
public sealed record MartStatus(
    bool Built,
    string Status,
    string? Error,
    DateTime? StartedAt,
    DateTime? RebuiltAt,
    long SourceRows,
    long FactRows,
    DateOnly? EarliestWeek,
    DateOnly? LatestWeek);
