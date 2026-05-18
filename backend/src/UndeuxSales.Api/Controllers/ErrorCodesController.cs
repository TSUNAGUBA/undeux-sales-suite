using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Core;

namespace UndeuxSales.Api.Controllers;

/// <summary>エラーコードの一覧（逆引きリファレンス）を提供する。</summary>
[ApiController]
[AllowAnonymous]
[Route("api/error-codes")]
public sealed class ErrorCodesController : ControllerBase
{
    /// <summary>全エラーコードの定義（コード・概要・対処方法）を取得する。</summary>
    [HttpGet]
    public IReadOnlyList<ErrorCodeInfo> Get() => ErrorCodes.All;
}
