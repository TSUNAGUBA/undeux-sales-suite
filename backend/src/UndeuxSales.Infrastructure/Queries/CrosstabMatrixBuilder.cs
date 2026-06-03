using UndeuxSales.Core;
using UndeuxSales.Core.Models;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>Dapper マッピング用のクロス集計フロー行（行×列キーと数量・金額・粗利・在日）。</summary>
internal sealed record CrosstabFlowRow(
    string RowKey,
    string ColKey,
    long Quantity,
    long Amount,
    long GrossProfit,
    double StockDays);

/// <summary>Dapper マッピング用のクロス集計スナップショット行（行×列キーと在庫・累計）。</summary>
internal sealed record CrosstabSnapshotRow(
    string RowKey,
    string ColKey,
    long Stock,
    long CumulativeSales,
    long CumulativeDelivery);

/// <summary>
/// クロス集計マトリクスの「組み立て」を担う共有ビルダー。SQL 生成・ディメンション解決は各リポジトリ
/// （<see cref="SalesAnalyticsRepository"/> / <see cref="MartAnalyticsRepository"/>）が担い、
/// 集計済みのフロー行・スナップショット行から表示用マトリクス（ラベルソート・切り詰め・合計整合・
/// 気温オーバーレイ）を構築する処理だけをここに集約する（プレゼンテーション非依存・DRY）。
/// </summary>
internal static class CrosstabMatrixBuilder
{
    /// <summary>未設定（NULL/空文字）のラベル代替表記。</summary>
    public const string UnsetLabel = "(未設定)";

    /// <summary>クロス集計マトリクス表示の最大件数（行・列それぞれ）。</summary>
    private const int MaxAxisLabels = 100;

    /// <summary>全7メトリクスのキー（フロントエンド側と一致）。</summary>
    public static readonly IReadOnlyList<string> AllMetricKeys = new[]
    {
        "amount", "quantity", "grossProfit", "sharePercent",
        "stockDays", "sellThroughRate", "stock",
    };

    /// <summary>在庫系メトリクス（最新週スナップショット基準のため、時間軸と組み合わせ不可）。</summary>
    private static readonly HashSet<string> StockMetrics = new()
    {
        "stockDays", "sellThroughRate", "stock",
    };

    /// <summary>気温系メトリクス（時間軸＋エリア種別が指定された場合のみ利用可能）。</summary>
    private static readonly IReadOnlyList<string> TemperatureMetrics = new[]
    {
        "tempAvg", "tempMax", "tempMin",
    };

    /// <summary>時間軸ディメンション。行・列のいずれかに含まれる場合、在庫系メトリクスは null とする。</summary>
    private static readonly HashSet<CrosstabDimension> TimeDimensions = new()
    {
        CrosstabDimension.TimeYear,
        CrosstabDimension.TimeQuarter,
        CrosstabDimension.TimeMonth,
    };

    /// <summary>時間軸ディメンション（年/四半期/月）かどうか。</summary>
    public static bool IsTimeDimension(CrosstabDimension dim) => TimeDimensions.Contains(dim);

