using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Api.Controllers;

/// <summary>稼働状態・準備状態を返すヘルスチェックエンドポイント（認証不要）。</summary>
[ApiController]
[AllowAnonymous]
[Route("api/health")]
public sealed class HealthController : ControllerBase
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        IDbConnectionFactory connectionFactory, ILogger<HealthController> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <summary>プロセスの稼働確認（liveness）。</summary>
    [HttpGet]
    public IActionResult Liveness() => Ok(new { status = "ok" });

    /// <summary>DB接続を含む準備状態の確認（readiness）。</summary>
    [HttpGet("ready")]
    public async Task<IActionResult> Readiness(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection =
                await _connectionFactory.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT 1;", cancellationToken: cancellationToken));
            return Ok(new { status = "ready" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Readiness チェックに失敗しました。");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable, new { status = "unavailable" });
        }
    }
}
