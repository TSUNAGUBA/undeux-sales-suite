namespace UndeuxSales.Core;

/// <summary>エラーコード1件の定義（コード・概要・対処方法）。</summary>
/// <param name="Code">エラーコード（形式: UNDX-{領域}-{連番}）。</param>
/// <param name="Summary">エラー内容の概要（ユーザー向け）。</param>
/// <param name="Remedy">想定される対処方法。</param>
public sealed record ErrorCodeInfo(string Code, string Summary, string Remedy);

/// <summary>
/// アプリケーション全体のエラーコード一元定義。
/// <para>
/// 形式: <c>UNDX-{領域}-{連番3桁}</c>。
/// 領域: AUTH=認証/認可, REQ=リクエスト検証, IMP=取込処理, DATA=データ層, AI=AI機能, SYS=システム。
/// </para>
/// </summary>
public static class ErrorCodes
{
    public static readonly ErrorCodeInfo Unauthorized = new(
        "UNDX-AUTH-001",
        "認証が必要です。または認証トークンが無効・期限切れです。",
        "ログインし直してから操作をやり直してください。");

    public static readonly ErrorCodeInfo InvalidRequest = new(
        "UNDX-REQ-001",
        "リクエストパラメータが不正です。",
        "入力値の形式・範囲を確認してください。");

    public static readonly ErrorCodeInfo InvalidDateRange = new(
        "UNDX-REQ-002",
        "期間指定が不正です（開始日が終了日より後）。",
        "開始日・終了日の前後関係を確認してください。");

    public static readonly ErrorCodeInfo UnknownDimension = new(
        "UNDX-REQ-003",
        "指定された集計軸が不正です。",
        "指定可能な行・列ディメンション（時間軸: 年・四半期・月、"
        + "カテゴリ軸: 部門・業態・季節・品番3桁・単品・カラー・サイズ・帳票区分・棚割1・棚割2・商品記号）"
        + "から選択してください。");

    public static readonly ErrorCodeInfo ImportFileMissing = new(
        "UNDX-IMP-001",
        "取込ファイルが指定されていません。",
        "CSVファイルを選択してアップロードしてください。");

    public static readonly ErrorCodeInfo ImportFileEmpty = new(
        "UNDX-IMP-002",
        "取込ファイルにデータ行がありません。",
        "ヘッダー行とデータ行を含むCSVファイルをアップロードしてください。");

    public static readonly ErrorCodeInfo ImportFormatInvalid = new(
        "UNDX-IMP-003",
        "取込ファイルの形式（ヘッダー列）が不正です。",
        "必須列をすべて含む正しい形式のCSVをアップロードしてください。");

    public static readonly ErrorCodeInfo ImportRowInvalid = new(
        "UNDX-IMP-004",
        "取込ファイルに不正なデータ行が含まれています。",
        "エラー詳細に従い該当行を修正し、ファイル全体を再アップロードしてください。");

    public static readonly ErrorCodeInfo ImportFileTooLarge = new(
        "UNDX-IMP-005",
        "取込ファイルのサイズが上限を超えています。",
        "ファイルを分割するか、上限内のサイズにしてアップロードしてください。");

    public static readonly ErrorCodeInfo SubsidiaryImageMissing = new(
        "UNDX-REQ-004",
        "副資材チェックの画像が指定されていません。",
        "指示書画像（1〜3枚）とタグ画像（1〜10枚）の両方をアップロードしてください。");

    public static readonly ErrorCodeInfo SubsidiaryImageInvalidFormat = new(
        "UNDX-REQ-005",
        "副資材チェックの画像形式が不正です（JPEG / PNG のみ対応）。",
        "画像を JPEG または PNG 形式に変換してからアップロードし直してください。");

    public static readonly ErrorCodeInfo SubsidiaryImageTooLarge = new(
        "UNDX-REQ-006",
        "副資材チェックの画像サイズが上限（1枚あたり5MB）を超えています。",
        "画像を縮小・圧縮して5MB以下にしてからアップロードし直してください。");

    public static readonly ErrorCodeInfo SubsidiaryImageTooMany = new(
        "UNDX-REQ-007",
        "副資材チェックの画像枚数が上限を超えています。",
        "指示書画像は3枚以内、タグ画像は10枚以内に減らしてアップロードし直してください。");

    public static readonly ErrorCodeInfo DatabaseError = new(
        "UNDX-DATA-001",
        "データベース処理でエラーが発生しました。",
        "時間をおいて再試行してください。解決しない場合はシステム管理者に連絡してください。");

    public static readonly ErrorCodeInfo ProductNotFound = new(
        "UNDX-DATA-002",
        "指定された商品が商品マスタに存在しません。",
        "商品マスタに対象商品が登録されているか確認してください。");

    public static readonly ErrorCodeInfo FlagNotFound = new(
        "UNDX-DATA-003",
        "指定された在庫アクションフラグが見つかりません。",
        "一覧を再読み込みして最新の状態を確認してください（他のユーザーが削除した可能性があります）。");

    public static readonly ErrorCodeInfo KnowledgeNotFound = new(
        "UNDX-DATA-004",
        "指定されたナレッジ（またはマスタ行・原本ファイル）が見つかりません。",
        "一覧を再読み込みして最新の状態を確認してください（他のユーザーが削除した可能性があります）。");

    public static readonly ErrorCodeInfo SubsidiaryCheckNotFound = new(
        "UNDX-DATA-005",
        "指定された副資材チェック（または画像）が見つかりません。",
        "一覧を再読み込みして最新の状態を確認してください。");

    public static readonly ErrorCodeInfo AiCallFailed = new(
        "UNDX-AI-001",
        "AI 応答の生成に失敗しました（LLM 呼出エラー/タイムアウト）。",
        "時間をおいて再送してください。繰り返し発生する場合はシステム管理者に連絡してください。");

    public static readonly ErrorCodeInfo AiNotConfigured = new(
        "UNDX-AI-008",
        "AI 機能が未設定です（Anthropic API キーが構成されていません）。",
        "運営者が環境変数 Anthropic__ApiKey（ANTHROPIC_API_KEY）を設定してから利用してください。");

    // UNDX-AI-002〜007 は将来機能向けに DD-04（AI/RAG エージェント詳細設計）で予約済みのため 009 を採番。
    public static readonly ErrorCodeInfo AiResponseUnparseable = new(
        "UNDX-AI-009",
        "AI 応答の解析に失敗しました（チェック結果の JSON を読み取れません）。",
        "再実行してください。繰り返し発生する場合はシステム管理者に連絡してください。");

    public static readonly ErrorCodeInfo Unexpected = new(
        "UNDX-SYS-001",
        "想定外のシステムエラーが発生しました。",
        "システム管理者に連絡してください。");

    /// <summary>全エラーコード一覧（運用ガイドの逆引きリファレンス用）。</summary>
    public static IReadOnlyList<ErrorCodeInfo> All { get; } = new[]
    {
        Unauthorized,
        InvalidRequest,
        InvalidDateRange,
        UnknownDimension,
        ImportFileMissing,
        ImportFileEmpty,
        ImportFormatInvalid,
        ImportRowInvalid,
        ImportFileTooLarge,
        SubsidiaryImageMissing,
        SubsidiaryImageInvalidFormat,
        SubsidiaryImageTooLarge,
        SubsidiaryImageTooMany,
        DatabaseError,
        ProductNotFound,
        FlagNotFound,
        KnowledgeNotFound,
        SubsidiaryCheckNotFound,
        AiCallFailed,
        AiNotConfigured,
        AiResponseUnparseable,
        Unexpected,
    };
}
