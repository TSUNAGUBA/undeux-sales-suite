namespace UndeuxSales.Core.Rag;

/// <summary>チャット履歴の1メッセージ（role は user / assistant）。</summary>
public sealed record AiChatMessage(string Role, string Content);

/// <summary>system プロンプトの1ブロック。先に置いたブロックほど安定プレフィックス（Gemini の暗黙キャッシュ対象）。</summary>
public sealed record AiSystemBlock(string Text);

/// <summary>LLM へのチャット要求（system ブロック列＋会話履歴）。</summary>
public sealed record AiChatRequest(
    IReadOnlyList<AiSystemBlock> System,
    IReadOnlyList<AiChatMessage> Messages,
    int MaxTokens);

/// <summary>
/// ストリーミング応答イベント。
/// Type: "delta"（Text にテキスト断片） / "done"（トークン使用量）。
/// </summary>
public sealed record AiStreamEvent(string Type, string? Text, long InputTokens, long OutputTokens)
{
    public static AiStreamEvent Delta(string text) => new("delta", text, 0, 0);

    public static AiStreamEvent Done(long inputTokens, long outputTokens) =>
        new("done", null, inputTokens, outputTokens);
}

/// <summary>
/// 画像分析への入力1枚。
/// </summary>
/// <param name="Data">画像バイナリ。</param>
/// <param name="MediaType">image/jpeg または image/png。</param>
/// <param name="Label">画像の直前に置くテキストブロック（例:「指示書画像（正） 1/2」）。</param>
public sealed record AiImageInput(byte[] Data, string MediaType, string Label);

/// <summary>
/// LLM クライアント抽象（実装は Infrastructure の GeminiVertexAiClient）。
/// 未設定（API キーなし）の場合、呼出側は UNDX-AI-008 で応答する（グレースフルデグラデーション）。
/// </summary>
public interface IAiChatClient
{
    /// <summary>API キーが構成済みで呼出可能か。</summary>
    bool IsConfigured { get; }

    /// <summary>チャットに使用するモデル ID（表示用）。</summary>
    string ChatModel { get; }

    /// <summary>チャット応答をストリーミングで生成する。</summary>
    IAsyncEnumerable<AiStreamEvent> StreamChatAsync(AiChatRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 画像の内容説明テキストを生成する（ナレッジ取込の補助処理）。
    /// mediaType は image/jpeg または image/png。
    /// </summary>
    Task<string> DescribeImageAsync(byte[] imageData, string mediaType, string? hint, CancellationToken cancellationToken);

    /// <summary>
    /// 複数画像の分析応答テキストを生成する（副資材チェック等）。
    /// 各画像はラベル（テキストブロック）→画像ブロックの順で並べ、末尾に userPrompt を置く。
    /// </summary>
    Task<string> AnalyzeImagesAsync(
        IReadOnlyList<AiImageInput> images,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken cancellationToken);
}
