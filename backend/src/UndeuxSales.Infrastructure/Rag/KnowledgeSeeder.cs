using System.Text;
using Microsoft.Extensions.Logging;
using UndeuxSales.Core.Rag;
using UndeuxSales.Infrastructure.Queries;

namespace UndeuxSales.Infrastructure.Rag;

/// <summary>事前蓄積ナレッジ1件の投入元（Api の埋め込みリソースから読み込む）。</summary>
public sealed record KnowledgeSeedSource(
    string Slug,
    string Title,
    string Category,
    string? BusinessTypeCode,
    string? DeptCode,
    string? BizDomain,
    string? SourceDocument,
    string Content);

/// <summary>
/// 事前蓄積ナレッジ（しまむら提供資料由来）の投入と、
/// マスタ由来の自動生成ナレッジ（業態・部門・相談窓口一覧）の同期。
/// <para>
/// 冪等性: シードは seed_slug の ON CONFLICT DO NOTHING により初回のみ投入され、
/// 運用者の編集・削除は再起動しても巻き戻らない（原則2）。
/// 個別の失敗は記録して継続し、起動を妨げない（原則4）。
/// </para>
/// </summary>
public sealed class KnowledgeSeeder
{
    /// <summary>マスタ自動生成エントリの冪等キー。</summary>
    public const string OrgSeedSlug = "seed-auto-org-master";

    private const string OrgSeedTitle = "【自動生成】業態・部門・相談窓口一覧";

    private readonly KnowledgeRepository _repository;
    private readonly OrgMasterRepository _orgMasterRepository;
    private readonly ILogger<KnowledgeSeeder> _logger;

    public KnowledgeSeeder(
        KnowledgeRepository repository,
        OrgMasterRepository orgMasterRepository,
        ILogger<KnowledgeSeeder> logger)
    {
        _repository = repository;
        _orgMasterRepository = orgMasterRepository;
        _logger = logger;
    }

    /// <summary>シード一式を投入し、続けてマスタ自動生成ナレッジを同期する。</summary>
    public async Task SeedAsync(IReadOnlyList<KnowledgeSeedSource> sources, CancellationToken cancellationToken = default)
    {
        var inserted = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var existing = await _repository.FindBySeedSlugAsync(source.Slug, cancellationToken);
                if (existing is not null)
                {
                    skipped++;
                    continue;
                }

                var content = source.SourceDocument is null
                    ? source.Content
                    : $"> 出典資料: {source.SourceDocument}\n\n{source.Content}";

                var chunks = KnowledgeIngestionService.BuildChunks(source.Title, content);
                var id = await _repository.InsertAsync(
                    new KnowledgeCreateData(
                        KnowledgeTaxonomy.ScopeOperator, source.Category,
                        source.BusinessTypeCode, source.DeptCode, source.BizDomain,
                        source.Title, content, "text", null, null, null, source.Slug, "seed"),
                    "indexed", null, chunks, cancellationToken);

                if (id is null)
                {
                    skipped++;
                }
                else
                {
                    inserted++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "シードナレッジの投入に失敗しました（{Slug}）", source.Slug);
            }
        }

        _logger.LogInformation(
            "シードナレッジ投入完了: 追加 {Inserted} / 既存スキップ {Skipped} / 失敗 {Failed}", inserted, skipped, failed);

        await RefreshOrgKnowledgeAsync(cancellationToken);
    }

    /// <summary>
    /// マスタ（業態・部門・相談窓口）から自動生成ナレッジを再生成する。
    /// マスタ修正後にも呼び出され、SoT（マスタ）→ 派生（ナレッジ）の同期を保つ（原則6）。
    /// 運用者が本エントリを削除済みの場合は復活させない。
    /// </summary>
    public async Task RefreshOrgKnowledgeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tree = await _orgMasterRepository.GetTreeAsync(cancellationToken);
            var desks = await _orgMasterRepository.GetContactDesksAsync(cancellationToken);
            var content = BuildOrgContent(tree, desks);
            var chunks = KnowledgeIngestionService.BuildChunks(OrgSeedTitle, content);
            await _repository.UpsertGeneratedEntryAsync(OrgSeedSlug, OrgSeedTitle, content, chunks, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 補助処理のため主要フロー（起動・マスタ保存）は止めない（原則4）。
            _logger.LogWarning(ex, "マスタ自動生成ナレッジの更新に失敗しました");
        }
    }

    private static string BuildOrgContent(
        IReadOnlyList<OrgBusinessTypeNode> tree, IReadOnlyList<ContactDesk> desks)
    {
        var builder = new StringBuilder();
        builder.AppendLine("> 本ナレッジは業態・部門マスタ（マスタメンテナンス画面）から自動生成されます。マスタ修正時に自動で更新されるため、直接編集しないでください。");
        builder.AppendLine();
        builder.AppendLine("## しまむらグループの業態と部門（商品部・連絡先）");

        foreach (var businessType in tree)
        {
            if (businessType.Sections.Count == 0 && businessType.Departments.Count == 0)
            {
                continue;
            }

            builder.AppendLine();
            builder.AppendLine($"### 業態「{businessType.DisplayName ?? businessType.Code}」（業態コード {businessType.Code}）");

            // 所属先が同一業態の部署一覧に無い部門（未設定・越境参照の防御）は「部署未設定」へ合流させ、
            // 自動生成ナレッジからの欠落を防ぐ。
            var sectionIds = businessType.Sections.Select(s => (long?)s.Id).ToHashSet();
            var departmentsBySection = businessType.Departments
                .ToLookup(d => sectionIds.Contains(d.BuyerSectionId) ? d.BuyerSectionId : null);
            foreach (var section in businessType.Sections)
            {
                var note = string.IsNullOrWhiteSpace(section.CategoryNote) ? string.Empty : $"（{section.CategoryNote}）";
                var floor = string.IsNullOrWhiteSpace(section.Floor) ? string.Empty : $"・本社{section.Floor}";
                builder.AppendLine($"- {section.Name}{note}｜連絡先 {section.Phone ?? "未設定"}{floor}");
                foreach (var department in departmentsBySection[section.Id])
                {
                    builder.AppendLine($"  - 部門 {department.DeptCode}（{department.DeptName}）");
                }
            }

            var orphans = departmentsBySection[null].ToList();
            if (orphans.Count > 0)
            {
                builder.AppendLine("- 部署未設定の部門:");
                foreach (var department in orphans)
                {
                    builder.AppendLine($"  - 部門 {department.DeptCode}（{department.DeptName}）");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("## お取引についての相談受付窓口");
        foreach (var desk in desks)
        {
            builder.AppendLine($"- {desk.Topic}: {desk.DepartmentName} {desk.Phone ?? string.Empty}".TrimEnd());
        }

        return builder.ToString().Trim();
    }
}
