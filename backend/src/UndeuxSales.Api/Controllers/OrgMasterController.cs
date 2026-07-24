using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Core;
using UndeuxSales.Infrastructure.Queries;
using UndeuxSales.Infrastructure.Rag;

namespace UndeuxSales.Api.Controllers;

/// <summary>業態ツリー応答。</summary>
public sealed record OrgMasterTreeResponse(IReadOnlyList<OrgBusinessTypeNode> BusinessTypes);

/// <summary>相談受付デスク一覧応答。</summary>
public sealed record ContactDeskListResponse(IReadOnlyList<ContactDesk> Desks);

/// <summary>削除要求（対象 id）。</summary>
public sealed record OrgDeleteRequest(long Id);

/// <summary>
/// しまむらグループ組織マスタ（業態・商品部・部門・相談受付デスク）。
/// 参照は全認証ユーザー、変更は運営者（Owner ポリシー＝role=admin）に限定する。
/// マスタ変更後は自動生成ナレッジ（業態・部門一覧）を同期する（SoT→派生。原則6。
/// 同期は応答前に await するが、失敗はログのみで保存自体は成功として返す＝原則4）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/org-master")]
public sealed class OrgMasterController : ControllerBase
{
    private readonly OrgMasterRepository _repository;
    private readonly KnowledgeSeeder _knowledgeSeeder;
    private readonly ChatPromptCacheVersion _promptCacheVersion;

    public OrgMasterController(
        OrgMasterRepository repository,
        KnowledgeSeeder knowledgeSeeder,
        ChatPromptCacheVersion promptCacheVersion)
    {
        _repository = repository;
        _knowledgeSeeder = knowledgeSeeder;
        _promptCacheVersion = promptCacheVersion;
    }

    /// <summary>マスタ変更を派生物（自動生成ナレッジ・チャット用プロンプトキャッシュ）へ反映する。</summary>
    private async Task PropagateMasterChangeAsync(CancellationToken cancellationToken)
    {
        _promptCacheVersion.Bump();
        await _knowledgeSeeder.RefreshOrgKnowledgeAsync(cancellationToken);
    }

    /// <summary>業態ツリー（業態＋商品部＋部門）。マスタメンテと商談チャットの chips に使用。</summary>
    [HttpGet]
    public async Task<OrgMasterTreeResponse> GetTree(CancellationToken cancellationToken)
        => new(await _repository.GetTreeAsync(cancellationToken));

    /// <summary>相談受付デスク一覧。</summary>
    [HttpGet("contact-desks")]
    public async Task<ContactDeskListResponse> GetContactDesks(CancellationToken cancellationToken)
        => new(await _repository.GetContactDesksAsync(cancellationToken));

    /// <summary>商品部の作成・更新（id=null で新規作成）。</summary>
    [HttpPost("sections/save")]
    [Authorize(Policy = AuthorizationPolicies.Owner)]
    public async Task<OrgSection> SaveSection(
        [FromBody] OrgSectionSaveRequest request, CancellationToken cancellationToken)
    {
        ValidateRequired(request.BusinessTypeCode, "businessTypeCode");
        ValidateRequired(request.Name, "name");
        var saved = await _repository.SaveSectionAsync(request, cancellationToken);
        await PropagateMasterChangeAsync(cancellationToken);
        return saved;
    }

    /// <summary>商品部の削除。配下部門は所属未設定として温存される。</summary>
    [HttpPost("sections/delete")]
    [Authorize(Policy = AuthorizationPolicies.Owner)]
    public async Task<IActionResult> DeleteSection(
        [FromBody] OrgDeleteRequest request, CancellationToken cancellationToken)
    {
        await _repository.DeleteSectionAsync(request.Id, cancellationToken);
        await PropagateMasterChangeAsync(cancellationToken);
        return Ok(new { });
    }

    /// <summary>部門の作成・更新（id=null で新規作成）。</summary>
    [HttpPost("departments/save")]
    [Authorize(Policy = AuthorizationPolicies.Owner)]
    public async Task<OrgDepartment> SaveDepartment(
        [FromBody] OrgDepartmentSaveRequest request, CancellationToken cancellationToken)
    {
        ValidateRequired(request.BusinessTypeCode, "businessTypeCode");
        ValidateRequired(request.DeptCode, "deptCode");
        ValidateRequired(request.DeptName, "deptName");
        var saved = await _repository.SaveDepartmentAsync(request, cancellationToken);
        await PropagateMasterChangeAsync(cancellationToken);
        return saved;
    }

    /// <summary>部門の削除。</summary>
    [HttpPost("departments/delete")]
    [Authorize(Policy = AuthorizationPolicies.Owner)]
    public async Task<IActionResult> DeleteDepartment(
        [FromBody] OrgDeleteRequest request, CancellationToken cancellationToken)
    {
        await _repository.DeleteDepartmentAsync(request.Id, cancellationToken);
        await PropagateMasterChangeAsync(cancellationToken);
        return Ok(new { });
    }

    /// <summary>相談受付デスクの作成・更新（id=null で新規作成）。</summary>
    [HttpPost("contact-desks/save")]
    [Authorize(Policy = AuthorizationPolicies.Owner)]
    public async Task<ContactDesk> SaveContactDesk(
        [FromBody] ContactDeskSaveRequest request, CancellationToken cancellationToken)
    {
        ValidateRequired(request.Topic, "topic");
        ValidateRequired(request.DepartmentName, "departmentName");
        if (request.ChatDomain is not null
            && !UndeuxSales.Core.Rag.KnowledgeTaxonomy.IsValidChatDomain(request.ChatDomain))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                "chatDomain が不正です（system / quality / logistics または未指定）。");
        }

        var saved = await _repository.SaveContactDeskAsync(request, cancellationToken);
        await PropagateMasterChangeAsync(cancellationToken);
        return saved;
    }

    /// <summary>相談受付デスクの削除。</summary>
    [HttpPost("contact-desks/delete")]
    [Authorize(Policy = AuthorizationPolicies.Owner)]
    public async Task<IActionResult> DeleteContactDesk(
        [FromBody] OrgDeleteRequest request, CancellationToken cancellationToken)
    {
        await _repository.DeleteContactDeskAsync(request.Id, cancellationToken);
        await PropagateMasterChangeAsync(cancellationToken);
        return Ok(new { });
    }

    private static void ValidateRequired(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, $"{field} は必須です。");
        }
    }
}