    /// <summary>
    /// <see cref="CrosstabDimension"/> からフロント側に返す情報（Key/Category/Label/IsTimeAxis）を生成する。
    /// sales 系・mart 系で共通（表示メタ情報はデータソースに依存しない）。
    /// </summary>
    public static CrosstabDimensionInfo DimensionInfo(CrosstabDimension dim) => dim switch
    {
        CrosstabDimension.TimeYear => new CrosstabDimensionInfo("time:year", "time", "年", true),
        CrosstabDimension.TimeQuarter => new CrosstabDimensionInfo("time:quarter", "time", "四半期", true),
        CrosstabDimension.TimeMonth => new CrosstabDimensionInfo("time:month", "time", "月", true),
        CrosstabDimension.CategoryDepartment => new CrosstabDimensionInfo("category:department", "category", "部門", false),
        CrosstabDimension.CategoryBusinessType => new CrosstabDimensionInfo("category:businessType", "category", "業態", false),
        CrosstabDimension.CategorySeason => new CrosstabDimensionInfo("category:season", "category", "季節区分", false),
        CrosstabDimension.CategoryHinban => new CrosstabDimensionInfo("category:hinban", "category", "品番3桁", false),
        CrosstabDimension.CategoryProduct => new CrosstabDimensionInfo("category:product", "category", "単品（品番-単品）", false),
        CrosstabDimension.CategoryColor => new CrosstabDimensionInfo("category:color", "category", "カラー", false),
        CrosstabDimension.CategorySize => new CrosstabDimensionInfo("category:size", "category", "サイズ", false),
        CrosstabDimension.CategoryChohyoKubun => new CrosstabDimensionInfo("category:chohyoKubun", "category", "帳票区分", false),
        CrosstabDimension.CategoryTanawari1 => new CrosstabDimensionInfo("category:tanawari1", "category", "棚割1", false),
        CrosstabDimension.CategoryTanawari2 => new CrosstabDimensionInfo("category:tanawari2", "category", "棚割2", false),
        CrosstabDimension.CategoryShohinKigo => new CrosstabDimensionInfo("category:shohinKigo", "category", "商品記号", false),
        _ => throw new AppException(ErrorCodes.UnknownDimension, 400),
    };

    /// <summary>
    /// 利用可能なメトリクスキー一覧を解決する。時間軸ありは在庫系を除外し、エリア種別が有効なら気温系を追加。
    /// 時間軸なしは在庫系を含む全メトリクス（気温は時間バケットが無いため対象外）。
    /// </summary>
    public static List<string> ResolveAvailableMetrics(bool hasTimeAxis, bool temperatureActive)
    {
        if (!hasTimeAxis)
        {
            return AllMetricKeys.ToList();
        }

        var metrics = AllMetricKeys.Where(m => !StockMetrics.Contains(m)).ToList();
        if (temperatureActive)
        {
            metrics.AddRange(TemperatureMetrics);
        }

        return metrics;
    }

    /// <summary>空マトリクス（データなし時）を返す。</summary>
    public static CrosstabMatrixResponse BuildEmpty(
        CrosstabDimensionInfo rowInfo,
        CrosstabDimensionInfo colInfo,
        IReadOnlyList<string> availableMetrics) => new(
        rowInfo,
        colInfo,
        Array.Empty<string>(),
        Array.Empty<string>(),
        new Dictionary<string, IReadOnlyDictionary<string, CrosstabCell>>(),
        new Dictionary<string, CrosstabCell>(),
        new Dictionary<string, CrosstabCell>(),
        new CrosstabCell(new CrosstabCellValues(null, null, null, null, null, null, null)),
        null,
        availableMetrics,
        false,
        false);

