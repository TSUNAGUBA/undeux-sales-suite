using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UndeuxSales.Core;
using UndeuxSales.Core.Rag;

namespace UndeuxSales.Infrastructure.Ai;

/// <summary>
/// Anthropic Claude API（Messages API）クライアント。
/// <para>
/// system の安定プレフィックス（役割定義・マスタ文脈）へ cache_control を付与して
/// プロンプトキャッシュを効かせ、可変部（RAG 検索結果）はその後に置く（DD-04 §7.2）。
/// API キー未設定時は IsConfigured=false となり、呼出側が UNDX-AI-008 で応答する。
/// </para>
/// </summary>
public sealed class AnthropicAiClient : IAiChatClient
{
    private readonly AiOptions _options;
    private readonly ILogger<AnthropicAiClient> _logger;
    private readonly AnthropicClient? _client;

    public AnthropicAiClient(IOptions<AiOptions> options, ILogger<AnthropicAiClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? null
            : new AnthropicClient { ApiKey = _options.ApiKey };
    }

    public bool IsConfigured => _client is not null;

    public string ChatModel => _options.Model;

    public async IAsyncEnumerable<AiStreamEvent> StreamChatAsync(
        AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = RequireClient();

        var systemBlocks = new List<TextBlockParam>();
        foreach (var block in request.System)
        {
            systemBlocks.Add(new TextBlockParam
            {
                Text = block.Text,
                CacheControl = block.Cache ? new CacheControlEphemeral() : null,
            });
        }

        var messages = new List<MessageParam>();
        foreach (var message in request.Messages)
        {
            messages.Add(new MessageParam
            {
                Role = message.Role == "assistant" ? Role.Assistant : Role.User,
                Content = message.Content,
            });
        }

        var parameters = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = request.MaxTokens,
            System = systemBlocks,
            Messages = messages,
        };

        long inputTokens = 0;
        long outputTokens = 0;

        await foreach (var streamEvent in client.Messages.CreateStreaming(
                           parameters, cancellationToken: cancellationToken))
        {
            if (streamEvent.TryPickContentBlockDelta(out var deltaEvent))
            {
                if (deltaEvent.Delta.TryPickText(out var text))
                {
                    yield return AiStreamEvent.Delta(text.Text);
                }

                continue;
            }

            if (streamEvent.TryPickStart(out var start))
            {
                inputTokens = start.Message.Usage.InputTokens;
                continue;
            }

            if (streamEvent.TryPickDelta(out var messageDelta) && messageDelta.Usage is not null)
            {
                outputTokens = messageDelta.Usage.OutputTokens;
            }
        }

        yield return AiStreamEvent.Done(inputTokens, outputTokens);
    }

    public async Task<string> DescribeImageAsync(
        byte[] imageData, string mediaType, string? hint, CancellationToken cancellationToken)
    {
        var client = RequireClient();

        var prompt =
            "この画像はアパレル小売業のナレッジベースに登録される資料です。"
            + "画像に含まれる文字情報は可能な限りすべて書き起こし、図表・写真は内容を日本語で具体的に説明してください。"
            + "検索用テキストとして使うため、装飾的な前置きは不要です。"
            + (string.IsNullOrWhiteSpace(hint) ? string.Empty : $"\n補足情報: {hint}");

        var parameters = new MessageCreateParams
        {
            Model = _options.VisionModel,
            MaxTokens = 1024,
            Messages =
            [
                new MessageParam
                {
                    Role = Role.User,
                    Content = new List<ContentBlockParam>
                    {
                        new ImageBlockParam
                        {
                            Source = new Base64ImageSource
                            {
                                Data = Convert.ToBase64String(imageData),
                                MediaType = mediaType == "image/png"
                                    ? MediaType.ImagePng
                                    : MediaType.ImageJpeg,
                            },
                        },
                        new TextBlockParam { Text = prompt },
                    },
                },
            ],
        };

        try
        {
            var response = await client.Messages.Create(parameters, cancellationToken: cancellationToken);
            var texts = response.Content
                .Select(block => block.TryPickText(out var text) ? text.Text : null)
                .Where(text => !string.IsNullOrEmpty(text));
            return string.Join("\n", texts).Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "画像説明の生成に失敗しました（モデル: {Model}）", _options.VisionModel);
            throw new AppException(ErrorCodes.AiCallFailed, StatusCodes502, ex.Message);
        }
    }

    public async Task<string> AnalyzeImagesAsync(
        IReadOnlyList<AiImageInput> images,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var client = RequireClient();

        // ラベル（テキスト）→画像 の順に並べ、末尾にチェック指示（userPrompt）を置く。
        var content = new List<ContentBlockParam>();
        foreach (var image in images)
        {
            if (!string.IsNullOrWhiteSpace(image.Label))
            {
                content.Add(new TextBlockParam { Text = image.Label });
            }

            content.Add(new ImageBlockParam
            {
                Source = new Base64ImageSource
                {
                    Data = Convert.ToBase64String(image.Data),
                    MediaType = image.MediaType == "image/png"
                        ? MediaType.ImagePng
                        : MediaType.ImageJpeg,
                },
            });
        }

        content.Add(new TextBlockParam { Text = userPrompt });

        // チェック精度優先で VisionModel ではなくメインモデル（_options.Model）を使用する。
        var parameters = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = maxTokens,
            System = new List<TextBlockParam> { new() { Text = systemPrompt } },
            Messages =
            [
                new MessageParam
                {
                    Role = Role.User,
                    Content = content,
                },
            ],
        };

        try
        {
            var response = await client.Messages.Create(parameters, cancellationToken: cancellationToken);
            var texts = response.Content
                .Select(block => block.TryPickText(out var text) ? text.Text : null)
                .Where(text => !string.IsNullOrEmpty(text));
            return string.Join("\n", texts).Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "画像分析応答の生成に失敗しました（モデル: {Model}）", _options.Model);
            throw new AppException(ErrorCodes.AiCallFailed, StatusCodes502, ex.Message);
        }
    }

    private const int StatusCodes502 = 502;

    private AnthropicClient RequireClient() =>
        _client ?? throw new AppException(ErrorCodes.AiNotConfigured, 503);
}
