namespace UndeuxSales.Core;

/// <summary>
/// エラーコードとHTTPステータスを伴うアプリケーション例外。
/// 想定内のエラー（リクエスト不正・取込失敗等）の伝播に使用する。
/// </summary>
public sealed class AppException : Exception
{
    /// <summary>対応するエラーコード定義。</summary>
    public ErrorCodeInfo Error { get; }

    /// <summary>クライアントへ返すHTTPステータスコード。</summary>
    public int HttpStatus { get; }

    /// <summary>ユーザー向けの補足情報（不正行の一覧など）。</summary>
    public IReadOnlyList<string> Details { get; }

    public AppException(
        ErrorCodeInfo error,
        int httpStatus,
        string? detail = null,
        IReadOnlyList<string>? details = null)
        : base(detail ?? error.Summary)
    {
        Error = error;
        HttpStatus = httpStatus;
        Details = details ?? Array.Empty<string>();
    }
}
