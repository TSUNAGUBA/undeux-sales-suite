namespace UndeuxSales.Api.Configuration;

/// <summary>Firebase Authentication（IDトークン検証）の設定。</summary>
public sealed class FirebaseAuthOptions
{
    /// <summary>appsettings.json 上のセクション名。</summary>
    public const string SectionName = "Firebase";

    /// <summary>Firebase プロジェクトID。IDトークンの issuer / audience の検証に用いる。</summary>
    public string ProjectId { get; set; } = string.Empty;
}
