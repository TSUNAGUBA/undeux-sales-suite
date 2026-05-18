namespace UndeuxSales.Api.Configuration;

/// <summary>CORS（フロントエンドオリジン許可）の設定。</summary>
public sealed class CorsSettings
{
    /// <summary>appsettings.json 上のセクション名。</summary>
    public const string SectionName = "Cors";

    /// <summary>許可するフロントエンドのオリジン一覧。</summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
}
