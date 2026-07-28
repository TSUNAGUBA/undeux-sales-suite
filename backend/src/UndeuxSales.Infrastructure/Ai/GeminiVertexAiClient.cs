using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UndeuxSales.Core;
using UndeuxSales.Core.Rag;

namespace UndeuxSales.Infrastructure.Ai;

/// <summary>
/// Google Vertex AI（Gemini）クライアント。REST（generateContent / streamGenerateContent）を
/// <see cref="IHttpClientFactory"/> の HttpClient で呼び出す。
/// <para>
/// <b>SDK ではなく REST を用いる理由:</b> api コンテナのメモリ上限は 512MiB（GC ヒープハードリミット
/// 384MiB）で余裕が小さい（docs/design.md §13.5）。公式 SDK <c>Google.Cloud.AIPlatform.V1</c> は
/// gRPC/protobuf の重い依存を持ち込むため、軽量な HttpClient + 認証のみ <c>Google.Apis.Auth</c>
/// （ADC/サービスアカウントのトークン取得）で構成し、メモリ収支を守る（原則3の趣旨）。
/// </para>
/// <para>
/// system の安定プレフィックス（役割定義・マスタ文脈）へのプロンプトキャッシュは、Anthropic の
/// <c>cache_control</c> のような明示指定はせず、Gemini 2.5 の暗黙キャッシュに委ねる。
/// ProjectId/認証情報が未設定・初期化失敗のときは <see cref="IsConfigured"/>=false となり、
/// 呼出側が UNDX-AI-008 / 503 で応答する（グレースフルデグラデーション・原則4）。
/// </para>
/// </summary>
public sealed class GeminiVertexAiClient : IAiChatClient
{
    /// <summary>Vertex AI 呼出に使う名前付き HttpClient のキー（DI 登録と一致させる）。</summary>
    internal const string HttpClientName = "vertex-ai";

    /// <summary>Vertex AI（Cloud Platform）アクセス用の OAuth スコープ。</summary>
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    private const int StatusCodes502 = 502;

    /// <summary>送信 JSON は camelCase・null 省略（Gemini の inlineData/systemInstruction 等に一致）。</summary>
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>受信 JSON は camelCase・大小無視で読み取る。</summary>
    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AiOptions _options;
    private readonly ILogger<GeminiVertexAiClient> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    // 認証情報。null＝未設定または初期化失敗（＝AI 無効）。トークンは内部でキャッシュ・自動更新される。
    private readonly GoogleCredential? _credential;

    public GeminiVertexAiClient(
        IOptions<AiOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<GeminiVertexAiClient> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _credential = TryCreateCredential(_options, logger);
    }

    public bool IsConfigured => _credential is not null;

    public string ChatModel => _options.Model;

    public async IAsyncEnumerable<AiStreamEvent> StreamChatAsync(
        AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var payload = new GeminiRequest
        {
            Contents = request.Messages.Select(ToUserOrModelContent).ToList(),
            SystemInstruction = BuildSystemInstruction(request.System),
            GenerationConfig = BuildGenerationConfig(request.MaxTokens),
        };

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var endpoint = BuildEndpoint(_options.Model, "streamGenerateContent", stream: true);
        using var httpRequest = await BuildAuthorizedRequestAsync(endpoint, payload, cancellationToken);
        using var response = await client.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadErrorAsync(response, cancellationToken);
            _logger.LogWarning(
                "Vertex AI ストリーミング呼出が失敗しました（status: {Status}, model: {Model}）: {Body}",
                (int)response.StatusCode, _options.Model, body);
            throw new AppException(ErrorCodes.AiCallFailed, StatusCodes502,
                "AI 呼出に失敗しました。時間をおいて再試行してください。");
        }

        long inputTokens = 0;
        long outputTokens = 0;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            // SSE（alt=sse）は "data: {json}" 行の連なり。data 以外（空行・コメント）は読み飛ばす。
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var json = line[5..].Trim();
            if (json.Length == 0)
            {
                continue;
            }

            var chunk = TryParseChunk(json);
            if (chunk is null)
            {
                continue;
            }

            var text = ExtractText(chunk);
            if (!string.IsNullOrEmpty(text))
            {
                yield return AiStreamEvent.Delta(text);
            }

