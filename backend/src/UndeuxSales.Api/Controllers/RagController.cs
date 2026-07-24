using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Core;
using UndeuxSales.Core.Rag;
using UndeuxSales.Infrastructure.Rag;

namespace UndeuxSales.Api.Controllers;

/// <summary>RAG ステータス応答。</summary>
public sealed record RagStatusResponse(
    bool AiConfigured,
    string ChatModel,
    string EmbeddingModel,
    bool LexicalSearchEnabled,
    IReadOnlyList<KnowledgeCount> Counts);

/// <summary>自由入力ナレッジの登録要求。</summary>
public sealed record KnowledgeCreateRequest(
    string? Scope,
    string? Category,
    string? BusinessTypeCode,
    string? DeptCode,
    string? BizDomain,
    string? Title,
    string? Content);

/// <summary>ナレッジ更新要求。category を指定した場合のみ分類タグ（businessTypeCode 等）も併せて適用する。</summary>
public sealed record KnowledgeUpdateRequest(
    string? Title,
    string? Content,
    string? Category,
    string? BusinessTypeCode,
    string? DeptCode,
    string? BizDomain);

/// <summary>RAG 検索テストの結果1件（チャンク全文は返さない）。</summary>
public sealed record RagSearchResultItem(
    long ChunkId,
    Guid EntryId,
    string Title,
    string Category,
    string? BusinessTypeCode,
    string? DeptCode,
    int Seq,
    string Snippet,
    double Score,
    double VectorScore,
    double LexicalScore);

/// <summary>RAG 検索テスト応答。</summary>
public sealed record RagSearchResponse(IReadOnlyList<RagSearchResultItem> Results);

/// <summary>
/// RAG 設定（ナレッジ管理）。
/// 参照・検索は全認証ユーザー。運営者スコープ（operator）の変更は role=admin に限定する。
/// </summary>
[ApiController]
[Authorize]
[Route("api/rag")]
public sealed class RagController : ControllerBase
{
    private const long HardSizeLimitBytes = 25 * 1024 * 1024; // multipart 全体の上限（本体 20MB＋メタ）

    private readonly KnowledgeRepository _repository;
    private readonly KnowledgeIngestionService _ingestionService;
    private readonly IAiChatClient _aiClient;

    public RagController(
        KnowledgeRepository repository,
        KnowledgeIngestionService ingestionService,
        IAiChatClient aiClient)
    {
        _repository = repository;
        _ingestionService = ingestionService;
        _aiClient = aiClient;
    }

    /// <summary>AI 設定状態と登録件数（RAG設定・チャット画面の事前チェック用）。</summary>
    [HttpGet("status")]
    public async Task<RagStatusResponse> GetStatus(CancellationToken cancellationToken)
        => new(
            _aiClient.IsConfigured,
            _aiClient.ChatModel,
            HashingVectorizer.ModelName,
            await _repository.IsLexicalSearchEnabledAsync(cancellationToken),
            await _repository.CountByScopeCategoryAsync(cancellationToken));

