using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Core;
using UndeuxSales.Core.Rag;
using UndeuxSales.Infrastructure.Rag;

namespace UndeuxSales.Api.Controllers;

/// <summary>チャットメッセージ（リクエスト用）。</summary>
public sealed record ChatMessageDto(string? Role, string? Content);

/// <summary>業務チャット要求。</summary>
public sealed record BusinessChatRequest(string? Domain, List<ChatMessageDto>? Messages);

/// <summary>商談チャット要求。</summary>
public sealed record NegotiationChatRequest(
    string? BusinessTypeCode, string? DeptCode, List<ChatMessageDto>? Messages);

/// <summary>
/// AI チャット（業務チャット／商談シミュレーション）。応答は SSE ストリーミング。
/// <para>
/// イベント: sources（参照ナレッジ、応答前に1回）→ delta（テキスト断片）→ done（トークン使用量）。
/// ストリーム開始前のエラーは通常の ApiError（JSON）、開始後のエラーは error イベントで返す。
/// AI 未設定（API キーなし）の場合は 503 UNDX-AI-008 を返す（グレースフルデグラデーション）。
/// </para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/chat")]
public sealed class ChatController : ControllerBase
{
    // SSE データは HTML に埋め込まれないため、日本語を \uXXXX へエスケープせず可読なまま送る。
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ChatContextService _contextService;
    private readonly IAiChatClient _aiClient;
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        ChatContextService contextService,
        IAiChatClient aiClient,
        ILogger<ChatController> logger)
    {
        _contextService = contextService;
        _aiClient = aiClient;
        _logger = logger;
    }

    /// <summary>業務チャット（システム部／商品管理部／物流部を先に指定）。</summary>
    [HttpPost("business")]
    public async Task Business([FromBody] BusinessChatRequest request, CancellationToken cancellationToken)
    {
        EnsureAiConfigured();
        var preparation = await _contextService.PrepareBusinessChatAsync(
            request.Domain ?? string.Empty, MapMessages(request.Messages), cancellationToken);
        await StreamResponseAsync(preparation, cancellationToken);
    }

    /// <summary>商談チャット（業態×部門のバイヤーシミュレーション）。</summary>
    [HttpPost("negotiation")]
    public async Task Negotiation([FromBody] NegotiationChatRequest request, CancellationToken cancellationToken)
    {
        EnsureAiConfigured();
        if (string.IsNullOrWhiteSpace(request.BusinessTypeCode) || string.IsNullOrWhiteSpace(request.DeptCode))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "businessTypeCode と deptCode は必須です。");
        }

        var preparation = await _contextService.PrepareNegotiationChatAsync(
            request.BusinessTypeCode, request.DeptCode, MapMessages(request.Messages), cancellationToken);
        await StreamResponseAsync(preparation, cancellationToken);
    }

    private void EnsureAiConfigured()
    {
        if (!_aiClient.IsConfigured)
        {
            throw new AppException(ErrorCodes.AiNotConfigured, 503);
        }
    }

    private static IReadOnlyList<AiChatMessage> MapMessages(List<ChatMessageDto>? messages)
    {
        if (messages is null || messages.Count == 0)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "messages は1件以上必要です。");
        }

        return messages
            .Select(m => new AiChatMessage(m.Role ?? string.Empty, m.Content ?? string.Empty))
            .ToList();
    }

    /// <summary>SSE でチャット応答をストリーミングする。</summary>
    private async Task StreamResponseAsync(ChatPreparation preparation, CancellationToken cancellationToken)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        // nginx 等のリバースプロキシでのバッファリングを抑止する。
        Response.Headers["X-Accel-Buffering"] = "no";

        await WriteEventAsync("sources", new
        {
            sources = preparation.Sources
                .Select(s => new { entryId = s.EntryId, title = s.Title, category = s.Category, seq = s.Seq }),
        }, cancellationToken);

        try
        {
            await foreach (var streamEvent in _aiClient.StreamChatAsync(preparation.Request, cancellationToken))
            {
                if (streamEvent.Type == "delta")
                {
                    await WriteEventAsync("delta", new { text = streamEvent.Text }, cancellationToken);
                }
                else if (streamEvent.Type == "done")
                {
                    // トークン使用量を運用ログへ残す（コスト集計の一次情報。DD-04 §8.4 の簡易版）。
                    _logger.LogInformation(
                        "チャット応答完了（user: {User}, inputTokens: {Input}, outputTokens: {Output}）",
                        RequestIdentity.AuditUserOf(User), streamEvent.InputTokens, streamEvent.OutputTokens);
                    await WriteEventAsync("done", new
                    {
                        inputTokens = streamEvent.InputTokens,
                        outputTokens = streamEvent.OutputTokens,
                    }, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // クライアント切断（停止ボタン等）。正常系として扱う。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "チャット応答のストリーミング中にエラーが発生しました");
            var error = ex is AppException app ? app.Error : ErrorCodes.AiCallFailed;
            if (!cancellationToken.IsCancellationRequested)
            {
                await WriteEventAsync("error", new
                {
                    errorCode = error.Code,
                    summary = error.Summary,
                    remedy = error.Remedy,
                }, CancellationToken.None);
            }
        }
    }

    private async Task WriteEventAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
