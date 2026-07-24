namespace UndeuxSales.Infrastructure.Ai;

/// <summary>
/// AI（Anthropic Claude API）設定。環境変数 Anthropic__ApiKey 等で注入する。
/// モデル ID は環境依存として設定で解決する（DD-04 §7.1 のモデル抽象化方針）。
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>Anthropic API キー。空の場合 AI 機能は無効（チャットは UNDX-AI-008 を返す）。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>チャット応答用モデル（高推論クラス）。</summary>
    public string Model { get; set; } = "claude-opus-4-8";

    /// <summary>画像内容説明用モデル（低コストクラス。取込時の補助処理）。</summary>
    public string VisionModel { get; set; } = "claude-haiku-4-5";

    /// <summary>チャット応答の最大出力トークン数。</summary>
    public int MaxOutputTokens { get; set; } = 2048;
}