    /// <summary>
    /// 集計結果（flow + snapshot）からレスポンスを構築する。
    /// 行・列ラベルのソート、合計算出、メトリクス値の null 制御をここで行う。
    ///
    /// 切り詰めと合計の整合性を保つため、行・列ラベル確定後に対象キーのみで
    /// 行/列/総計を再構築する：
    /// - 切り詰めされたラベル集合に含まれないキーの flow/snapshot は集計から除外する
    /// - これにより rowTotals == sum(visible cells)、grandTotal == sum(rowTotals) == sum(columnTotals)
    ///   が表示マトリクスと完全に一致する（sharePercent の合計も 100% になる）
    /// </summary>
    public static CrosstabMatrixResponse Build(
        IReadOnlyList<CrosstabFlowRow> flowRows,
        IReadOnlyDictionary<(string Row, string Col), CrosstabSnapshotRow> snapshotMap,
        CrosstabDimensionInfo rowInfo,
        CrosstabDimensionInfo colInfo,
        bool hasTimeAxis,
        IReadOnlyList<string> availableMetrics,
        DateOnly? latestWeek,
        CrosstabDimension rowDim,
        CrosstabDimension colDim,
        TemperatureArea? temperatureArea)
    {
        // 1) ラベル順序確定用の暫定集計（全データ）。amount 降順でラベル順を決めるためにだけ使う。
        var initialRowAggregates = new Dictionary<string, FlowAggregate>();
        var initialColAggregates = new Dictionary<string, FlowAggregate>();
        foreach (var row in flowRows)
        {
            if (!initialRowAggregates.TryGetValue(row.RowKey, out var ra))
            {
                ra = new FlowAggregate();
                initialRowAggregates[row.RowKey] = ra;
            }
            ra.Add(row);

            if (!initialColAggregates.TryGetValue(row.ColKey, out var ca))
            {
                ca = new FlowAggregate();
                initialColAggregates[row.ColKey] = ca;
            }
            ca.Add(row);
        }

        // 2) 行・列ラベルのソート（時間軸: 文字列昇順、カテゴリ軸: amount 降順、未設定は末尾）
        var rowLabels = SortLabels(initialRowAggregates, rowInfo);
        var columnLabels = SortLabels(initialColAggregates, colInfo);

        // 3) 最大100件で切り詰める（truncated フラグを記録）
        var rowTruncated = rowLabels.Count > MaxAxisLabels;
        var columnTruncated = columnLabels.Count > MaxAxisLabels;
        if (rowTruncated)
        {
            rowLabels = rowLabels.Take(MaxAxisLabels).ToList();
        }
        if (columnTruncated)
        {
            columnLabels = columnLabels.Take(MaxAxisLabels).ToList();
        }
        var rowSet = new HashSet<string>(rowLabels);
        var colSet = new HashSet<string>(columnLabels);

        // 3.5) 気温オーバーレイ。時間軸（行 or 列）の各ラベルが表す期間に対する標準気候を算出する。
        //      気温は売上行の集計ではなく時間バケットの期間だけで決まるため、同一時間ラベルの
        //      全セルで同じ値になる。時間軸を跨いだ合計（全体合計・非時間軸方向の小計）は全体平均
        //      （平均=各ラベル平均の平均、最高=最高の最大、最低=最低の最小）を用いる。
        var temperatureActive = temperatureArea.HasValue && hasTimeAxis;
        var temperatureByLabel = new Dictionary<string, TemperatureReading>(StringComparer.Ordinal);
        TemperatureReading? temperatureOverall = null;
        var tempAxisIsRow = false;
        if (temperatureActive)
        {
            tempAxisIsRow = string.Equals(rowInfo.Category, "time", StringComparison.Ordinal);
            var timeDim = tempAxisIsRow ? rowDim : colDim;
            var timeLabels = tempAxisIsRow ? rowLabels : columnLabels;
            double sumAvg = 0;
            var maxOut = double.NegativeInfinity;
            var minOut = double.PositiveInfinity;
            var count = 0;
            foreach (var label in timeLabels)
            {
                var range = TimeLabelRange(timeDim, label);
                if (range is null)
                {
                    continue;
                }
                var reading = ClimateModel.Range(temperatureArea!.Value, range.Value.Start, range.Value.End);
                temperatureByLabel[label] = reading;
                sumAvg += reading.Average;
                if (reading.Maximum > maxOut) maxOut = reading.Maximum;
                if (reading.Minimum < minOut) minOut = reading.Minimum;
                count++;
            }
            if (count > 0)
            {
                temperatureOverall = new TemperatureReading(sumAvg / count, maxOut, minOut);
            }
        }

        // セル（rowLabel × colLabel）の気温。時間軸ラベル側の値を引く（非時間軸の値には依存しない）。
        TemperatureReading? CellTemperature(string rowLabel, string colLabel)
        {
            if (!temperatureActive)
            {
                return null;
            }
            var key = tempAxisIsRow ? rowLabel : colLabel;
            return temperatureByLabel.TryGetValue(key, out var reading) ? reading : (TemperatureReading?)null;
        }

        // 4) 切り詰め後の集計を再構築。表示対象セル（rowSet × colSet）のみを対象に
        //    rowAggregates / colAggregates / grand / cells を構築することで、
        //    rowTotals == sum(visible cells per row) と grandTotal == sum(rowTotals) を一致させる。
        var rowAggregates = new Dictionary<string, FlowAggregate>();
        var colAggregates = new Dictionary<string, FlowAggregate>();
        var grand = new FlowAggregate();
        var cells = new Dictionary<string, IReadOnlyDictionary<string, CrosstabCell>>();

        // フローセル：amount 等は flowRows から積み上げるが grandAmount は cell 構築より先に
        // 確定する必要があるため、いったん集計用にループしたあと、改めてセル化する。
        foreach (var row in flowRows)
        {
            if (!rowSet.Contains(row.RowKey) || !colSet.Contains(row.ColKey))
            {
                continue;
            }
            if (!rowAggregates.TryGetValue(row.RowKey, out var ra))
            {
                ra = new FlowAggregate();
                rowAggregates[row.RowKey] = ra;
            }
            if (!colAggregates.TryGetValue(row.ColKey, out var ca))
            {
                ca = new FlowAggregate();
                colAggregates[row.ColKey] = ca;
            }
            ra.Add(row);
            ca.Add(row);
            grand.Add(row);
        }

        // スナップショット：行/列/全体合計を 1 ループで構築（O(N²) を O(N) に改善）。
        Dictionary<string, FlowSnapshotAggregate>? rowSnapshotAggregates = null;
        Dictionary<string, FlowSnapshotAggregate>? colSnapshotAggregates = null;
        FlowSnapshotAggregate? grandSnapshot = null;
        if (!hasTimeAxis)
        {
            rowSnapshotAggregates = new Dictionary<string, FlowSnapshotAggregate>();
            colSnapshotAggregates = new Dictionary<string, FlowSnapshotAggregate>();
            grandSnapshot = new FlowSnapshotAggregate();
            foreach (var ((rKey, cKey), snap) in snapshotMap)
            {
                // 切り詰めにより表示対象外となったキーのスナップショットも合計から除外する。
                if (!rowSet.Contains(rKey) || !colSet.Contains(cKey))
                {
                    continue;
                }
                if (!rowSnapshotAggregates.TryGetValue(rKey, out var rs))
                {
                    rs = new FlowSnapshotAggregate();
                    rowSnapshotAggregates[rKey] = rs;
                }
                if (!colSnapshotAggregates.TryGetValue(cKey, out var cs))
                {
                    cs = new FlowSnapshotAggregate();
                    colSnapshotAggregates[cKey] = cs;
                }
                rs.Add(snap);
                cs.Add(snap);
                grandSnapshot.Add(snap);
            }
        }

        var grandAmount = grand.Amount;

        // 5) セルマップ構築（grandAmount 確定後に sharePercent を計算する）
        foreach (var row in flowRows)
        {
            if (!rowSet.Contains(row.RowKey) || !colSet.Contains(row.ColKey))
            {
                continue;
            }

            if (!cells.TryGetValue(row.RowKey, out var rowCells))
            {
                rowCells = new Dictionary<string, CrosstabCell>();
                cells[row.RowKey] = rowCells;
            }

            CrosstabSnapshotRow? snap = null;
            if (!hasTimeAxis && snapshotMap.TryGetValue((row.RowKey, row.ColKey), out var s))
            {
                snap = s;
            }

            ((Dictionary<string, CrosstabCell>)rowCells)[row.ColKey] = BuildCell(
                row.Amount,
                row.Quantity,
                row.GrossProfit,
                row.StockDays,
                snap,
                grandAmount,
                hasTimeAxis,
                CellTemperature(row.RowKey, row.ColKey));
        }

        // 6) 行合計セル / 列合計セル / 総計セル
        var rowTotals = new Dictionary<string, CrosstabCell>();
        foreach (var label in rowLabels)
        {
            rowAggregates.TryGetValue(label, out var agg);
            agg ??= new FlowAggregate();
            CrosstabSnapshotRow? rowSnap = null;
            if (rowSnapshotAggregates != null
                && rowSnapshotAggregates.TryGetValue(label, out var rs))
            {
                rowSnap = rs.ToRow(label, string.Empty);
            }
            // 行が時間軸ならその行ラベルの気温、そうでなければ（列が時間軸）全体平均。
            var rowTemp = !temperatureActive
                ? (TemperatureReading?)null
                : tempAxisIsRow
                    ? (temperatureByLabel.TryGetValue(label, out var rt) ? rt : (TemperatureReading?)null)
                    : temperatureOverall;
            rowTotals[label] = BuildCell(
                agg.Amount, agg.Quantity, agg.GrossProfit, agg.AverageStockDays(),
                rowSnap, grandAmount, hasTimeAxis, rowTemp);
        }

        var columnTotals = new Dictionary<string, CrosstabCell>();
        foreach (var label in columnLabels)
        {
            colAggregates.TryGetValue(label, out var agg);
            agg ??= new FlowAggregate();
            CrosstabSnapshotRow? colSnap = null;
            if (colSnapshotAggregates != null
                && colSnapshotAggregates.TryGetValue(label, out var cs))
            {
                colSnap = cs.ToRow(string.Empty, label);
            }
            // 列が時間軸ならその列ラベルの気温、そうでなければ（行が時間軸）全体平均。
            var colTemp = !temperatureActive
                ? (TemperatureReading?)null
                : !tempAxisIsRow
                    ? (temperatureByLabel.TryGetValue(label, out var ct) ? ct : (TemperatureReading?)null)
                    : temperatureOverall;
            columnTotals[label] = BuildCell(
                agg.Amount, agg.Quantity, agg.GrossProfit, agg.AverageStockDays(),
                colSnap, grandAmount, hasTimeAxis, colTemp);
        }

        var grandSnap = grandSnapshot?.ToRow(string.Empty, string.Empty);
        var grandTotal = BuildCell(
            grand.Amount, grand.Quantity, grand.GrossProfit, grand.AverageStockDays(),
            grandSnap, grandAmount, hasTimeAxis, temperatureOverall);

        return new CrosstabMatrixResponse(
            rowInfo,
            colInfo,
            rowLabels,
            columnLabels,
            cells,
            rowTotals,
            columnTotals,
            grandTotal,
            latestWeek.HasValue ? latestWeek.Value.ToString("yyyy-MM-dd") : null,
            availableMetrics,
            rowTruncated,
            columnTruncated);
    }

