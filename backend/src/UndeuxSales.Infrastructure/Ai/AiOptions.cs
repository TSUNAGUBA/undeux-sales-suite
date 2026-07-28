namespace UndeuxSales.Infrastructure.Ai;

/// <summary>
/// AI（Google Vertex AI / Gemini）設定。環境変数 VertexAi__ProjectId 等で注入する。
/// <para>
/// LLM は Vertex AI 経由の Gemini（Messages ではなく generateContent / streamGenerateContent）。
/// モデル ID は環境依存として設定で解決する（DD-04 §7.1 のモデル抽象化方針）。運用側は
/// リポジトリ変数（GEMINI_MODEL 等）でモデルを差し替えられる（コード変更不要）。
/// </para>
/// <para>
/// 認証は GCP のサービスアカウント（<see cref="ServiceAccountJsonBase64"/>）または
/// Application Default Credentials（ADC）で行う。ProjectId 未設定時は AI 機能は無効
/// （<c>IsConfigured=false</c>。チャットは UNDX-AI-008、副資材チェックは 503 を返す）。
/// </para>
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "VertexAi";

    /// <summary>
    /// GCP プロジェクト ID。空の場合 AI 機能は無効（グレースフルデグラデーション）。
    /// Firebase（認証）と同一の GCP プロジェクトを指定してよい。
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Vertex AI のロケーション。既定は "global"（グローバルエンドポイント）。
    /// リージョン指定時はホストが <c>{Location}-aiplatform.googleapis.com</c> になる。
    /// </summary>
    public string Location { get; set; } = "global";

    /// <summary>
    /// サービスアカウント鍵（JSON）を base64 で符号化した文字列。空の場合は ADC
    /// （環境変数 GOOGLE_APPLICATION_CREDENTIALS のファイル、または GCE/GKE のメタデータサーバ）を使う。
    /// <para>
    /// 本番の api コンテナは GCP 外（AWS EC2）で動くためメタデータ経由の ADC は使えず、
    /// サービスアカウント鍵を渡す必要がある。鍵 JSON は改行・引用符・= を含み、そのままでは
    /// <c>.env</c>/compose 補間で壊れるため、base64（<c>[A-Za-z0-9+/=]</c> のみ）で受け渡す。
    /// </para>
    /// </summary>
    public string ServiceAccountJsonBase64 { get; set; } = string.Empty;

    /// <summary>チャット応答・画像分析（副資材チェック）用モデル。既定 gemini-2.5-flash。</summary>
    public string Model { get; set; } = "gemini-2.5-flash";

    /// <summary>画像内容説明用モデル（ナレッジ取込の補助処理）。既定 gemini-2.5-flash。</summary>
    public string VisionModel { get; set; } = "gemini-2.5-flash";

    /// <summary>チャット応答の最大出力トークン数。</summary>
    public int MaxOutputTokens { get; set; } = 2048;

    /// <summary>
    /// Gemini の思考（thinking）バジェット。既定 0＝無効（gemini-2.5-flash 向け）。
    /// <para>
    /// gemini-2.5 系は思考がデフォルト有効で、思考トークンは <see cref="MaxOutputTokens"/> と同じ
    /// 出力バジェットから消費される。決定的な JSON 抽出（副資材チェック）・画像説明・簡潔なチャット
    /// では思考は不要で、有効なままだと出力前にバジェットが枯渇し MAX_TOKENS 切詰・空応答を招くため
    /// 0（無効）を既定とする。-1＝動的、正の値＝上限トークン。gemini-2.5-pro は 0 不可
    /// （最小バジェットあり）のため、上位モデルへ差し替える場合は本値も併せて調整する。
    /// </para>
    /// </summary>
    public int ThinkingBudget { get; set; } = 0;
}
