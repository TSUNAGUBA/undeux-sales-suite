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

    [Theory]
    // ASP.NET Core が上限超過で throw する実際のメッセージ形式（"... limit {N} exceeded."）。
    [InlineData("Multipart body length limit 26214400 exceeded.")]
    [InlineData("Form value length limit 4194304 exceeded.")]
    [InlineData("Form key count limit 1024 exceeded.")]
    [InlineData("Multipart headers length limit 16384 exceeded.")]
    public async Task InvalidDataExceptionForSizeLimit_IsMappedTo413WithUploadTotalTooLarge(
        string message)
    {
        var (statusCode, error) = await InvokeAsync(new InvalidDataException(message));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, statusCode);
        Assert.Equal(ErrorCodes.UploadTotalTooLarge.Code, error!.ErrorCode);
    }

    [Theory]
    // 上限超過以外の multipart 解析失敗。413「合計サイズ超過」に化けると、利用者はファイルを
    // 縮小し続けても復帰できないため 400（UNDX-REQ-001）へ分岐する。
    [InlineData("Missing content-type boundary.")]
    [InlineData("Unexpected end of Stream, the content may have already been read by another component.")]
    [InlineData("Form section has invalid Content-Disposition value: ")]
    public async Task InvalidDataExceptionForNonSizeFailure_IsMappedTo400WithInvalidRequest(
        string message)
    {
        var (statusCode, error) = await InvokeAsync(new InvalidDataException(message));

        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Equal(ErrorCodes.InvalidRequest.Code, error!.ErrorCode);
        Assert.Contains("読み取りに失敗", error.Detail ?? string.Empty);
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
