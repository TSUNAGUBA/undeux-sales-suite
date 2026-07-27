using System.Data.Common;
using UndeuxSales.Core;

namespace UndeuxSales.Api.Middleware;

/// <summary>
/// 例外を捕捉し、エラーコード付きの <see cref="ApiError"/> JSON へ変換するミドルウェア。
/// 想定内エラー（<see cref="AppException"/>）・DB例外・想定外例外を区別して扱う。
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException ex)
        {
            _logger.LogWarning(ex,
                "想定内エラー {ErrorCode}: {Message}", ex.Error.Code, ex.Message);
            var detail = ex.Message == ex.Error.Summary ? null : ex.Message;
            var details = ex.Details.Count > 0 ? ex.Details : null;
            await WriteErrorAsync(context, ex.HttpStatus,
                new ApiError(ex.Error.Code, ex.Error.Summary, ex.Error.Remedy, detail, details));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel のリクエストサイズ上限（RequestSizeLimit）超過。
            // 正当入力に近い過大アップロードのため 500（UNDX-SYS-001）ではなく 413 の採番エラーで返す。
            _logger.LogWarning(ex, "リクエストサイズ上限超過のリクエストを拒否しました。");
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge,
                new ApiError(
                    ErrorCodes.UploadTotalTooLarge.Code,
                    ErrorCodes.UploadTotalTooLarge.Summary,
                    ErrorCodes.UploadTotalTooLarge.Remedy));
        }
        catch (InvalidDataException ex) when (IsSizeLimitFailure(ex))
        {
            // multipart 読取の上限（MultipartBodyLengthLimit 等）超過。413 の採番エラーへマップする。
            _logger.LogWarning(ex, "multipart 読取の上限を超過したリクエストを拒否しました。");
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge,
                new ApiError(
                    ErrorCodes.UploadTotalTooLarge.Code,
                    ErrorCodes.UploadTotalTooLarge.Summary,
                    ErrorCodes.UploadTotalTooLarge.Remedy));
        }
        catch (InvalidDataException ex)
        {
            // サイズ系以外の multipart 解析失敗（不正な boundary・Content-Disposition の解析失敗・
            // フォームキー数/ヘッダ数の上限超過等）。413「合計サイズ超過」に化けると、利用者は
            // ファイルを縮小し続けても復帰できないため 400（UNDX-REQ-001）で返す。
            _logger.LogWarning(ex, "リクエストボディ（multipart）の読み取りに失敗しました。");
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                new ApiError(
                    ErrorCodes.InvalidRequest.Code,
                    ErrorCodes.InvalidRequest.Summary,
                    ErrorCodes.InvalidRequest.Remedy,
                    "リクエストの読み取りに失敗しました（送信形式が不正です）。"));
        }
        catch (DbException ex)
        {
            _logger.LogError(ex, "データベースエラーが発生しました。");
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                new ApiError(
                    ErrorCodes.DatabaseError.Code,
                    ErrorCodes.DatabaseError.Summary,
                    ErrorCodes.DatabaseError.Remedy));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // クライアント切断による中断はエラーレスポンス不要。
            _logger.LogDebug("リクエストがクライアントにより中断されました。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "想定外のエラーが発生しました。");
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError,
                new ApiError(
                    ErrorCodes.Unexpected.Code,
                    ErrorCodes.Unexpected.Summary,
                    ErrorCodes.Unexpected.Remedy));
        }
    }

    /// <summary>
    /// multipart / フォーム読取の <see cref="InvalidDataException"/> が「上限超過」によるものか。
    /// <para>
    /// ASP.NET Core は上限超過を「<c>... limit {N} exceeded.</c>」形式のメッセージで throw する
    /// （MultipartReaderStream / MultipartReader / FormReader の各 LengthLimit・CountLimit）。
    /// 上限超過も解析失敗も同一の例外型のため、判別材料はメッセージのみとなる。
    /// </para>
    /// <para>
    /// 誤って非サイズ系を 413「合計サイズ超過」に倒すと、利用者はファイルを縮小し続けても
    /// 復帰できないため、上限超過と確証が持てるものだけを 413 とする安全側の判定にしている。
    /// </para>
    /// <para>
    /// <b>既知の制約（意図的に現状維持）:</b> 判定が ASP.NET Core の英語メッセージ文言に依存するため、
    /// フレームワーク側で文言が変わると本判定が false となり 413 が静かに 400 へ劣化しうる。
    /// ただし劣化方向は「サイズ超過を汎用の 400 として返す」＝<b>安全側</b>であり
    /// （逆方向の「解析失敗を 413 と誤認して復帰不能な案内を出す」は起きない）、
    /// 例外型・プロパティに上限超過を示す公開情報がない以上これ以上の判別材料もないため、
    /// メッセージ一致による判定を維持する。
    /// </para>
    /// </summary>
    private static bool IsSizeLimitFailure(InvalidDataException exception)
        => exception.Message.Contains("limit", StringComparison.OrdinalIgnoreCase)
           && exception.Message.Contains("exceeded", StringComparison.OrdinalIgnoreCase);

    private async Task WriteErrorAsync(HttpContext context, int statusCode, ApiError error)
    {
        if (context.Response.HasStarted)
        {
            _logger.LogWarning(
                "レスポンス送信開始後のため、エラー {ErrorCode} を返却できません。", error.ErrorCode);
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(error, context.RequestAborted);
    }
}
