namespace UndeuxSales.Infrastructure.Import;

/// <summary>取込処理1回の結果。</summary>
/// <param name="BatchId">取込バッチID。</param>
/// <param name="RowCount">取り込んだ行数。</param>
/// <param name="WeekCount">取り込んだ取込日（週）の種類数。</param>
/// <param name="MinImportDate">取り込んだ最古の取込日。</param>
/// <param name="MaxImportDate">取り込んだ最新の取込日。</param>
public sealed record ImportResult(
    long BatchId,
    int RowCount,
    int WeekCount,
    DateOnly? MinImportDate,
    DateOnly? MaxImportDate);

/// <summary>取込バッチ履歴の1件。</summary>
public sealed record ImportBatchInfo(
    long Id,
    string SourceType,
    string FileName,
    string Status,
    int RowCount,
    int WeekCount,
    DateOnly? MinImportDate,
    DateOnly? MaxImportDate,
    string? ErrorMessage,
    DateTime StartedAt,
    DateTime? CompletedAt);
