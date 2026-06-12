using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Api.Controllers;

/// <summary>
/// 在庫アクションフラグ（発注停止候補・値下げ候補）と対応状況の管理API。
/// チーム共有の判断記録であり、認証ユーザー全員が読み書きできる
/// （Importer ポリシーの保護対象である売上 SoT（sales_weekly）を改変しない可逆データのため。
/// created_by / updated_by で監査可能）。
/// HTTP メソッドは既存 API 群（imports / rebuild）と同じく GET / POST のみで構成する
/// （useApi が get/post のみのため。/status・/delete の動詞パスはそのトレードオフ）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/inventory-flags")]
public sealed class InventoryFlagsController : ControllerBase
{
    private readonly InventoryFlagRepository _repository;

    public InventoryFlagsController(InventoryFlagRepository repository)
        => _repository = repository;

    /// <summary>
    /// フラグを一括登録する（冪等）。既存フラグはスキップされ、対応状況は巻き戻らない。
    /// flagged_week はサーバ側で最新スナップショット週を解決して記録する。
    /// </summary>
    [HttpPost("bulk")]
    public Task<InventoryFlagBulkResult> Bulk(
        [FromBody] InventoryFlagBulkRequest request, CancellationToken cancellationToken)
        => _repository.BulkUpsertAsync(
            request, RequestIdentity.AuditUserOf(User), cancellationToken);

    /// <summary>対応状況を一括変更する（all-or-nothing。未知 id が含まれると 404 で全件未更新）。</summary>
    [HttpPost("status")]
    public async Task<IActionResult> UpdateStatus(
        [FromBody] InventoryFlagStatusRequest request, CancellationToken cancellationToken)
    {
        await _repository.UpdateStatusAsync(
            request, RequestIdentity.AuditUserOf(User), cancellationToken);
        return NoContent();
    }

    /// <summary>フラグを一括削除する（誤操作の訂正用。対応の終了は status の done / dismissed が正）。</summary>
    [HttpPost("delete")]
    public async Task<IActionResult> Delete(
        [FromBody] InventoryFlagDeleteRequest request, CancellationToken cancellationToken)
    {
        await _repository.DeleteAsync(request, cancellationToken);
        return NoContent();
    }

    /// <summary>フラグ種別×対応状況の件数と孤児フラグ件数（完売等で明細に現れないフラグ）を取得する。</summary>
    [HttpGet("summary")]
    public Task<InventoryFlagSummary> Summary(CancellationToken cancellationToken)
        => _repository.GetSummaryAsync(cancellationToken);
}
