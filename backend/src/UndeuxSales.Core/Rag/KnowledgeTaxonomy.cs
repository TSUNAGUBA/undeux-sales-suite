namespace UndeuxSales.Core.Rag;

/// <summary>
/// ナレッジ（RAG）の分類語彙の一元定義（実装 SoT）。
/// <para>
/// スキーマ（db/schema.sql の CHECK 制約）と API 検証の両方がこの語彙に従う。
/// 値を追加・変更する場合はスキーマ側の CHECK 制約も同時に更新すること（原則6）。
/// </para>
/// </summary>
public static class KnowledgeTaxonomy
{
    /// <summary>運営者RAG設定（書込は管理者クレーム必須）。</summary>
    public const string ScopeOperator = "operator";

    /// <summary>ユーザーRAG設定（全認証ユーザー書込可）。</summary>
    public const string ScopeUser = "user";

    /// <summary>しまむらグループ（会社そのものに対するナレッジ）。</summary>
    public const string CategoryGroup = "group";

    /// <summary>業態（しまむら/アベイル/バースデイ/シャンブル/ディバロ 等）ごとのナレッジ。</summary>
    public const string CategoryBusinessType = "business_type";

    /// <summary>部門（業態×部門コード）ごとのナレッジ。</summary>
    public const string CategoryDepartment = "department";

    /// <summary>基本情報（業界・業種のドメインナレッジ、一般知識）。</summary>
    public const string CategoryBasic = "basic";

    /// <summary>マニュアル（しまむら提供資料。運営者スコープ専用）。</summary>
    public const string CategoryManual = "manual";

    /// <summary>業務チャット部署タグ: システム部（Web-EDI・受発注・システム関連）。</summary>
    public const string DomainSystem = "system";

    /// <summary>業務チャット部署タグ: 商品管理部（品質管理・品質規格）。</summary>
    public const string DomainQuality = "quality";

    /// <summary>業務チャット部署タグ: 物流部（直流・納品・物流）。</summary>
    public const string DomainLogistics = "logistics";

    public static readonly IReadOnlyList<string> Scopes = new[] { ScopeOperator, ScopeUser };

    public static readonly IReadOnlyList<string> Categories = new[]
    {
        CategoryGroup, CategoryBusinessType, CategoryDepartment, CategoryBasic, CategoryManual,
    };

    public static readonly IReadOnlyList<string> ChatDomains = new[]
    {
        DomainSystem, DomainQuality, DomainLogistics,
    };

    /// <summary>業務チャットの部署キー → 表示名（要件上の呼称）。</summary>
    public static readonly IReadOnlyDictionary<string, string> ChatDomainLabels =
        new Dictionary<string, string>
        {
            [DomainSystem] = "システム部",
            [DomainQuality] = "商品管理部",
            [DomainLogistics] = "物流部",
        };

    public static bool IsValidScope(string? value) => value is ScopeOperator or ScopeUser;

    public static bool IsValidCategory(string? value) =>
        value is CategoryGroup or CategoryBusinessType or CategoryDepartment or CategoryBasic or CategoryManual;

    public static bool IsValidChatDomain(string? value) =>
        value is DomainSystem or DomainQuality or DomainLogistics;

    /// <summary>
    /// エントリの分類整合を検証し、不正な組み合わせをエラー詳細として返す（正常時は空）。
    /// スキーマの CHECK 制約と同一のルール。API 層で先に検証し 400 を返すために使う。
    /// </summary>
    public static IReadOnlyList<string> ValidateEntry(
        string? scope, string? category, string? businessTypeCode, string? deptCode, string? bizDomain)
    {
        var errors = new List<string>();
        if (!IsValidScope(scope))
        {
            errors.Add($"scope が不正です（operator / user のいずれか）: {scope}");
        }

        if (!IsValidCategory(category))
        {
            errors.Add($"category が不正です（group / business_type / department / basic / manual のいずれか）: {category}");
            return errors;
        }

        if (bizDomain is not null && !IsValidChatDomain(bizDomain))
        {
            errors.Add($"bizDomain が不正です（system / quality / logistics または未指定）: {bizDomain}");
        }

        if (category == CategoryBusinessType && string.IsNullOrWhiteSpace(businessTypeCode))
        {
            errors.Add("category=business_type の場合は businessTypeCode が必須です。");
        }

        if (category == CategoryDepartment
            && (string.IsNullOrWhiteSpace(businessTypeCode) || string.IsNullOrWhiteSpace(deptCode)))
        {
            errors.Add("category=department の場合は businessTypeCode と deptCode が必須です。");
        }

        if (category == CategoryManual && scope != ScopeOperator)
        {
            errors.Add("category=manual は運営者RAG設定（scope=operator）専用です。");
        }

        return errors;
    }
}
