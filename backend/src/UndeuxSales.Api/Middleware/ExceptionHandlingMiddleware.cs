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
