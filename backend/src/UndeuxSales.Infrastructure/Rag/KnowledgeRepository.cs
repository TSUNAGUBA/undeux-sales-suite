using Dapper;
using Npgsql;
using UndeuxSales.Core;
using UndeuxSales.Core.Rag;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.Rag;

/// <summary>
/// ナレッジストア（knowledge スキーマ）のデータアクセス。
/// <para>
/// SoT は knowledge.entry（原本）。chunk / chunk_embedding は派生であり、
/// 本リポジトリの書込は常に「entry 確定 → chunk → embedding」の順で
/// 同一トランザクション内に行う（原則6）。
/// </para>
/// </summary>
public sealed class KnowledgeRepository
{
    private const string SummaryColumns = """
        e.id, e.scope, e.category, e.business_type_code, e.dept_code, e.biz_domain,
        e.title, e.source_type, e.file_name, e.file_size_bytes,
        LEFT(e.content, 200) AS content_preview,
        e.index_state, e.index_error, e.chunk_count,
        (e.seed_slug IS NOT NULL) AS is_seed,
        e.updated_at, e.updated_by
        """;

    /// <summary>字句類似（word_similarity）を計算する候補チャンク数の上限（ベクトル上位から選抜）。</summary>
    private const int CandidateLimit = 200;

    // pg_trgm 拡張の有無はプロセス起動後に不変とみなし static にキャッシュする。
    // 稼働中に拡張を導入した場合は API 再起動で字句スコアが有効化される。
    private static readonly object TrgmLock = new();
    private static bool? _trgmAvailable;

    private readonly IDbConnectionFactory _connectionFactory;