    private static List<string> SortLabels(
        Dictionary<string, FlowAggregate> aggregates,
        CrosstabDimensionInfo info)
    {
        var labels = aggregates.Keys.ToList();
        if (string.Equals(info.Category, "time", StringComparison.Ordinal))
        {
            // 時間軸: 文字列の昇順（YYYY / YYYY-Qn / YYYY-MM はそれで時系列順になる）
            // ただし import_date が NULL（→ '(未設定)' に置換）の行は時系列の前後関係を持たないため
            // カテゴリ軸と同様に末尾に固定する。
            labels.Sort((a, b) =>
            {
                var aUnset = a == UnsetLabel;
                var bUnset = b == UnsetLabel;
                if (aUnset != bUnset)
                {
                    return aUnset ? 1 : -1;
                }
                return StringComparer.Ordinal.Compare(a, b);
            });
        }
        else
        {
            // 行小計の amount 降順、未設定（'(未設定)'）は末尾
            labels.Sort((a, b) =>
            {
                var aUnset = a == UnsetLabel;
                var bUnset = b == UnsetLabel;
                if (aUnset != bUnset)
                {
                    return aUnset ? 1 : -1;
                }
                var cmp = aggregates[b].Amount.CompareTo(aggregates[a].Amount);
                return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a, b);
            });
        }
        return labels;
    }

    /// <summary>
    /// セル値を構築する。在庫系は時間軸絡みなら null、構成比率は amount/grandAmount × 100。
    /// 気温は時間軸＋エリア種別が指定された場合のみ <paramref name="temperature"/> から設定する。
    /// </summary>
    private static CrosstabCell BuildCell(
        long amount,
        long quantity,
        long grossProfit,
        double? stockDays,
        CrosstabSnapshotRow? snap,
        long grandAmount,
        bool hasTimeAxis,
        TemperatureReading? temperature)
    {
        var sharePercent = grandAmount == 0
            ? (double?)null
            : (double)amount / grandAmount * 100.0;

        long? stock;
        double? sellThroughRate;
        double? stockDaysOut;
        if (hasTimeAxis)
        {
            stock = null;
            sellThroughRate = null;
            stockDaysOut = null;
        }
        else
        {
            stock = snap?.Stock;
            sellThroughRate = snap == null || snap.CumulativeDelivery == 0
                ? (double?)null
                : (double)snap.CumulativeSales / snap.CumulativeDelivery * 100.0;
            stockDaysOut = stockDays;
        }

        return new CrosstabCell(new CrosstabCellValues(
            amount,
            quantity,
            grossProfit,
            sharePercent,
            stockDaysOut,
            sellThroughRate,
            stock,
            temperature?.Average,
            temperature?.Maximum,
            temperature?.Minimum));
    }

    /// <summary>
    /// 時間軸ラベル（"2024" / "2024-Q2" / "2024-05"）が表す暦上の期間を返す。
    /// 気温オーバーレイの期間気候算出に用いる。未設定ラベルや解析不能なラベルは <c>null</c>。
    /// </summary>
    private static (DateOnly Start, DateOnly End)? TimeLabelRange(CrosstabDimension dim, string label)
    {
        if (string.Equals(label, UnsetLabel, StringComparison.Ordinal))
        {
            return null;
        }

        switch (dim)
        {
            case CrosstabDimension.TimeYear:
            {
                if (!int.TryParse(label, out var year) || year is < 1 or > 9999)
                {
                    return null;
                }
                return (new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
            }
            case CrosstabDimension.TimeQuarter:
            {
                var parts = label.Split("-Q", StringSplitOptions.None);
                if (parts.Length != 2
                    || !int.TryParse(parts[0], out var year) || year is < 1 or > 9999
                    || !int.TryParse(parts[1], out var quarter) || quarter is < 1 or > 4)
                {
                    return null;
                }
                var start = new DateOnly(year, (quarter - 1) * 3 + 1, 1);
                return (start, start.AddMonths(3).AddDays(-1));
            }
            case CrosstabDimension.TimeMonth:
            {
                var parts = label.Split('-', StringSplitOptions.None);
                if (parts.Length != 2
                    || !int.TryParse(parts[0], out var year) || year is < 1 or > 9999
                    || !int.TryParse(parts[1], out var month) || month is < 1 or > 12)
                {
                    return null;
                }
                var start = new DateOnly(year, month, 1);
                return (start, start.AddMonths(1).AddDays(-1));
            }
            default:
                return null;
        }
    }

    /// <summary>クロス集計のフロー指標（数量・金額・粗利・在日）の集計累計。</summary>
    private sealed class FlowAggregate
    {
        public long Quantity { get; private set; }
        public long Amount { get; private set; }
        public long GrossProfit { get; private set; }
        private double _stockDaysSum;
        private int _stockDaysCount;

        public void Add(CrosstabFlowRow row)
        {
            Quantity += row.Quantity;
            Amount += row.Amount;
            GrossProfit += row.GrossProfit;
            _stockDaysSum += row.StockDays;
            _stockDaysCount += 1;
        }

        /// <summary>セルごとの zainiti AVG を、行/列合計では単純平均する。データなしは null。</summary>
        public double? AverageStockDays() => _stockDaysCount == 0
            ? (double?)null
            : _stockDaysSum / _stockDaysCount;
    }

    /// <summary>
    /// クロス集計のスナップショット指標（在庫数・累計売上数・累計納品数）の集計累計。
    /// 行ラベル単位・列ラベル単位・総計を一度のループで構築するために使う（O(N×M) を O(N) に改善）。
    /// </summary>
    private sealed class FlowSnapshotAggregate
    {
        private long _stock;
        private long _sales;
        private long _delivery;
        private bool _hasData;

        public void Add(CrosstabSnapshotRow snap)
        {
            _stock += snap.Stock;
            _sales += snap.CumulativeSales;
            _delivery += snap.CumulativeDelivery;
            _hasData = true;
        }

        /// <summary>累計の <see cref="CrosstabSnapshotRow"/> に変換する。データなしは null。</summary>
        public CrosstabSnapshotRow? ToRow(string rowKey, string colKey)
            => _hasData
                ? new CrosstabSnapshotRow(rowKey, colKey, _stock, _sales, _delivery)
                : null;
    }
}
