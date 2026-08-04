using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UndeuxSales.Core;
using UndeuxSales.Core.SubsidiaryCheck.ManualCheck;
using UndeuxSales.Infrastructure.SubsidiaryCheck;

namespace UndeuxSales.Api.Controllers;

/// <summary>
/// タグパターン（手入力チェックの入力フォーム構成テンプレート）の CRUD API。
/// 項目の組合せ・型・単複・突合方式を登録し、新規チェック時に呼び出してフォームを構成する。
/// SoT はアプリ（subsidiary_tag_pattern）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/subsidiary-check/tag-patterns")]
public sealed class TagPatternController : ControllerBase
{
    private readonly TagPatternRepository _repository;

    public TagPatternController(TagPatternRepository repository)
    {
        _repository = repository;
    }

    /// <summary>タグパターン一覧。<c>includeInactive=true</c> でアーカイブ済みも含める（既定は有効のみ）。</summary>
    [HttpGet]
    public Task<IReadOnlyList<TagPattern>> List(
        [FromQuery] bool includeInactive, CancellationToken cancellationToken)
        => _repository.ListAsync(includeInactive, cancellationToken);

    /// <summary>タグパターン1件。未存在は 404（UNDX-DATA-006）。</summary>
    [HttpGet("{patternId}")]
    public async Task<TagPattern> Get(string patternId, CancellationToken cancellationToken)
        => await _repository.GetAsync(ParseId(patternId), cancellationToken)
           ?? throw new AppException(ErrorCodes.TagPatternNotFound, 404);

    /// <summary>タグパターンを新規登録する。</summary>
    [HttpPost]
    public async Task<TagPattern> Create(
        [FromBody] TagPatternUpsert body, CancellationToken cancellationToken)
    {
        var (name, description, fields) = ValidateAndNormalize(body);
        var patternId = Guid.NewGuid();
        await _repository.InsertAsync(
            patternId, name, description, TagPatternRepository.SerializeFields(fields),
            body.IsActive, RequestIdentity.AuditUserOf(User), cancellationToken);
        return await _repository.GetAsync(patternId, cancellationToken)
               ?? throw new AppException(ErrorCodes.TagPatternNotFound, 404);
    }

    /// <summary>タグパターンを更新する。未存在は 404。</summary>
    [HttpPut("{patternId}")]
    public async Task<TagPattern> Update(
        string patternId, [FromBody] TagPatternUpsert body, CancellationToken cancellationToken)
    {
        var id = ParseId(patternId);
        var (name, description, fields) = ValidateAndNormalize(body);
        var updated = await _repository.UpdateAsync(
            id, name, description, TagPatternRepository.SerializeFields(fields),
            body.IsActive, RequestIdentity.AuditUserOf(User), cancellationToken);
        if (updated == 0)
        {
            throw new AppException(ErrorCodes.TagPatternNotFound, 404);
        }

        return await _repository.GetAsync(id, cancellationToken)
               ?? throw new AppException(ErrorCodes.TagPatternNotFound, 404);
    }

    /// <summary>タグパターンを削除する。既存チェックの参照は SET NULL で温存される。未存在は 404。</summary>
    [HttpDelete("{patternId}")]
    public async Task<IActionResult> Delete(string patternId, CancellationToken cancellationToken)
    {
        var deleted = await _repository.DeleteAsync(ParseId(patternId), cancellationToken);
        if (deleted == 0)
        {
            throw new AppException(ErrorCodes.TagPatternNotFound, 404);
        }

        return NoContent();
    }

    // ------------------------------------------------------------

    private const int MaxNameLength = 100;
    private const int MaxDescriptionLength = 500;
    private const int MaxKeyLength = 60;

    /// <summary>パターン定義を検証し、正規化した値（name/description/fields）を返す。</summary>
    private static (string Name, string Description, IReadOnlyList<TagPatternField> Fields) ValidateAndNormalize(
        TagPatternUpsert? body)
    {
        if (body is null)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "リクエストボディが空です。");
        }

        var name = (body.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "パターン名を入力してください。");
        }

        if (name.Length > MaxNameLength)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                $"パターン名は {MaxNameLength} 文字以内で入力してください。");
        }

        var description = (body.Description ?? string.Empty).Trim();
        if (description.Length > MaxDescriptionLength)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                $"説明は {MaxDescriptionLength} 文字以内で入力してください。");
        }

        var rawFields = body.Fields ?? new List<TagPatternField>();
        if (rawFields.Count == 0)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400, "項目を1つ以上定義してください。");
        }

        if (rawFields.Count > ManualCheckService.MaxInputFields)
        {
            throw new AppException(ErrorCodes.InvalidRequest, 400,
                $"項目は {ManualCheckService.MaxInputFields} 個以内にしてください。");
        }

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<TagPatternField>(rawFields.Count);
        foreach (var field in rawFields)
        {
            var key = (field?.Key ?? string.Empty).Trim();
            var label = (field?.Label ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400, "項目キーを入力してください。");
            }

            if (key.Length > MaxKeyLength)
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"項目キーは {MaxKeyLength} 文字以内にしてください（{key}）。");
            }

            if (!seenKeys.Add(key))
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400, $"項目キーが重複しています（{key}）。");
            }

            if (label.Length == 0)
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400, $"項目「{key}」のラベルを入力してください。");
            }

            var valueType = field!.ValueType?.Trim() ?? string.Empty;
            if (!ManualFieldValueType.IsKnown(valueType))
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"項目「{label}」の型が不正です（string / number）。");
            }

            var compare = field.Compare?.Trim() ?? string.Empty;
            if (!ManualFieldCompareMode.IsKnown(compare))
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"項目「{label}」の突合方式が不正です（text / ratio / careIcon）。");
            }

            fields.Add(new TagPatternField(key, label, valueType, field.Multiple, compare, field.Required));
        }

        return (name, description, fields);
    }

    private static Guid ParseId(string id)
        => Guid.TryParse(id, out var parsed)
            ? parsed
            : throw new AppException(ErrorCodes.TagPatternNotFound, 404, "ID の形式が不正です。");
}