            // usageMetadata は概ね最終チャンクに付く。届いた値で上書きする（最後の値が最終集計）。
            if (chunk.UsageMetadata is { } usage)
            {
                inputTokens = usage.PromptTokenCount;
                outputTokens = usage.CandidatesTokenCount;
            }
        }

        yield return AiStreamEvent.Done(inputTokens, outputTokens);
    }

    public async Task<string> DescribeImageAsync(
        byte[] imageData, string mediaType, string? hint, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var prompt =
            "この画像はアパレル小売業のナレッジベースに登録される資料です。"
            + "画像に含まれる文字情報は可能な限りすべて書き起こし、図表・写真は内容を日本語で具体的に説明してください。"
            + "検索用テキストとして使うため、装飾的な前置きは不要です。"
            + (string.IsNullOrWhiteSpace(hint) ? string.Empty : $"\n補足情報: {hint}");

        var payload = new GeminiRequest
        {
            Contents = new List<GeminiContent>
            {
                new("user", new List<GeminiPart>
                {
                    new() { InlineData = new GeminiInlineData(ToGeminiMimeType(mediaType), Convert.ToBase64String(imageData)) },
                    new() { Text = prompt },
                }),
            },
            GenerationConfig = BuildGenerationConfig(1024),
        };

        try
        {
            var response = await PostForResponseAsync(_options.VisionModel, payload, cancellationToken);
            return ExtractText(response).Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AppException)
        {
            // SDK/HTTP の生メッセージ（内部文言）はログのみに残し、ユーザー向け詳細は汎用文言にする。
            _logger.LogWarning(ex, "画像説明の生成に失敗しました（モデル: {Model}）", _options.VisionModel);
            throw new AppException(ErrorCodes.AiCallFailed, StatusCodes502,
                "AI 呼出に失敗しました。時間をおいて再試行してください。");
        }
    }

    public async Task<string> AnalyzeImagesAsync(
        IReadOnlyList<AiImageInput> images,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        // ラベル（テキスト）→画像 の順に並べ、末尾にチェック指示（userPrompt）を置く。
        var parts = new List<GeminiPart>();
        foreach (var image in images)
        {
            if (!string.IsNullOrWhiteSpace(image.Label))
            {
                parts.Add(new GeminiPart { Text = image.Label });
            }

            parts.Add(new GeminiPart
            {
                // media type は RFC 9110 上 case-insensitive で、副資材チェックの入力検証も
                // OrdinalIgnoreCase で受理する（"IMAGE/PNG" 等）。完全一致比較で PNG を JPEG と宣言して
                // 送ると Gemini に拒否されて恒久的に失敗するため、境界で png/jpeg に正規化する。
                InlineData = new GeminiInlineData(ToGeminiMimeType(image.MediaType), Convert.ToBase64String(image.Data)),
            });
        }

        parts.Add(new GeminiPart { Text = userPrompt });

        // チェック精度優先で VisionModel ではなくメインモデル（_options.Model）を使用する。
        var payload = new GeminiRequest
        {
            Contents = new List<GeminiContent> { new("user", parts) },
            SystemInstruction = new GeminiSystemInstruction(new List<GeminiPart> { new() { Text = systemPrompt } }),
            GenerationConfig = BuildGenerationConfig(maxTokens),
        };

        GeminiResponse response;
        try
        {
            response = await PostForResponseAsync(_options.Model, payload, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AppException)
        {
            _logger.LogWarning(ex, "画像分析応答の生成に失敗しました（モデル: {Model}）", _options.Model);
            throw new AppException(ErrorCodes.AiCallFailed, StatusCodes502,
                "AI 呼出に失敗しました。時間をおいて再試行してください。");
        }

        var finishReason = response.Candidates is { Count: > 0 } ? response.Candidates[0].FinishReason : null;
        if (string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
        {
            // 出力上限で切り詰められた JSON は解析不能なため、解析前に明示メッセージで失敗させる。
            _logger.LogWarning(
                "画像分析応答が最大出力トークン（{MaxTokens}）で切り詰められました（モデル: {Model}）",
                maxTokens, _options.Model);
            throw new AppException(ErrorCodes.AiCallFailed, StatusCodes502,
                "AI 応答が出力トークン上限で切り詰められました。画像の枚数を減らして再実行してください。");
        }

        return ExtractText(response).Trim();
    }

    // ---- 内部ヘルパー ----------------------------------------------------------------

    private void EnsureConfigured()
    {
        if (_credential is null)
        {
            throw new AppException(ErrorCodes.AiNotConfigured, 503);
        }
    }

    /// <summary>
    /// 認証情報を初期化する。ProjectId 未設定は AI 無効（null）。初期化失敗も起動を止めず null にして
    /// AI 機能のみ無効化する（原則4）。サービスアカウント鍵（base64）指定時はそれを、無指定時は ADC を使う。
    /// </summary>
    private static GoogleCredential? TryCreateCredential(AiOptions options, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectId))
        {
            return null;
        }

        try
        {
            var credential = string.IsNullOrWhiteSpace(options.ServiceAccountJsonBase64)
                ? GoogleCredential.GetApplicationDefault()
                : GoogleCredential.FromJson(DecodeServiceAccountJson(options.ServiceAccountJsonBase64));

            // サービスアカウント鍵はスコープ指定が必須（ADC/メタデータ由来は付与済みのことが多い）。
            if (credential.IsCreateScopedRequired)
            {
                credential = credential.CreateScoped(CloudPlatformScope);
            }

            return credential;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Vertex AI の認証情報を初期化できませんでした。AI 機能は無効になります（IsConfigured=false）。");
            return null;
        }
    }

    private static string DecodeServiceAccountJson(string base64) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));

    /// <summary>
    /// generateContent / streamGenerateContent のエンドポイントを組み立てる。
    /// location="global" のときはホストに接頭辞を付けない（グローバルエンドポイント）。
    /// </summary>
    private Uri BuildEndpoint(string model, string method, bool stream)
    {
        var location = string.IsNullOrWhiteSpace(_options.Location) ? "global" : _options.Location.Trim();
        var host = string.Equals(location, "global", StringComparison.OrdinalIgnoreCase)
            ? "aiplatform.googleapis.com"
            : $"{location}-aiplatform.googleapis.com";
        var url =
            $"https://{host}/v1/projects/{_options.ProjectId}/locations/{location}"
            + $"/publishers/google/models/{model}:{method}";
        if (stream)
        {
            // Server-Sent Events で受け取る（既定の JSON 配列ストリームより逐次解析が容易）。
            url += "?alt=sse";
        }

        return new Uri(url);
    }

    /// <summary>認証トークンを付与した POST リクエストを組み立てる（本文は UTF-8 直列化で1コピーに抑える）。</summary>
    private async Task<HttpRequestMessage> BuildAuthorizedRequestAsync(
        Uri endpoint, GeminiRequest payload, CancellationToken cancellationToken)
    {
        // UnderlyingCredential（ICredential : ITokenAccess）がトークンを内部キャッシュし、失効前に自動更新する。
        var token = await _credential!.UnderlyingCredential.GetAccessTokenForRequestAsync(
            cancellationToken: cancellationToken);

        // 巨大な base64 を含む本文は、UTF-16 文字列を経由せず UTF-8 バイト列へ直接直列化して
        // 余分なコピー（約2倍のピーク）を避ける（メモリ収支・design.md §13.5）。
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, RequestJsonOptions);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>generateContent（非ストリーミング）を呼び、応答を解析して返す。HTTP 失敗は AppException。</summary>
    private async Task<GeminiResponse> PostForResponseAsync(
        string model, GeminiRequest payload, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var endpoint = BuildEndpoint(model, "generateContent", stream: false);
        using var request = await BuildAuthorizedRequestAsync(endpoint, payload, cancellationToken);
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadErrorAsync(response, cancellationToken);
            _logger.LogWarning(
                "Vertex AI 呼出が失敗しました（status: {Status}, model: {Model}）: {Body}",
                (int)response.StatusCode, model, body);
            throw new AppException(ErrorCodes.AiCallFailed, StatusCodes502,
                "AI 呼出に失敗しました。時間をおいて再試行してください。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var parsed = await JsonSerializer.DeserializeAsync<GeminiResponse>(
            stream, ResponseJsonOptions, cancellationToken);
        return parsed ?? throw new AppException(ErrorCodes.AiCallFailed, StatusCodes502,
            "AI 応答が空でした。時間をおいて再試行してください。");
    }

    private GeminiResponse? TryParseChunk(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GeminiResponse>(json, ResponseJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Vertex AI ストリーミングチャンクの解析に失敗しました。");
            return null;
        }
    }

    private static async Task<string> SafeReadErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // ログ肥大を防ぐため先頭のみに切り詰める（原因調査には十分）。
            return body.Length > 500 ? body[..500] : body;
        }
        catch
        {
            return "(応答本文の読取に失敗)";
        }
    }

    private static GeminiContent ToUserOrModelContent(AiChatMessage message) =>
        new(message.Role == "assistant" ? "model" : "user",
            new List<GeminiPart> { new() { Text = message.Content } });

    /// <summary>
    /// generationConfig を組み立てる。Gemini 2.5 は思考（thinking）がデフォルト有効で、思考トークンは
    /// maxOutputTokens と同じ出力バジェットから消費される。既定（<see cref="AiOptions.ThinkingBudget"/>=0）で
    /// 思考を無効化し、出力バジェット全量を本文（副資材チェックの JSON 等）に充てる（High 指摘対応）。
    /// </summary>
    private GeminiGenerationConfig BuildGenerationConfig(int maxTokens) =>
        new()
        {
            MaxOutputTokens = maxTokens,
            ThinkingConfig = new GeminiThinkingConfig { ThinkingBudget = _options.ThinkingBudget },
        };

    private static GeminiSystemInstruction? BuildSystemInstruction(IReadOnlyList<AiSystemBlock> blocks)
    {
        var parts = blocks
            .Where(block => !string.IsNullOrEmpty(block.Text))
            .Select(block => new GeminiPart { Text = block.Text })
            .ToList();
        return parts.Count == 0 ? null : new GeminiSystemInstruction(parts);
    }

    private static string ExtractText(GeminiResponse response)
    {
        var candidate = response.Candidates is { Count: > 0 } ? response.Candidates[0] : null;
        if (candidate?.Content?.Parts is not { } parts)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (!string.IsNullOrEmpty(part.Text))
            {
                builder.Append(part.Text);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// AI へ渡す mime type を Gemini が受理する png/jpeg のいずれかへ正規化する
    /// （png 以外は jpeg。入力検証が jpeg/png に限定しているため二値でよい）。
    /// </summary>
    private static string ToGeminiMimeType(string mediaType) =>
        string.Equals(mediaType, "image/png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";

    // ---- Gemini REST DTO（generateContent / streamGenerateContent の要求・応答） --------------

    private sealed record GeminiRequest
    {
        public IReadOnlyList<GeminiContent> Contents { get; init; } = Array.Empty<GeminiContent>();

        public GeminiSystemInstruction? SystemInstruction { get; init; }

        public GeminiGenerationConfig? GenerationConfig { get; init; }
    }

    private sealed record GeminiContent(string Role, IReadOnlyList<GeminiPart> Parts);

    private sealed record GeminiPart
    {
        public string? Text { get; init; }

        public GeminiInlineData? InlineData { get; init; }
    }

    private sealed record GeminiInlineData(string MimeType, string Data);

    private sealed record GeminiSystemInstruction(IReadOnlyList<GeminiPart> Parts);

    private sealed record GeminiGenerationConfig
    {
        public int MaxOutputTokens { get; init; }

        public GeminiThinkingConfig? ThinkingConfig { get; init; }
    }

    private sealed record GeminiThinkingConfig
    {
        public int ThinkingBudget { get; init; }
    }

    private sealed record GeminiResponse
    {
        public IReadOnlyList<GeminiCandidate>? Candidates { get; init; }

        public GeminiUsageMetadata? UsageMetadata { get; init; }
    }

    private sealed record GeminiCandidate
    {
        public GeminiContent? Content { get; init; }

        public string? FinishReason { get; init; }
    }

    private sealed record GeminiUsageMetadata
    {
        public long PromptTokenCount { get; init; }

        public long CandidatesTokenCount { get; init; }
    }
}
