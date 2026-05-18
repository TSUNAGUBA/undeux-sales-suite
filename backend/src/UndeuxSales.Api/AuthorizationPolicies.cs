namespace UndeuxSales.Api;

/// <summary>
/// 認可ポリシーと関連クレームの定義。
/// 取込（データ更新）操作には Firebase カスタムクレームによるロール制御を課す。
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>週次取込（データ更新操作）を許可するポリシー名。</summary>
    public const string Importer = "Importer";

    /// <summary>ロールを表す Firebase カスタムクレーム名。</summary>
    public const string RoleClaim = "role";

    /// <summary>取込権限を持つロールの値。</summary>
    public const string AdminRole = "admin";
}
