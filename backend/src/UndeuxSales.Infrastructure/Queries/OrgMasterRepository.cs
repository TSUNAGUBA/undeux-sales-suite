using Dapper;
using Npgsql;
using UndeuxSales.Core;
using UndeuxSales.Infrastructure.Database;

namespace UndeuxSales.Infrastructure.Queries;

/// <summary>業態ツリー（業態＋商品部＋部門）のレスポンスモデル。</summary>
public sealed record OrgBusinessTypeNode(
    string Code,
    string? DisplayName,
    string? ShortName,
    IReadOnlyList<OrgSection> Sections,
    IReadOnlyList<OrgDepartment> Departments);

/// <summary>商品部（部署）。</summary>
public sealed record OrgSection(
    long Id,
    string BusinessTypeCode,
    string Name,
    string? CategoryNote,
    string? Phone,
    string? Floor,
    int DisplayOrder);

/// <summary>部門（業態×部門コード）。</summary>
public sealed record OrgDepartment(
    long Id,
    string BusinessTypeCode,
    string DeptCode,
    string DeptName,
    long? BuyerSectionId,
    int DisplayOrder);

/// <summary>相談受付デスク。</summary>
public sealed record ContactDesk(
    long Id,
    string Topic,
    string DepartmentName,
    string? Phone,
    string? ChatDomain,
    int DisplayOrder);

/// <summary>商品部の保存要求（Id=null で新規作成）。</summary>
public sealed record OrgSectionSaveRequest(
    long? Id,
    string BusinessTypeCode,
    string Name,
    string? CategoryNote,
    string? Phone,
    string? Floor,
    int DisplayOrder);

/// <summary>部門の保存要求（Id=null で新規作成）。</summary>
public sealed record OrgDepartmentSaveRequest(
    long? Id,
    string BusinessTypeCode,
    string DeptCode,
    string DeptName,
    long? BuyerSectionId,
    int DisplayOrder);

/// <summary>相談受付デスクの保存要求（Id=null で新規作成）。</summary>
public sealed record ContactDeskSaveRequest(
    long? Id,
    string Topic,
    string DepartmentName,
    string? Phone,
    string? ChatDomain,
    int DisplayOrder);

