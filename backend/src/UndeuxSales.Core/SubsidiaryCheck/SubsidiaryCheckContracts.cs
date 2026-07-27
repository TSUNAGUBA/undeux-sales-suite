namespace UndeuxSales.Core.SubsidiaryCheck;

/// <summary>チェック状態（subsidiary_check.status の語彙。実装 SoT は本クラス）。</summary>
public static class SubsidiaryCheckStatus
{
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>指摘カテゴリ（layout=レイアウト / order=順番 / content=内容）。</summary>
public static class SubsidiaryCheckCategory
{
    public const string Layout = "layout";
    public const string Order = "order";
    public const string Content = "content";

    /// <summary>既知のカテゴリか（小文字前提。正規化はパーサ側で行う）。</summary>
    public static bool IsKnown(string value) => value is Layout or Order or Content;
}

/// <summary>指摘の重要度（fail=要修正 / warn=要確認 / pass=OK）。</summary>
public static class SubsidiaryCheckSeverity
{
    public const string Fail = "fail";
    public const string Warn = "warn";
    public const string Pass = "pass";

    /// <summary>既知の重要度か（小文字前提。正規化はパーサ側で行う）。</summary>
    public static bool IsKnown(string value) => value is Fail or Warn or Pass;
}

/// <summary>チェック画像の種別（instruction=指示書（正） / tag=タグ（検査対象））。</summary>
public static class SubsidiaryCheckImageKind
{
    public const string Instruction = "instruction";
    public const string Tag = "tag";
}

/// <summary>副資材チェックのサマリ（履歴一覧・詳細ヘッダ共用）。</summary>
/// <param name="CreatedAt">チェックの登録日時（不変）。</param>
/// <param name="CheckedAt">完了（または失敗確定）日時。未確定なら null。</param>
/// <param name="StartedAt">
/// 最後に AI 実行を開始した日時（再実行のたびに更新される）。孤児判定（processing の滞留超過）の
/// 基準時刻。started_at 導入前の既存行は created_at でバックフィル済み（理論上のみ null）。
/// </param>
public sealed record SubsidiaryCheckSummary(
    Guid CheckId,
    Guid? ProductId,
    string ProductLabel,
    string Status,
    string? Judgment,
    int FailCount,
    int WarnCount,
    int FindingCount,
    int InstructionImageCount,
    int TagImageCount,
    string? AiModel,
    string? ErrorMessage,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime? CheckedAt,
    DateTime? StartedAt);

/// <summary>AI チェックの指摘1件。</summary>
/// <param name="Category">layout / order / content。</param>
/// <param name="Severity">fail / warn / pass。</param>
/// <param name="Suggestion">修正提案（なければ null）。</param>
/// <param name="Evidence">根拠（指示書のどの記載と比較したか等。なければ null）。</param>
public sealed record SubsidiaryCheckFinding(
    string Category,
    string Severity,
    string Title,
    string Detail,
    string? Suggestion,
    string? Evidence);

/// <summary>チェック画像のメタ情報（バイナリは画像取得 API で別途返す）。</summary>
public sealed record SubsidiaryCheckImageInfo(
    Guid ImageId,
    string Kind,
    string FileName,
    string ContentType,
    long SizeBytes,
    int SortOrder);

/// <summary>商品マスタ付属情報（m_product_attachment）。SoT は運用部門の管理ファイル→SQL投入。</summary>
public sealed record MasterProductAttachment(
    string? Composition,
    string? OriginCountry,
    string? CareLabels,
    string? ColorFastnessNote,
    string? DisplayOrder,
    string? QualityNotes,
    DateTime UpdatedAt);

/// <summary>チェック対象の商品情報（商品マスタ＋付属情報のスナップショット表示用）。</summary>
public sealed record SubsidiaryCheckProductInfo(
    Guid ProductId,
    string ProductName,
    string ProductSign,
    string ProductTypeCrd,
    string? Brand,
    MasterProductAttachment? Attachment);

/// <summary>副資材チェックの詳細（サマリ＋指摘＋画像＋商品情報）。</summary>
public sealed record SubsidiaryCheckDetail(
    SubsidiaryCheckSummary Summary,
    IReadOnlyList<SubsidiaryCheckFinding> Findings,
    IReadOnlyList<SubsidiaryCheckImageInfo> Images,
    SubsidiaryCheckProductInfo? Product);

/// <summary>副資材チェック履歴のページ。</summary>
public sealed record SubsidiaryCheckPage(
    IReadOnlyList<SubsidiaryCheckSummary> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>ルールブックの1項目。</summary>
/// <param name="Source">出典（資料名・章番号等。なければ null）。</param>
public sealed record SubsidiaryRuleItem(string Name, string Description, string? Source);

/// <summary>ルールブックの1セクション。</summary>
public sealed record SubsidiaryRuleSection(
    string Id,
    string Title,
    string? Description,
    IReadOnlyList<SubsidiaryRuleItem> Items);

/// <summary>副資材チェックのルールブック（画面表示用。SoT は SubsidiaryCheckRuleCatalog）。</summary>
/// <param name="Source">出典の総括文。</param>
public sealed record SubsidiaryRuleBook(
    IReadOnlyList<SubsidiaryRuleSection> Sections,
    string Source);