    public KnowledgeRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        DapperConfiguration.Initialize();
    }

    /// <summary>一覧（ページング・絞込・タイトル/本文の部分一致検索）。</summary>
    public async Task<KnowledgeListResult> ListAsync(KnowledgeListFilter filter, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var conditions = new List<string> { "e.status = 'active'" };
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(filter.Scope))
        {
            conditions.Add("e.scope = @scope");
            parameters.Add("scope", filter.Scope);
        }

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            conditions.Add("e.category = @category");
            parameters.Add("category", filter.Category);
        }

        if (!string.IsNullOrWhiteSpace(filter.BusinessTypeCode))
        {
            conditions.Add("e.business_type_code = @businessTypeCode");
            parameters.Add("businessTypeCode", filter.BusinessTypeCode);
        }

        if (!string.IsNullOrWhiteSpace(filter.DeptCode))
        {
            conditions.Add("e.dept_code = @deptCode");
            parameters.Add("deptCode", filter.DeptCode);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            conditions.Add("(e.title ILIKE @search OR e.content ILIKE @search)");
            parameters.Add("search", $"%{EscapeLike(filter.Search)}%");
        }

        var where = string.Join(" AND ", conditions);
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        parameters.Add("limit", pageSize);
        parameters.Add("offset", (page - 1) * pageSize);

        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM knowledge.entry e WHERE {where};", parameters, cancellationToken: cancellationToken));

        var items = (await connection.QueryAsync<KnowledgeSummary>(new CommandDefinition($"""
            SELECT {SummaryColumns}
            FROM knowledge.entry e
            WHERE {where}
            ORDER BY e.updated_at DESC, e.id
            LIMIT @limit OFFSET @offset;
            """, parameters, cancellationToken: cancellationToken))).ToList();

        return new KnowledgeListResult(total, page, pageSize, items);
    }

    /// <summary>スコープ×カテゴリ別の件数（active のみ）。</summary>
    public async Task<IReadOnlyList<KnowledgeCount>> CountByScopeCategoryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<KnowledgeCount>(new CommandDefinition("""
            SELECT scope, category, COUNT(*)::int AS count
            FROM knowledge.entry
            WHERE status = 'active'
            GROUP BY scope, category
            ORDER BY scope, category;
            """, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <summary>詳細（本文つき）。未存在・論理削除済みは null。</summary>
    public async Task<KnowledgeDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<KnowledgeDetail>(new CommandDefinition("""
            SELECT e.id, e.scope, e.category, e.business_type_code, e.dept_code, e.biz_domain,
                   e.title, e.content, e.source_type, e.file_name, e.file_size_bytes,
                   e.index_state, e.index_error, e.chunk_count,
                   (e.seed_slug IS NOT NULL) AS is_seed,
                   e.updated_at, e.updated_by
            FROM knowledge.entry e
            WHERE e.id = @id AND e.status = 'active';
            """, new { id }, cancellationToken: cancellationToken));
    }

    /// <summary>原本ファイル（sourceType=file のみ）。未存在は null。</summary>
    public async Task<KnowledgeFilePayload?> GetFileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<KnowledgeFilePayload>(new CommandDefinition("""
            SELECT e.file_name, COALESCE(e.file_content_type, 'application/octet-stream') AS content_type, e.file_data AS data
            FROM knowledge.entry e
            WHERE e.id = @id AND e.status = 'active'
              AND e.source_type = 'file' AND e.file_data IS NOT NULL;
            """, new { id }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// 新規エントリをチャンク・ベクターとともに1トランザクションで登録する。
    /// seedSlug 指定時は ON CONFLICT DO NOTHING（既存シードは温存。原則2）で、
    /// 既存だった場合は null を返す。
    /// </summary>
    public async Task<Guid?> InsertAsync(
        KnowledgeCreateData data,
        string indexState,
        string? indexError,
        IReadOnlyList<(string Content, float[] Vector)> chunks,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var conflictClause = data.SeedSlug is null ? string.Empty : "ON CONFLICT (seed_slug) DO NOTHING";
        Guid? id;
        try
        {
            id = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition($"""
            INSERT INTO knowledge.entry
                (scope, category, business_type_code, dept_code, biz_domain,
                 title, content, source_type, file_name, file_content_type, file_size_bytes, file_data,
                 index_state, index_error, chunk_count, seed_slug, created_by, updated_by)
            VALUES
                (@scope, @category, @businessTypeCode, @deptCode, @bizDomain,
                 @title, @content, @sourceType, @fileName, @fileContentType, @fileSizeBytes, @fileData,
                 @indexState, @indexError, @chunkCount, @seedSlug, @createdBy, @createdBy)
            {conflictClause}
            RETURNING id;
            """,
            new
            {
                scope = data.Scope,
                category = data.Category,
                businessTypeCode = data.BusinessTypeCode,
                deptCode = data.DeptCode,
                bizDomain = data.BizDomain,
                title = data.Title,
                content = data.Content,
                sourceType = data.SourceType,
                fileName = data.FileName,
                fileContentType = data.FileContentType,
                fileSizeBytes = data.FileData is null ? (long?)null : data.FileData.LongLength,
                fileData = data.FileData,
                indexState,
                indexError,
                chunkCount = chunks.Count,
                seedSlug = data.SeedSlug,
                createdBy = data.CreatedBy,
            },
            transaction, cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw TranslateForeignKeyViolation();
        }

        if (id is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await InsertChunksAsync(connection, transaction, id.Value, chunks, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    /// <summary>
    /// エントリの内容・分類を更新し、チャンク・ベクターを差し替える（1トランザクション）。
    /// 原本（file_data）は変更しない。
    /// </summary>
    public async Task UpdateAsync(
        Guid id,
        string title,
        string content,
        string category,
        string? businessTypeCode,
        string? deptCode,
        string? bizDomain,
        string indexState,
        string? indexError,
        IReadOnlyList<(string Content, float[] Vector)> chunks,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int affected;
        try
        {
            affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE knowledge.entry
            SET title = @title,
                content = @content,
                category = @category,
                business_type_code = @businessTypeCode,
                dept_code = @deptCode,
                biz_domain = @bizDomain,
                index_state = @indexState,
                index_error = @indexError,
                chunk_count = @chunkCount,
                updated_by = @updatedBy,
                updated_at = now()
            WHERE id = @id AND status = 'active';
            """,
            new
            {
                id, title, content, category, businessTypeCode, deptCode, bizDomain,
                indexState, indexError, chunkCount = chunks.Count, updatedBy,
            },
            transaction, cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw TranslateForeignKeyViolation();
        }

        if (affected == 0)
        {
            throw new AppException(ErrorCodes.KnowledgeNotFound, 404);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM knowledge.chunk WHERE entry_id = @id;", new { id }, transaction, cancellationToken: cancellationToken));
        await InsertChunksAsync(connection, transaction, id, chunks, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 削除。シード由来は論理削除（再起動時のシード再投入で再出現しないようにする）、
    /// 手動登録は物理削除（チャンクは FK CASCADE で連鎖削除）。
    /// </summary>
    public async Task DeleteAsync(Guid id, string updatedBy, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<(Guid Id, string? SeedSlug)?>(new CommandDefinition(
            "SELECT id, seed_slug FROM knowledge.entry WHERE id = @id AND status = 'active';",
            new { id }, transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            throw new AppException(ErrorCodes.KnowledgeNotFound, 404);
        }

        if (row.Value.SeedSlug is not null)
        {
            // 論理削除。検索対象から外すためチャンクも除去する（原本は entry に温存）。
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE knowledge.entry
                SET status = 'deleted', chunk_count = 0, updated_by = @updatedBy, updated_at = now()
                WHERE id = @id;
                """, new { id, updatedBy }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM knowledge.chunk WHERE entry_id = @id;", new { id }, transaction, cancellationToken: cancellationToken));
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM knowledge.entry WHERE id = @id;", new { id }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// ハイブリッド検索（ベクトルコサイン類似 ＋ 字句類似）。
    /// scope（operator/user）は常に両方を対象とし、status=active のみを検索する。
    /// pg_trgm が無い環境では字句スコアを 0 としてベクトルのみで順位付けする。
    /// </summary>
    public async Task<IReadOnlyList<RagSearchHit>> SearchAsync(
        string query,
        float[] queryVector,
        RagSearchScope scope,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var trgm = await IsTrgmAvailableAsync(connection, cancellationToken);

        var conditions = new List<string> { "e.status = 'active'" };
        var parameters = new DynamicParameters();
        parameters.Add("query", query);
        parameters.Add("queryVector", queryVector);
        parameters.Add("model", HashingVectorizer.ModelName);
        parameters.Add("limit", Math.Clamp(limit, 1, 50));

        if (scope.ChatDomain is not null)
        {
            conditions.Add("(e.biz_domain = @chatDomain OR e.biz_domain IS NULL)");
            parameters.Add("chatDomain", scope.ChatDomain);
        }

        if (scope.BusinessTypeCode is not null)
        {
            conditions.Add("""
                (
                    e.category IN ('group', 'basic', 'manual')
                    OR (e.category = 'business_type' AND e.business_type_code = @businessTypeCode)
                    OR (e.category = 'department' AND e.business_type_code = @businessTypeCode
                        AND (e.dept_code = @deptCode OR @deptCode IS NULL))
                )
                """);
            parameters.Add("businessTypeCode", scope.BusinessTypeCode);
            parameters.Add("deptCode", scope.DeptCode);
        }

        parameters.Add("dim", HashingVectorizer.Dim);
        parameters.Add("candidateLimit", CandidateLimit);

        // 2段階検索: ①メタデータ絞込後の候補全体にベクトルコサイン（比較的低コスト・実測 約0.1ms/チャンク）
        // を適用して上位 CandidateLimit 件へ絞り、②その候補にのみ高コストな字句類似
        // （word_similarity。実測でコサインの約2倍）を計算する。字句計算量が候補数で
        // 有界になり、チャンク総数の増加に対する劣化をベクトル走査ぶんに抑える
        // （さらに大規模化する場合は pgvector 索引への移行を想定。docs/design.md §12.2）。
        var lexicalExpression = trgm ? "word_similarity(@query, cand.content)" : "0::float8";
        var sql = $"""
            WITH cand AS (
                SELECT c.id AS chunk_id,
                       c.entry_id,
                       e.title,
                       e.category,
                       e.business_type_code,
                       e.dept_code,
                       c.seq,
                       c.content,
                       COALESCE(knowledge.cosine_similarity(ce.vector, @queryVector), 0)::float8 AS vector_score
                FROM knowledge.chunk c
                JOIN knowledge.entry e ON e.id = c.entry_id
                LEFT JOIN knowledge.chunk_embedding ce
                       ON ce.chunk_id = c.id AND ce.model = @model AND ce.dim = @dim
                WHERE {string.Join(" AND ", conditions)}
                ORDER BY vector_score DESC, c.id
                LIMIT @candidateLimit
            )
            SELECT cand.chunk_id,
                   cand.entry_id,
                   cand.title,
                   cand.category,
                   cand.business_type_code,
                   cand.dept_code,
                   cand.seq,
                   LEFT(cand.content, 160) AS snippet,
                   (0.6 * cand.vector_score + 0.4 * scores.lexical_score)::float8 AS score,
                   cand.vector_score,
                   scores.lexical_score,
                   cand.content AS full_content
            FROM cand
            CROSS JOIN LATERAL (
                SELECT ({lexicalExpression})::float8 AS lexical_score
            ) scores
            ORDER BY score DESC, cand.entry_id, cand.seq
            LIMIT @limit;
            """;

        var rows = await connection.QueryAsync<SearchRow>(new CommandDefinition(
            sql, parameters, cancellationToken: cancellationToken));

        return rows
            .Select(r => new RagSearchHit(
                r.ChunkId, r.EntryId, r.Title, r.Category, r.BusinessTypeCode, r.DeptCode,
                r.Seq, r.Snippet, r.FullContent, r.Score, r.VectorScore, r.LexicalScore))
            .ToList();
    }

    /// <summary>字句類似検索（pg_trgm）が利用可能か（/api/rag/status の運用可視化用）。</summary>
    public async Task<bool> IsLexicalSearchEnabledAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await IsTrgmAvailableAsync(connection, cancellationToken);
    }

    /// <summary>指定 seed_slug のエントリ（状態問わず）を取得する（シーダーの冪等判定用）。</summary>
    public async Task<(Guid Id, string Status)?> FindBySeedSlugAsync(string seedSlug, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<(Guid Id, string Status)?>(new CommandDefinition(
            "SELECT id, status FROM knowledge.entry WHERE seed_slug = @seedSlug;",
            new { seedSlug }, cancellationToken: cancellationToken));
        return row;
    }

    /// <summary>
    /// 自動生成エントリ（業態・部門一覧）の本文を最新マスタで上書きし、チャンクを再生成する。
    /// 論理削除済み（運用者が削除）の場合は復活させない（原則2）。
    /// </summary>
    public async Task UpsertGeneratedEntryAsync(
        string seedSlug,
        string title,
        string content,
        IReadOnlyList<(string Content, float[] Vector)> chunks,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindBySeedSlugAsync(seedSlug, cancellationToken);
        if (existing is { Status: "deleted" })
        {
            return;
        }

        if (existing is null)
        {
            await InsertAsync(
                new KnowledgeCreateData(
                    KnowledgeTaxonomy.ScopeOperator, KnowledgeTaxonomy.CategoryGroup,
                    null, null, null, title, content, "text", null, null, null, seedSlug, "system"),
                "indexed", null, chunks, cancellationToken);
            return;
        }

        await UpdateAsync(
            existing.Value.Id, title, content, KnowledgeTaxonomy.CategoryGroup,
            null, null, null, "indexed", null, chunks, "system", cancellationToken);
    }

    /// <summary>
    /// エントリ書込時の FK 違反を想定エラー（400）へ変換する。knowledge.entry の FK は
    /// business_type_code → business_type(code) のみ（UI 外から未知の業態コードを
    /// 指定した場合。原則: 想定エラーにはエラーコードを付与し 500 にしない）。
    /// </summary>
    private static AppException TranslateForeignKeyViolation() =>
        new(ErrorCodes.InvalidRequest, 400, "指定された業態コードが存在しません。");

    private static async Task InsertChunksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid entryId,
        IReadOnlyList<(string Content, float[] Vector)> chunks,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunkId = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO knowledge.chunk (entry_id, seq, content, char_count)
                VALUES (@entryId, @seq, @content, @charCount)
                RETURNING id;
                """,
                new { entryId, seq = i + 1, content = chunks[i].Content, charCount = chunks[i].Content.Length },
                transaction, cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO knowledge.chunk_embedding (chunk_id, model, dim, vector)
                VALUES (@chunkId, @model, @dim, @vector);
                """,
                new { chunkId, model = HashingVectorizer.ModelName, dim = HashingVectorizer.Dim, vector = chunks[i].Vector },
                transaction, cancellationToken: cancellationToken));
        }
    }

    private async Task<bool> IsTrgmAvailableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (_trgmAvailable is { } cached)
        {
            return cached;
        }

        var available = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm');",
            cancellationToken: cancellationToken));
        lock (TrgmLock)
        {
            _trgmAvailable = available;
        }

        return available;
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private sealed record SearchRow(
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
        double LexicalScore,
        string FullContent);
}
