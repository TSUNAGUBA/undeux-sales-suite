namespace UndeuxSales.Infrastructure.Rag;

/// <summary>ナレッジ一覧の1件（本文・ファイルバイナリなし）。</summary>
public sealed record KnowledgeSummary(
    Guid Id,
    string Scope,
    string Category,
    string? BusinessTypeCode,
    string? DeptCode,
    string? BizDomain,
    string Title,
    string SourceType,
    string? FileName,
    long? FileSizeBytes,
    string ContentPreview,
    string IndexState,
    string? IndexError,
    int ChunkCount,
    bool IsSeed,
    DateTime UpdatedAt,
    string UpdatedBy);

/// <summary>ナレッジ詳細（本文つき）。</summary>
public sealed record KnowledgeDetail(
    Guid Id,
    string Scope,
    string Category,
    string? BusinessTypeCode,
    string? DeptCode,
    string? BizDomain,
    string Title,
    string Content,
    string SourceType,
    string? FileName,
    long? FileSizeBytes,
    string IndexState,
    string? IndexError,
    int ChunkCount,
    bool IsSeed,
    DateTime UpdatedAt,
    string UpdatedBy);

/// <summary>ナレッジ一覧の応答（ページング）。</summary>
public sealed record KnowledgeListResult(
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<KnowledgeSummary> Items);

/// <summary>原本ファイル（ダウンロード用）。</summary>
public sealed record KnowledgeFilePayload(string FileName, string ContentType, byte[] Data);

/// <summary>ナレッジ一覧の絞込条件。</summary>
public sealed record KnowledgeListFilter(
    string? Scope,
    string? Category,
    string? BusinessTypeCode,
    string? DeptCode,
    string? Search,
    int Page,
    int PageSize);

/// <summary>ナレッジ新規登録データ（取込サービス内部用）。</summary>
public sealed record KnowledgeCreateData(
    string Scope,
    string Category,
    string? BusinessTypeCode,
    string? DeptCode,
    string? BizDomain,
    string Title,
    string Content,
    string SourceType,
    string? FileName,
    string? FileContentType,
    byte[]? FileData,
    string? SeedSlug,
    string CreatedBy);

/// <summary>ナレッジ更新データ（null のフィールドは変更しない）。</summary>
public sealed record KnowledgeUpdatePatch(
    string? Title,
    string? Content,
    string? Category,
    string? BusinessTypeCode,
    string? DeptCode,
    string? BizDomain,
    bool BusinessTypeCodeSpecified,
    bool DeptCodeSpecified,
    bool BizDomainSpecified);

/// <summary>RAG 検索のヒット1件（Content はチャンク全文。API 応答には Snippet のみ載せる）。</summary>
public sealed record RagSearchHit(
    long ChunkId,
    Guid EntryId,
    string Title,
    string Category,
    string? BusinessTypeCode,
    string? DeptCode,
    int Seq,
    string Snippet,
    string Content,
    double Score,
    double VectorScore,
    double LexicalScore);

/// <summary>RAG 検索の絞込（チャットモード別）。</summary>
public sealed record RagSearchScope(
    string? ChatDomain,
    string? BusinessTypeCode,
    string? DeptCode)
{
    /// <summary>絞込なし（RAG設定の検索テスト・既定）。</summary>
    public static readonly RagSearchScope All = new(null, null, null);

    /// <summary>業務チャット: 部署タグ一致＋共通（タグなし）を対象にする。</summary>
    public static RagSearchScope ForBusinessChat(string domain) => new(domain, null, null);

    /// <summary>商談チャット: 全社共通＋当該業態＋当該部門を対象にする。</summary>
    public static RagSearchScope ForNegotiation(string businessTypeCode, string deptCode) =>
        new(null, businessTypeCode, deptCode);
}

/// <summary>スコープ×カテゴリ別の件数（RAG設定のステータス表示用）。</summary>
public sealed record KnowledgeCount(string Scope, string Category, int Count);
