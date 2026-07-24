namespace UndeuxSales.Api;

/// <summary>
/// 認可ポリシーと関連クレームの定義。
/// 取込（データ更新）操作には Firebase カスタムクレームによるロール制御を課す。
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>週次取込（データ更新操作）を許可するポリシー名。</summary>
    public const string Importer = "Importer";

    /// <summary>
    /// 運営者（アプリオーナー）操作を許可するポリシー名。
    /// 組織マスタの編集・運営者RAG設定の変更に適用する。実体は Importer と同じ
    /// role=admin クレームだが、用途を明示するため別名のポリシーとして定義する。
    /// </summary>
    public const string Owner = "Owner";

    /// <summary>ロールを表す Firebase カスタムクレーム名。</summary>
    public const string RoleClaim = "role";

    /// <summary>取込権限を持つロールの値。</summary>
    public const string AdminRole = "admin";
}