/// <summary>
/// しまむらグループ組織マスタ（業態×商品部×部門・相談受付デスク）のリポジトリ。
/// SoT はマスタテーブル自身（初期値はしまむら提供資料。以後は運用者の人的修正が正）。
/// </summary>
public sealed class OrgMasterRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public OrgMasterRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        DapperConfiguration.Initialize();
    }

    /// <summary>業態ツリー（全業態＋配下の商品部・部門）を取得する。</summary>
    public async Task<IReadOnlyList<OrgBusinessTypeNode>> GetTreeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        var businessTypes = (await connection.QueryAsync<(string Code, string? DisplayName, string? ShortName)>(
            new CommandDefinition("""
                SELECT code, display_name, short_name
                FROM business_type
                ORDER BY code;
                """, cancellationToken: cancellationToken))).ToList();

        var sections = (await connection.QueryAsync<OrgSection>(new CommandDefinition("""
            SELECT id, business_type_code, name, category_note, phone, floor, display_order
            FROM m_buyer_section
            ORDER BY business_type_code, display_order, id;
            """, cancellationToken: cancellationToken))).ToList();

        var departments = (await connection.QueryAsync<OrgDepartment>(new CommandDefinition("""
            SELECT id, business_type_code, dept_code, dept_name, buyer_section_id, display_order
            FROM m_section_department
            ORDER BY business_type_code, display_order, id;
            """, cancellationToken: cancellationToken))).ToList();

        var sectionLookup = sections.ToLookup(s => s.BusinessTypeCode);
        var departmentLookup = departments.ToLookup(d => d.BusinessTypeCode);

        return businessTypes
            .Select(bt => new OrgBusinessTypeNode(
                bt.Code,
                bt.DisplayName,
                bt.ShortName,
                sectionLookup[bt.Code].ToList(),
                departmentLookup[bt.Code].ToList()))
            .ToList();
    }

    /// <summary>相談受付デスク一覧を取得する。</summary>
    public async Task<IReadOnlyList<ContactDesk>> GetContactDesksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ContactDesk>(new CommandDefinition("""
            SELECT id, topic, department_name, phone, chat_domain, display_order
            FROM m_contact_desk
            ORDER BY display_order, id;
            """, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <summary>商品部を保存する（Id=null で新規作成）。保存後の行を返す。</summary>
    public async Task<OrgSection> SaveSectionAsync(OrgSectionSaveRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        // 業態変更時: 配下に旧業態の部門が残ると業態越境参照になり、自動生成ナレッジ等の
        // 派生物から部門が欠落する。先に部門側の業態を揃える（または紐づけを外す）運用を要求する。
        if (request.Id is not null)
        {
            var crossedDepartments = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                SELECT COUNT(*)::int
                FROM m_section_department
                WHERE buyer_section_id = @id AND business_type_code <> @businessTypeCode;
                """,
                new { id = request.Id, businessTypeCode = request.BusinessTypeCode },
                cancellationToken: cancellationToken));
            if (crossedDepartments > 0)
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    $"この商品部には別業態の部門が {crossedDepartments} 件紐づいています。先に部門の業態・所属を変更してください。");
            }
        }

        try
        {
            var parameters = new DynamicParameters(new
            {
                id = request.Id,
                businessTypeCode = request.BusinessTypeCode,
                name = request.Name,
                categoryNote = request.CategoryNote,
                phone = request.Phone,
                floor = request.Floor,
                displayOrder = request.DisplayOrder,
            });

            OrgSection? saved;
            if (request.Id is null)
            {
                saved = await connection.QuerySingleOrDefaultAsync<OrgSection>(new CommandDefinition("""
                    INSERT INTO m_buyer_section (business_type_code, name, category_note, phone, floor, display_order)
                    VALUES (@businessTypeCode, @name, @categoryNote, @phone, @floor, @displayOrder)
                    RETURNING id, business_type_code, name, category_note, phone, floor, display_order;
                    """, parameters, cancellationToken: cancellationToken));
            }
            else
            {
                saved = await connection.QuerySingleOrDefaultAsync<OrgSection>(new CommandDefinition("""
                    UPDATE m_buyer_section
                    SET business_type_code = @businessTypeCode,
                        name = @name,
                        category_note = @categoryNote,
                        phone = @phone,
                        floor = @floor,
                        display_order = @displayOrder,
                        updated_at = now()
                    WHERE id = @id
                    RETURNING id, business_type_code, name, category_note, phone, floor, display_order;
                    """, parameters, cancellationToken: cancellationToken));
            }

            return saved ?? throw new AppException(ErrorCodes.KnowledgeNotFound, 404, "指定された商品部が見つかりません。");
        }
        catch (PostgresException ex)
        {
            throw TranslateConstraintViolation(ex, "商品部");
        }
    }

    /// <summary>商品部を削除する。配下部門は所属未設定（buyer_section_id=NULL）で温存される。</summary>
    public async Task DeleteSectionAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM m_buyer_section WHERE id = @id;", new { id }, cancellationToken: cancellationToken));
        if (affected == 0)
        {
            throw new AppException(ErrorCodes.KnowledgeNotFound, 404, "指定された商品部が見つかりません。");
        }
    }

    /// <summary>部門を保存する（Id=null で新規作成）。保存後の行を返す。</summary>
    public async Task<OrgDepartment> SaveDepartmentAsync(OrgDepartmentSaveRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);

        // 所属商品部は同一業態のものに限定する（業態越境参照の防止。UI と同じ制約をサーバー側でも強制）。
        if (request.BuyerSectionId is not null)
        {
            var sectionBusinessType = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT business_type_code FROM m_buyer_section WHERE id = @id;",
                new { id = request.BuyerSectionId }, cancellationToken: cancellationToken));
            if (sectionBusinessType is null)
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400, "指定された商品部が存在しません。");
            }

            if (sectionBusinessType != request.BusinessTypeCode)
            {
                throw new AppException(ErrorCodes.InvalidRequest, 400,
                    "所属商品部は部門と同じ業態のものを指定してください。");
            }
        }

        try
        {
            var parameters = new DynamicParameters(new
            {
                id = request.Id,
                businessTypeCode = request.BusinessTypeCode,
                deptCode = request.DeptCode,
                deptName = request.DeptName,
                buyerSectionId = request.BuyerSectionId,
                displayOrder = request.DisplayOrder,
            });

            OrgDepartment? saved;
            if (request.Id is null)
            {
                saved = await connection.QuerySingleOrDefaultAsync<OrgDepartment>(new CommandDefinition("""
                    INSERT INTO m_section_department (business_type_code, dept_code, dept_name, buyer_section_id, display_order)
                    VALUES (@businessTypeCode, @deptCode, @deptName, @buyerSectionId, @displayOrder)
                    RETURNING id, business_type_code, dept_code, dept_name, buyer_section_id, display_order;
                    """, parameters, cancellationToken: cancellationToken));
            }
            else
            {
                saved = await connection.QuerySingleOrDefaultAsync<OrgDepartment>(new CommandDefinition("""
                    UPDATE m_section_department
                    SET business_type_code = @businessTypeCode,
                        dept_code = @deptCode,
                        dept_name = @deptName,
                        buyer_section_id = @buyerSectionId,
                        display_order = @displayOrder,
                        updated_at = now()
                    WHERE id = @id
                    RETURNING id, business_type_code, dept_code, dept_name, buyer_section_id, display_order;
                    """, parameters, cancellationToken: cancellationToken));
            }

            return saved ?? throw new AppException(ErrorCodes.KnowledgeNotFound, 404, "指定された部門が見つかりません。");
        }
        catch (PostgresException ex)
        {
            throw TranslateConstraintViolation(ex, "部門");
        }
    }

    /// <summary>部門を削除する。</summary>
    public async Task DeleteDepartmentAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM m_section_department WHERE id = @id;", new { id }, cancellationToken: cancellationToken));
        if (affected == 0)
        {
            throw new AppException(ErrorCodes.KnowledgeNotFound, 404, "指定された部門が見つかりません。");
        }
    }

    /// <summary>相談受付デスクを保存する（Id=null で新規作成）。保存後の行を返す。</summary>
    public async Task<ContactDesk> SaveContactDeskAsync(ContactDeskSaveRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        try
        {
            var parameters = new DynamicParameters(new
            {
                id = request.Id,
                topic = request.Topic,
                departmentName = request.DepartmentName,
                phone = request.Phone,
                chatDomain = request.ChatDomain,
                displayOrder = request.DisplayOrder,
            });

            ContactDesk? saved;
            if (request.Id is null)
            {
                saved = await connection.QuerySingleOrDefaultAsync<ContactDesk>(new CommandDefinition("""
                    INSERT INTO m_contact_desk (topic, department_name, phone, chat_domain, display_order)
                    VALUES (@topic, @departmentName, @phone, @chatDomain, @displayOrder)
                    RETURNING id, topic, department_name, phone, chat_domain, display_order;
                    """, parameters, cancellationToken: cancellationToken));
            }
            else
            {
                saved = await connection.QuerySingleOrDefaultAsync<ContactDesk>(new CommandDefinition("""
                    UPDATE m_contact_desk
                    SET topic = @topic,
                        department_name = @departmentName,
                        phone = @phone,
                        chat_domain = @chatDomain,
                        display_order = @displayOrder,
                        updated_at = now()
                    WHERE id = @id
                    RETURNING id, topic, department_name, phone, chat_domain, display_order;
                    """, parameters, cancellationToken: cancellationToken));
            }

            return saved ?? throw new AppException(ErrorCodes.KnowledgeNotFound, 404, "指定された相談受付デスクが見つかりません。");
        }
        catch (PostgresException ex)
        {
            throw TranslateConstraintViolation(ex, "相談受付デスク");
        }
    }

    /// <summary>相談受付デスクを削除する。</summary>
    public async Task DeleteContactDeskAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM m_contact_desk WHERE id = @id;", new { id }, cancellationToken: cancellationToken));
        if (affected == 0)
        {
            throw new AppException(ErrorCodes.KnowledgeNotFound, 404, "指定された相談受付デスクが見つかりません。");
        }
    }

    /// <summary>
    /// 一意制約・外部キー・CHECK 違反を利用者が対処可能な 400 エラーへ変換する。
    /// それ以外の DB エラーは既定のハンドリング（UNDX-DATA-001）に委ねるため再送出する。
    /// </summary>
    private static Exception TranslateConstraintViolation(PostgresException ex, string subject)
    {
        return ex.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => new AppException(
                ErrorCodes.InvalidRequest, 400, $"{subject}が重複しています（同一業態内で同じ名称・コードは登録できません）。"),
            PostgresErrorCodes.ForeignKeyViolation => new AppException(
                ErrorCodes.InvalidRequest, 400, "指定された業態コードまたは商品部が存在しません。"),
            PostgresErrorCodes.CheckViolation => new AppException(
                ErrorCodes.InvalidRequest, 400, "入力値が許可された値の範囲外です。"),
            _ => ex,
        };
    }
}
