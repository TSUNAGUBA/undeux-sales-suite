namespace UndeuxSales.Core.SubsidiaryCheck.ManualCheck;

/// <summary>
/// 手入力チェック（手入力した表示項目 × タグ画像から AI が読み取った内容の突合検証）の契約。
/// <para>
/// 既存の画像 × 画像の副資材チェック（<see cref="SubsidiaryCheckContracts"/> 群）とは別系統で、
/// 状態語彙（processing/completed/failed）と判定語彙（pass/warn/fail）、画像メタ
/// （<see cref="SubsidiaryCheckImageInfo"/>）・商品情報（<see cref="SubsidiaryCheckProductInfo"/>）は
/// 既存契約を再利用する（原則3）。JSON ワイヤ形式は既存同様 camelCase。
/// </para>
/// </summary>
public static class ManualFieldValueType
{
    public const string String = "string";
    public const string Number = "number";

    public static bool IsKnown(string value) => value is String or Number;
}

/// <summary>項目の突合方式（text=文字列突合 / ratio=組成割合突合 / careIcon=洗濯アイコン列突合）。</summary>
public static class ManualFieldCompareMode
{
    public const string Text = "text";
    public const string Ratio = "ratio";
    public const string CareIcon = "careIcon";

    public static bool IsKnown(string value) => value is Text or Ratio or CareIcon;
}

/// <summary>項目単位の突合判定（match=一致 / mismatch=相違 / warn=要確認 / missing=タグに見当たらず）。</summary>
public static class ManualCheckVerdict
{
    public const string Match = "match";
    public const string Mismatch = "mismatch";
    public const string Warn = "warn";
    public const string Missing = "missing";

    public static bool IsKnown(string value) => value is Match or Mismatch or Warn or Missing;
}

/// <summary>
/// タグパターンの項目定義。入力フォーム構成と突合方式の両方を駆動する単一定義（原則3・原則6）。
/// </summary>
/// <param name="Key">安定キー（英数）。入力値・抽出値・突合結果の突合わせに使う。</param>
/// <param name="Label">画面表示名（例: 品番・組成表示）。</param>
/// <param name="ValueType"><see cref="ManualFieldValueType"/>（string / number）。</param>
/// <param name="Multiple">複数入力可か（組成・洗濯表示等）。</param>
/// <param name="Compare"><see cref="ManualFieldCompareMode"/>（text / ratio / careIcon）。</param>
/// <param name="Required">新規チェック実行時に必須入力とするか。</param>
public sealed record TagPatternField(
    string Key,
    string Label,
    string ValueType,
    bool Multiple,
    string Compare,
    bool Required);

/// <summary>タグパターン（入力フォームの構成テンプレート。SoT はアプリ＝subsidiary_tag_pattern）。</summary>
public sealed record TagPattern(
    Guid PatternId,
    string Name,
    string Description,
    IReadOnlyList<TagPatternField> Fields,
    bool IsActive,
    string CreatedBy,
    DateTime CreatedAt,
    string UpdatedBy,
    DateTime UpdatedAt);

/// <summary>タグパターンの新規作成／更新の入力（クライアント→サーバ）。</summary>
public sealed record TagPatternUpsert(
    string Name,
    string? Description,
    IReadOnlyList<TagPatternField> Fields,
    bool IsActive);

/// <summary>組成（ratio）の1成分。割合は未入力もあり得るため nullable。</summary>
public sealed record RatioComponent(string Material, decimal? Percent);

/// <summary>
/// 手入力の1項目のスナップショット（項目定義＋入力値）。
/// text/number は <see cref="Values"/>（単数は要素0〜1、複数は複数要素）、
/// careIcon は <see cref="Values"/> にアイコンコードを並び順で、
/// ratio は <see cref="Ratios"/> に成分を保持する。
/// </summary>
public sealed record ManualInputField(
    string Key,
    string Label,
    string ValueType,
    bool Multiple,
    string Compare,
    IReadOnlyList<string> Values,
    IReadOnlyList<RatioComponent> Ratios);

/// <summary>手入力チェックの入力全体（実行時のスナップショット）。</summary>
public sealed record ManualCheckInput(
    Guid? PatternId,
    string PatternName,
    IReadOnlyList<ManualInputField> Fields);

/// <summary>
/// タグ画像から AI が読み取った1項目の抽出値。
/// <see cref="Text"/> は代表テキスト、<see cref="Items"/> は複数値（組成の各行・洗濯アイコンのコード列）。
/// <see cref="Readable"/>=false はタグに該当項目が判読できなかったことを表す（突合は warn へ倒す）。
/// </summary>
public sealed record ExtractedField(
    string Key,
    string? Text,
    IReadOnlyList<string> Items,
    bool Readable);

/// <summary>AI 抽出結果の全体（項目キー→抽出値）。</summary>
public sealed record ManualCheckExtraction(IReadOnlyList<ExtractedField> Fields);

/// <summary>項目単位の突合結果（1項目1件）。</summary>
/// <param name="Verdict"><see cref="ManualCheckVerdict"/>。</param>
/// <param name="Expected">手入力値の表示文字列。</param>
/// <param name="Extracted">タグから読み取った表示文字列（見当たらない場合 null）。</param>
/// <param name="Detail">一致/相違の説明（担当者の目視確認の手掛かり）。</param>
/// <param name="ExpectedItems">組成・洗濯アイコン等の内訳（手入力側）。</param>
/// <param name="ExtractedItems">組成・洗濯アイコン等の内訳（タグ読取側）。</param>
public sealed record ManualFieldResult(
    string Key,
    string Label,
    string Compare,
    string Verdict,
    string Expected,
    string? Extracted,
    string Detail,
    IReadOnlyList<string> ExpectedItems,
    IReadOnlyList<string> ExtractedItems);

/// <summary>手入力チェックのサマリ（履歴一覧・詳細ヘッダ共用）。</summary>
public sealed record ManualCheckSummary(
    Guid CheckId,
    Guid? ProductId,
    string ProductLabel,
    Guid? PatternId,
    string PatternName,
    string Status,
    string? Judgment,
    int FieldCount,
    int MatchCount,
    int MismatchCount,
    int WarnCount,
    int ImageCount,
    string? AiModel,
    string? ErrorMessage,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime? CheckedAt,
    DateTime? StartedAt);

/// <summary>手入力チェックの詳細（サマリ＋入力スナップショット＋突合結果＋画像＋商品情報）。</summary>
public sealed record ManualCheckDetail(
    ManualCheckSummary Summary,
    ManualCheckInput Input,
    IReadOnlyList<ManualFieldResult> Results,
    IReadOnlyList<SubsidiaryCheckImageInfo> Images,
    SubsidiaryCheckProductInfo? Product);

/// <summary>手入力チェック履歴のページ。</summary>
public sealed record ManualCheckPage(
    IReadOnlyList<ManualCheckSummary> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>洗濯表示アイコン（マスタ1件）。原本 SVG はコード側の静的カタログが SoT。</summary>
/// <param name="Code">安定コード（例: wash_40）。入力・抽出・突合の突き合わせキー。</param>
/// <param name="Category">分類（wash/bleach/dry/tumble/iron/professional）。</param>
/// <param name="Label">日本語名称（例: 液温40℃を限度・洗濯機弱）。</param>
/// <param name="Svg">自己完結のインライン SVG（外部依存なし）。</param>
public sealed record CareIcon(string Code, string Category, string Label, string Svg);

/// <summary>洗濯表示アイコンのマスタ全体（選択 UI・突合の選択肢）。</summary>
public sealed record CareIconCatalogResponse(IReadOnlyList<CareIcon> Icons, string Source);
