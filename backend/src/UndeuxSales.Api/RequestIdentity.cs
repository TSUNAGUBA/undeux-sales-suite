using System.Security.Claims;

namespace UndeuxSales.Api;

/// <summary>
/// 認証済みリクエストから監査用のユーザー識別子（メール優先）を解決する。
/// </summary>
/// <remarks>
/// 本番は Firebase JWT を <c>MapInboundClaims = false</c>（Program.cs）で検証するため
/// クレーム名は生名（<c>email</c> / <c>sub</c>）になる。一方、統合テストの
/// TestAuthHandler は <see cref="ClaimTypes"/> のマップ名で発行するため、両方の名前を
/// この順で解決する（生名 → マップ名 → 主体ID）。どれも無い場合のフォールバックは
/// "unknown"（[Authorize] 配下では実際には到達しない防御値）。
/// </remarks>
public static class RequestIdentity
{
    public static string AuditUserOf(ClaimsPrincipal user)
        => user.FindFirstValue("email")
           ?? user.FindFirstValue(ClaimTypes.Email)
           ?? user.FindFirstValue("sub")
           ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? "unknown";
}
