using System.Text.Json;
using Dapper;
using UndeuxSales.Core.SubsidiaryCheck.ManualCheck;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.SubsidiaryCheck;

/// <summary>
/// タグパターン（subsidiary_tag_pattern）のデータアクセス。SoT はアプリ（設定系データ）。
/// fields（項目定義配列）は API ワイヤ形式と同一の JSON（camelCase）を jsonb で保持する。
/// </summary>
public sealed class TagPatternRepository
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string Columns = """
        pattern_id, name, description, fields::text AS fields_json, is_active,
        created_by, created_at, updated_by, updated_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public TagPatternRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        DapperConfiguration.Initialize();
    }

    /// <summary>項目定義配列を保存用 JSON（camelCase）へ直列化する。</summary>
    public static string SerializeFields(IReadOnlyList<TagPatternField> fields) =>
        JsonSerializer.Serialize(fields, WriteOptions);

    /// <summary>タグパターン一覧（更新日時降順）。<paramref name="includeInactive"/>=false は有効なもののみ。</summary>
    public async Task<IReadOnlyList<TagPattern>> ListAsync(
        bool includeInactive, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PatternRow>(new CommandDefinition($"""
            SELECT {Columns}
            FROM subsidiary_tag_pattern
            WHERE @includeInactive OR is_active
            ORDER BY updated_at DESC, pattern_id;
            """, new { includeInactive }, cancellationToken: cancellationToken));
        return rows.Select(ToPattern).ToList();
    }

    /// <summary>タグパターン1件。未存在は null。</summary>
    public async Task<TagPattern?> GetAsync(Guid patternId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<PatternRow>(new CommandDefinition($"""
            SELECT {Columns}
            FROM subsidiary_tag_pattern
            WHERE pattern_id = @patternId;
            """, new { patternId }, cancellationToken: cancellationToken));
        return row is null ? null : ToPattern(row);
    }

    /// <summary>タグパターンを新規登録する（uuid はアプリ側生成）。</summary>
    public async Task InsertAsync(
        Guid patternId,
        string name,
        string description,
        string fieldsJson,
        bool isActive,
        string createdBy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO subsidiary_tag_pattern
                (pattern_id, name, description, fields, is_active, created_by, updated_by)
            VALUES
                (@patternId, @name, @description, @fieldsJson::jsonb, @isActive, @createdBy, @createdBy);
            """,
            new { patternId, name, description, fieldsJson, isActive, createdBy },
            cancellationToken: cancellationToken));
    }

    /// <summary>タグパターンを更新する。created_by / created_at（記録系）は変更しない。</summary>
    /// <returns>更新された行数（0 = 未存在）。</returns>
    public async Task<int> UpdateAsync(
        Guid patternId,
        string name,
        string description,
        string fieldsJson,
        bool isActive,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE subsidiary_tag_pattern
               SET name        = @name,
                   description = @description,
                   fields      = @fieldsJson::jsonb,
                   is_active   = @isActive,
                   updated_by  = @updatedBy,
                   updated_at  = now()
             WHERE pattern_id = @patternId;
            """,
            new { patternId, name, description, fieldsJson, isActive, updatedBy },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// タグパターンを削除する。既存チェックの pattern_id 参照は ON DELETE SET NULL で温存され、
    /// pattern_name スナップショットにより履歴表示は維持される（原則7）。
    /// </summary>
    /// <returns>削除された行数（0 = 未存在）。</returns>
    public async Task<int> DeleteAsync(Guid patternId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM subsidiary_tag_pattern WHERE pattern_id = @patternId;
            """, new { patternId }, cancellationToken: cancellationToken));
    }

    private static TagPattern ToPattern(PatternRow row) => new(
        row.PatternId,
        row.Name,
        row.Description,
        DeserializeFields(row.FieldsJson),
        row.IsActive,
        row.CreatedBy,
        row.CreatedAt,
        row.UpdatedBy,
        row.UpdatedAt);

    private static IReadOnlyList<TagPatternField> DeserializeFields(string? fieldsJson)
    {
        if (string.IsNullOrWhiteSpace(fieldsJson))
        {
            return Array.Empty<TagPatternField>();
        }

        return JsonSerializer.Deserialize<List<TagPatternField>>(fieldsJson, ReadOptions)
               ?? (IReadOnlyList<TagPatternField>)Array.Empty<TagPatternField>();
    }

    private sealed record PatternRow(
        Guid PatternId,
        string Name,
        string Description,
        string? FieldsJson,
        bool IsActive,
        string CreatedBy,
        DateTime CreatedAt,
        string UpdatedBy,
        DateTime UpdatedAt);
}
