using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using UndeuxSales.Api;
using UndeuxSales.Api.Middleware;
using UndeuxSales.Core;

namespace UndeuxSales.Tests.Unit;

/// <summary>
/// ExceptionHandlingMiddleware の例外→ApiError マッピングの単体テスト。
/// 特に transport 層の上限超過（BadHttpRequestException 413 / InvalidDataException）が
/// 500（UNDX-SYS-001）ではなく 413（UNDX-REQ-008）へマップされることを検証する。
/// </summary>
public sealed class ExceptionHandlingMiddlewareTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<(int StatusCode, ApiError? Error)> InvokeAsync(Exception exception)
    {
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw exception, NullLogger<ExceptionHandlingMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var error = string.IsNullOrWhiteSpace(body)
            ? null
            : JsonSerializer.Deserialize<ApiError>(body, JsonOptions);
        return (context.Response.StatusCode, error);
    }

    [Fact]
    public async Task BadHttpRequest413_IsMappedTo413WithUploadTotalTooLarge()
    {
        var (statusCode, error) = await InvokeAsync(
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, statusCode);
        Assert.Equal(ErrorCodes.UploadTotalTooLarge.Code, error!.ErrorCode);
    }

    [Fact]
    public async Task InvalidDataException_IsMappedTo413WithUploadTotalTooLarge()
    {
        var (statusCode, error) = await InvokeAsync(
            new InvalidDataException("Multipart body length limit exceeded."));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, statusCode);
        Assert.Equal(ErrorCodes.UploadTotalTooLarge.Code, error!.ErrorCode);
    }

    [Fact]
    public async Task BadHttpRequestWithOtherStatus_FallsBackToUnexpected500()
    {
        // 413 以外の BadHttpRequestException（不正リクエスト等）は従来どおり汎用 catch に委ねる。
        var (statusCode, error) = await InvokeAsync(
            new BadHttpRequestException("Malformed request.", StatusCodes.Status400BadRequest));

        Assert.Equal(StatusCodes.Status500InternalServerError, statusCode);
        Assert.Equal(ErrorCodes.Unexpected.Code, error!.ErrorCode);
    }

    [Fact]
    public async Task AppException_IsMappedToItsStatusAndCode()
    {
        var (statusCode, error) = await InvokeAsync(
            new AppException(ErrorCodes.UploadTotalTooLarge, 413, "合計サイズ超過テスト"));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, statusCode);
        Assert.Equal(ErrorCodes.UploadTotalTooLarge.Code, error!.ErrorCode);
        Assert.Equal("合計サイズ超過テスト", error.Detail);
    }
}
