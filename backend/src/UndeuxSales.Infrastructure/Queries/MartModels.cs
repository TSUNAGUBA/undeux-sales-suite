namespace UndeuxSales.Infrastructure.Queries;

// ============================================================
//  分析 mart（スタースキーマ）の返却モデル
//  docs/star-schema-design.md。フロー指標（数量・金額・粗利）を
//  事前計算済みファクト mart.fact_sales_weekly から集計する。
//  在庫スナップショット系（在庫・消化率）は後続イテレーションで追加。
// ============================================================

/// <summary>mart 全社サマリーの主要KPI（週次フロー指標）。</summary>
public sealed record MartKpi(
    long Quantity,
    long Amount,
    long GrossProfit,
    double GrossProfitRate,
    int ProductCount,
    int SkuCount,
    DateOnly? LatestWeek);

/// <summary>mart 全社サマリーのレスポンス（KPI＋週次トレンド）。</summary>
public sealed record MartSummaryResponse(MartKpi Kpi, IReadOnlyList<TrendPoint> WeeklyTrend);

/// <summary>mart 集計軸別分析のレスポンス。<see cref="BreakdownRow"/> を sales 系と共有する。</summary>
public sealed record MartBreakdownResponse(string Dimension, IReadOnlyList<BreakdownRow> Rows);

/// <summary>mart（スタースキーマ）の構築状態。フロントの再構築UI・鮮度表示に使う。</summary>
public sealed record MartStatus(
    bool Built,
    DateTime? RebuiltAt,
    long SourceRows,
    long FactRows,
    DateOnly? EarliestWeek,
    DateOnly? LatestWeek);