    /// <summary>ナレッジ一覧（ページング・絞込・部分一致検索）。</summary>
    [HttpGet("knowledge")]
    public Task<KnowledgeListResult> List(
        [FromQuery] string? scope,
        [FromQuery] string? category,
        [FromQuery] string? businessTypeCode,
        [FromQuery] string? deptCode,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (scope is not null && !KnowledgeTaxonomy.IsValidScope(scope))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, $"scope が不正です: {scope}");
        }

        if (category is not null && !KnowledgeTaxonomy.IsValidCategory(category))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, $"category が不正です: {category}");
        }

        return _repository.ListAsync(
            new KnowledgeListFilter(scope, category, businessTypeCode, deptCode, search, page, pageSize),
            cancellationToken);
    }

    /// <summary>ナレッジ詳細（本文つき）。</summary>
    [HttpGet("knowledge/{id}")]
    public async Task<KnowledgeDetail> Get(string id, CancellationToken cancellationToken)
        => await _repository.GetAsync(ParseId(id), cancellationToken)
           ?? throw new AppException(ErrorCodes.KnowledgeNotFound, 404);

    /// <summary>原本ファイルのダウンロード（sourceType=file のみ）。</summary>
    [HttpGet("knowledge/{id}/file")]
    public async Task<IActionResult> DownloadFile(string id, CancellationToken cancellationToken)
    {
        var file = await _repository.GetFileAsync(ParseId(id), cancellationToken)
                   ?? throw new AppException(ErrorCodes.KnowledgeNotFound, 404,
                       "原本ファイルがありません（自由入力ナレッジ、またはナレッジ未存在）。");
        return File(file.Data, file.ContentType, file.FileName);
    }

    /// <summary>
    /// ナレッジの新規登録。application/json は自由入力、multipart/form-data はファイル登録。
    /// 登録と同時にチャンク化・ベクター化（インデックス）まで同期実行する。
    /// </summary>
    [HttpPost("knowledge")]
    [RequestSizeLimit(HardSizeLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = HardSizeLimitBytes)]
    public async Task<KnowledgeDetail> Create(CancellationToken cancellationToken)
    {
        var user = RequestIdentity.AuditUserOf(User);

        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(cancellationToken);
            var scope = form["scope"].ToString();
            RequireScopeWritePermission(scope);

            var file = form.Files.GetFile("file")
                ?? throw new AppException(ErrorCodes.InvalidRequest, 400, "file は必須です。");

            // サイズ超過（画像5MB/その他20MB）はバッファ確保前に拒否する（無駄な大容量確保の回避）。
            KnowledgeIngestionService.ValidateFileSize(file.FileName, file.Length);

            // MemoryStream 経由の二重バッファ（瞬間2倍のメモリ確保）を避け、単一バッファへ直接読み込む。
            var fileData = new byte[file.Length];
            await using (var stream = file.OpenReadStream())
            {
                await stream.ReadExactlyAsync(fileData, cancellationToken);
            }

            return await _ingestionService.CreateFileEntryAsync(
                scope,
                form["category"].ToString(),
                NullIfEmpty(form["businessTypeCode"]),
                NullIfEmpty(form["deptCode"]),
                NullIfEmpty(form["bizDomain"]),
                NullIfEmpty(form["title"]),
                NullIfEmpty(form["description"]),
                file.FileName,
                fileData,
                user,
                cancellationToken);
        }

        var request = await Request.ReadFromJsonAsync<KnowledgeCreateRequest>(cancellationToken: cancellationToken)
            ?? throw new AppException(ErrorCodes.InvalidRequest, 400, "リクエストボディが不正です。");
        RequireScopeWritePermission(request.Scope ?? string.Empty);

        return await _ingestionService.CreateTextEntryAsync(
            request.Scope ?? string.Empty,
            request.Category ?? string.Empty,
            request.BusinessTypeCode,
            request.DeptCode,
            request.BizDomain,
            request.Title ?? string.Empty,
            request.Content ?? string.Empty,
            user,
            cancellationToken);
    }

    /// <summary>ナレッジの更新（本文・タイトル変更時は自動再インデックス）。</summary>
    [HttpPost("knowledge/{id}/update")]
    public async Task<KnowledgeDetail> Update(
        string id, [FromBody] KnowledgeUpdateRequest request, CancellationToken cancellationToken)
    {
        var entryId = ParseId(id);
        await RequireEntryWritePermissionAsync(entryId, cancellationToken);

        var categorySpecified = !string.IsNullOrWhiteSpace(request.Category);
        var patch = new KnowledgeUpdatePatch(
            request.Title,
            request.Content,
            request.Category,
            request.BusinessTypeCode,
            request.DeptCode,
            request.BizDomain,
            BusinessTypeCodeSpecified: categorySpecified,
            DeptCodeSpecified: categorySpecified,
            BizDomainSpecified: categorySpecified);

        return await _ingestionService.UpdateEntryAsync(
            entryId, patch, RequestIdentity.AuditUserOf(User), cancellationToken);
    }

    /// <summary>ナレッジの削除（シード由来は論理削除、手動登録は物理削除）。</summary>
    [HttpPost("knowledge/{id}/delete")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var entryId = ParseId(id);
        await RequireEntryWritePermissionAsync(entryId, cancellationToken);
        await _repository.DeleteAsync(entryId, RequestIdentity.AuditUserOf(User), cancellationToken);
        return Ok(new { });
    }

    /// <summary>再インデックス（原本は不変のままチャンク・ベクターを再生成）。</summary>
    [HttpPost("knowledge/{id}/reindex")]
    public async Task<KnowledgeDetail> Reindex(string id, CancellationToken cancellationToken)
    {
        var entryId = ParseId(id);
        await RequireEntryWritePermissionAsync(entryId, cancellationToken);
        return await _ingestionService.ReindexAsync(
            entryId, RequestIdentity.AuditUserOf(User), cancellationToken);
    }

    /// <summary>RAG 検索テスト（RAG設定画面用。チャットと同じハイブリッド検索を実行する）。</summary>
    [HttpGet("search")]
    public async Task<RagSearchResponse> Search(
        [FromQuery] string? query,
        [FromQuery] string? mode,
        [FromQuery] string? domain,
        [FromQuery] string? businessTypeCode,
        [FromQuery] string? deptCode,
        [FromQuery] int limit = 8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "query は必須です。");
        }

        var scope = mode switch
        {
            "business" when KnowledgeTaxonomy.IsValidChatDomain(domain) =>
                RagSearchScope.ForBusinessChat(domain!),
            "business" => throw new AppException(ErrorCodes.InvalidRequest, 400,
                "mode=business の場合は domain（system/quality/logistics）が必須です。"),
            "negotiation" when !string.IsNullOrWhiteSpace(businessTypeCode) =>
                new RagSearchScope(null, businessTypeCode, NullIfEmpty(deptCode)),
            "negotiation" => throw new AppException(ErrorCodes.InvalidRequest, 400,
                "mode=negotiation の場合は businessTypeCode が必須です。"),
            null or "" => RagSearchScope.All,
            _ => throw new AppException(ErrorCodes.InvalidRequest, 400, $"mode が不正です: {mode}"),
        };

        var hits = await _repository.SearchAsync(
            query, HashingVectorizer.Embed(query), scope, limit, cancellationToken);

        return new RagSearchResponse(hits
            .Select(hit => new RagSearchResultItem(
                hit.ChunkId, hit.EntryId, hit.Title, hit.Category, hit.BusinessTypeCode,
                hit.DeptCode, hit.Seq, hit.Snippet, hit.Score, hit.VectorScore, hit.LexicalScore))
            .ToList());
    }

    private static Guid ParseId(string id)
        => Guid.TryParse(id, out var parsed)
            ? parsed
            : throw new AppException(ErrorCodes.KnowledgeNotFound, 404, "ナレッジIDの形式が不正です。");

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private bool IsOwner
        => User.HasClaim(AuthorizationPolicies.RoleClaim, AuthorizationPolicies.AdminRole);

    /// <summary>スコープ別の書込権限（operator は運営者のみ）を検証する。</summary>
    private void RequireScopeWritePermission(string scope)
    {
        if (scope == KnowledgeTaxonomy.ScopeOperator && !IsOwner)
        {
            throw new AppException(ErrorCodes.Unauthorized, 403,
                "運営者RAG設定の変更には管理者権限（role=admin）が必要です。");
        }
    }

    /// <summary>既存エントリへの書込権限を検証する（entry のスコープに従う）。</summary>
    private async Task RequireEntryWritePermissionAsync(Guid entryId, CancellationToken cancellationToken)
    {
        var entry = await _repository.GetAsync(entryId, cancellationToken)
                    ?? throw new AppException(ErrorCodes.KnowledgeNotFound, 404);
        RequireScopeWritePermission(entry.Scope);
    }
}
