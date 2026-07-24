using Microsoft.Extensions.Logging;
using UndeuxSales.Core;
using UndeuxSales.Core.Rag;

namespace UndeuxSales.Infrastructure.Rag;

/// <summary>
/// ナレッジ取込（登録・更新・再インデックス）のオーケストレーション。
/// <para>
/// フロー: 原本確定（SoT）→ テキスト抽出 → チャンク化 → ベクター化 → 索引（派生）。
/// 抽出・AI 説明など補助処理の失敗は登録自体を止めず、index_state=failed として
/// 記録し後から再インデックスできるようにする（原則4・DD-04 §3.2）。
/// </para>
/// </summary>
public sealed class KnowledgeIngestionService
{
    /// <summary>アップロードファイルの上限サイズ（20MB）。</summary>
    public const long MaxFileSizeBytes = 20 * 1024 * 1024;

    private readonly KnowledgeRepository _repository;
    private readonly IAiChatClient _aiClient;
    private readonly ILogger<KnowledgeIngestionService> _logger;

    public KnowledgeIngestionService(
        KnowledgeRepository repository,
        IAiChatClient aiClient,
        ILogger<KnowledgeIngestionService> logger)
    {
        _repository = repository;
        _aiClient = aiClient;
        _logger = logger;
    }

    /// <summary>自由入力テキストのナレッジを登録し、同期でインデックスまで行う。</summary>
    public async Task<KnowledgeDetail> CreateTextEntryAsync(
        string scope, string category, string? businessTypeCode, string? deptCode, string? bizDomain,
        string title, string content, string createdBy, CancellationToken cancellationToken = default)
    {
        Validate(scope, category, businessTypeCode, deptCode, bizDomain);
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "title は必須です。");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "content は必須です。");
        }

        var chunks = BuildChunks(title, content);
        var data = new KnowledgeCreateData(
            scope, category, Normalize(businessTypeCode), Normalize(deptCode), Normalize(bizDomain),
            title.Trim(), content, "text", null, null, null, null, createdBy);

        var id = await _repository.InsertAsync(data, "indexed", null, chunks, cancellationToken)
                 ?? throw new AppException(ErrorCodes.Unexpected, 500);
        return await GetDetailAsync(id, cancellationToken);
    }

    /// <summary>
    /// ファイルのナレッジを登録する。原本を保管しつつテキスト抽出→インデックスまで同期実行する。
    /// 画像は AI 設定時のみ内容説明を自動生成し、抽出失敗時も登録は成功させる（index_state=failed）。
    /// </summary>
    public async Task<KnowledgeDetail> CreateFileEntryAsync(
        string scope, string category, string? businessTypeCode, string? deptCode, string? bizDomain,
        string? title, string? description, string fileName, byte[] fileData, string createdBy,
        CancellationToken cancellationToken = default)
    {
        Validate(scope, category, businessTypeCode, deptCode, bizDomain);
        if (string.IsNullOrWhiteSpace(fileName) || !KnowledgeTextExtractor.IsAllowed(fileName))
        {
            throw new AppException(
                ErrorCodes.InvalidRequest, 400,
                $"対応していないファイル形式です（{string.Join(" ", KnowledgeTextExtractor.AllowedExtensions)} のみ）。");
        }

        if (fileData.LongLength == 0)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "ファイルが空です。");
        }

        if (fileData.LongLength > MaxFileSizeBytes)
        {
            throw new AppException(ErrorCodes.ImportFileTooLarge, 413,
                $"ファイルサイズが上限（{MaxFileSizeBytes / 1024 / 1024}MB）を超えています。");
        }

        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(fileName)
            : title.Trim();

        var (content, indexState, indexError) =
            await ExtractContentAsync(fileName, fileData, description, cancellationToken);

        var chunks = BuildChunks(resolvedTitle, content);
        var data = new KnowledgeCreateData(
            scope, category, Normalize(businessTypeCode), Normalize(deptCode), Normalize(bizDomain),
            resolvedTitle, content, "file",
            Path.GetFileName(fileName), KnowledgeTextExtractor.ResolveContentType(fileName), fileData,
            null, createdBy);

        var id = await _repository.InsertAsync(data, indexState, indexError, chunks, cancellationToken)
                 ?? throw new AppException(ErrorCodes.Unexpected, 500);
        return await GetDetailAsync(id, cancellationToken);
    }

    /// <summary>
    /// ナレッジの内容・分類を更新する。本文またはタイトルの変更時はチャンク・ベクターを再生成する。
    /// </summary>
    public async Task<KnowledgeDetail> UpdateEntryAsync(
        Guid id, KnowledgeUpdatePatch patch, string updatedBy, CancellationToken cancellationToken = default)
    {
        var current = await GetDetailAsync(id, cancellationToken);

        var title = string.IsNullOrWhiteSpace(patch.Title) ? current.Title : patch.Title.Trim();
        var content = patch.Content ?? current.Content;
        var category = string.IsNullOrWhiteSpace(patch.Category) ? current.Category : patch.Category;
        var businessTypeCode = patch.BusinessTypeCodeSpecified
            ? Normalize(patch.BusinessTypeCode)
            : current.BusinessTypeCode;
        var deptCode = patch.DeptCodeSpecified ? Normalize(patch.DeptCode) : current.DeptCode;
        var bizDomain = patch.BizDomainSpecified ? Normalize(patch.BizDomain) : current.BizDomain;

        Validate(current.Scope, category, businessTypeCode, deptCode, bizDomain);

        var chunks = BuildChunks(title, content);
        await _repository.UpdateAsync(
            id, title, content, category, businessTypeCode, deptCode, bizDomain,
            "indexed", null, chunks, updatedBy, cancellationToken);
        return await GetDetailAsync(id, cancellationToken);
    }

    /// <summary>
    /// 再インデックス。原本は不変のまま、現在の本文からチャンク・ベクターを再生成する。
    /// 本文が空のファイルナレッジ（過去の抽出失敗）は原本から抽出をやり直す。
    /// </summary>
    public async Task<KnowledgeDetail> ReindexAsync(Guid id, string updatedBy, CancellationToken cancellationToken = default)
    {
        var current = await GetDetailAsync(id, cancellationToken);

        var content = current.Content;
        var indexState = "indexed";
        string? indexError = null;

        if (string.IsNullOrWhiteSpace(content) && current.SourceType == "file")
        {
            var file = await _repository.GetFileAsync(id, cancellationToken);
            if (file is not null)
            {
                (content, indexState, indexError) =
                    await ExtractContentAsync(file.FileName, file.Data, null, cancellationToken);
            }
        }

        var chunks = BuildChunks(current.Title, content);
        await _repository.UpdateAsync(
            id, current.Title, content, current.Category, current.BusinessTypeCode, current.DeptCode,
            current.BizDomain, indexState, indexError, chunks, updatedBy, cancellationToken);
        return await GetDetailAsync(id, cancellationToken);
    }

    /// <summary>タイトルを見出しとして本文と連結し、チャンク＋ベクターを生成する。</summary>
    public static IReadOnlyList<(string Content, float[] Vector)> BuildChunks(string title, string content)
    {
        var source = string.IsNullOrWhiteSpace(content)
            ? $"# {title}"
            : $"# {title}\n{content}";
        return RagChunker.Split(source)
            .Select(chunk => (chunk, HashingVectorizer.Embed(chunk)))
            .ToList();
    }

    private async Task<(string Content, string IndexState, string? IndexError)> ExtractContentAsync(
        string fileName, byte[] fileData, string? description, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(description))
        {
            parts.Add(description.Trim());
        }

        try
        {
            var extracted = KnowledgeTextExtractor.Extract(fileName, fileData);
            if (extracted.RequiresAiDescription)
            {
                if (_aiClient.IsConfigured)
                {
                    var mediaType = KnowledgeTextExtractor.ResolveImageMediaType(fileName);
                    var aiDescription = await _aiClient.DescribeImageAsync(
                        fileData, mediaType, description, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(aiDescription))
                    {
                        parts.Add(aiDescription);
                    }
                }
                else if (parts.Count == 0)
                {
                    // 画像かつ説明もなし: タイトルのみでインデックスされる旨を記録する。
                    return (string.Empty, "indexed", null);
                }
            }
            else if (!string.IsNullOrWhiteSpace(extracted.Text))
            {
                parts.Add(extracted.Text);
            }

            return (string.Join("\n\n", parts), "indexed", null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 抽出・AI 説明の失敗は登録を止めない（原則4）。説明文のみでも検索可能にする。
            _logger.LogWarning(ex, "ナレッジのテキスト抽出に失敗しました（{FileName}）", fileName);
            var message = ex is AppException app ? app.Error.Summary : ex.Message;
            return (string.Join("\n\n", parts), "failed", $"テキスト抽出に失敗しました: {message}");
        }
    }

    private async Task<KnowledgeDetail> GetDetailAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetAsync(id, cancellationToken)
        ?? throw new AppException(ErrorCodes.KnowledgeNotFound, 404);

    private static void Validate(
        string scope, string category, string? businessTypeCode, string? deptCode, string? bizDomain)
    {
        var errors = KnowledgeTaxonomy.ValidateEntry(
            scope, category, Normalize(businessTypeCode), Normalize(deptCode), Normalize(bizDomain));
        if (errors.Count > 0)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, errors[0], errors);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
