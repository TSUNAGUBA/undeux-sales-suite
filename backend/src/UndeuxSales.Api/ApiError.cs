namespace UndeuxSales.Api;

/// <summary>APIのエラーレスポンス本文。エラーコードと対処方法を含む。</summary>
/// <param name="ErrorCode">エラーコード（UNDX-xxx-nnn）。</param>
/// <param name="Summary">エラー内容の概要。</param>
/// <param name="Remedy">想定される対処方法。</param>
/// <param name="Detail">補足メッセージ（任意）。</param>
/// <param name="Details">行単位のエラーなどの詳細リスト（任意）。</param>
public sealed record ApiError(
    string ErrorCode,
    string Summary,
    string Remedy,
    string? Detail = null,
    IReadOnlyList<string>? Details = null);
