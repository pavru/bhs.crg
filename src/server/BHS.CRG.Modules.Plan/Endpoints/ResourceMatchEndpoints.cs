using System.Text.Json.Serialization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Matching;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Сопоставление материалов строк с номенклатурой (change <c>plan-08-matching</c>, ТЗ PLAN-9.1, COST-7.1, AUTH-8,
/// AUTH-9): материал — <c>PUT …/resources/{resourceId}/match</c>, похожие позиции для окна выбора —
/// <c>GET …/resources/{resourceId}/candidates</c>.
///
/// <para><b>Похожие — отдельным адресом</b> (решение владельца 25.10.2026), а не в чтении строк: подбор читает
/// весь справочник номенклатуры, и страница строк с ним стала бы медленной. Окно выбора спрашивает при
/// открытии, как в счетах.</para>
///
/// <para><b>Ворота и версия</b> — как у строк (<see cref="MatchEndpoints" />): правом <c>plan.estimate.edit</c>,
/// версию называет редакция заголовком <c>If-Match</c>. Похожие только читают — правом чтения смет.</para>
/// </summary>
public static class ResourceMatchEndpoints
{
    /// <summary>Сколько похожих на материал — блок над списком, который читают, не прокручивая (как в счетах).</summary>
    public const int CandidatesLimit = 5;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/api/plan/revisions/{id:guid}/resources/{resourceId:guid}/match", MatchAsync)
            .WithTags("Сметы").RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateEdit));
        endpoints.MapGet("/api/plan/revisions/{id:guid}/resources/{resourceId:guid}/candidates", CandidatesAsync)
            .WithTags("Сметы").RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateRead));
    }

    private static async Task<IResult> MatchAsync(
        Guid id, Guid resourceId, ResourceMatchRequest body, HttpRequest request, PlanDbContext db,
        MaterialMatching matching, IModuleUser user, CancellationToken ct)
    {
        var revision = await db.EstimateRevisions.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Редакция не найдена — возможно, её удалили.");
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(revision), "Редакцию", "редакцию");
        var (row, material) = await FindAsync(db, id, resourceId, tracked: true, ct);

        Guid? choice = body.Clear == true
            ? null
            : body.NomenclatureId ?? throw new InvalidRequestException(
                "Позиция номенклатуры не названа («nomenclatureId»). Чтобы снять сопоставление материала, пришлите " +
                "«clear»: true.");
        var memory = await matching.MatchAsync(revision, row, material, choice, body.Remember ?? true, ct);

        var hidden = await EstimateViews.HiddenAsync(user, ct);
        var view = (await EstimateRowsEndpoints.ViewsAsync(db, revision, [row], hidden is null, ct))[0];
        return Results.Ok(new ResourceMatchResultDto(view, db.VersionOf(revision), memory, hidden));
    }

    /// <summary>
    /// Похожие позиции номенклатуры для материала — подбором ядра (<see cref="IModuleCatalog.SimilarAsync" />):
    /// код ресурса, то же наименование, большинство слов. Запомненное, которое подставить нельзя (позиция в архиве
    /// или удалена), названо отдельно: иначе память молчала бы непонятно почему.
    /// </summary>
    private static async Task<IResult> CandidatesAsync(
        Guid id, Guid resourceId, PlanDbContext db, IModuleCatalog catalog, CancellationToken ct)
    {
        var (_, material) = await FindAsync(db, id, resourceId, tracked: false, ct);
        var found = await catalog.SimilarAsync(FunctionalTag.RoleNomenclature,
                [new ModuleLineWords(material.SourceName, material.SourceCode)], CandidatesLimit, ct)
            // Пустой ответ здесь означал бы «подходящих нет», а справочника нет вовсе.
            ?? throw new ConflictException(EstimateRowReferences.NoNomenclature);
        var core = found[0];

        var recalled = ResourceMatching.Recall(
            await ResourceMatching.FindAsync(db, ResourceMatchKey.Sought(material.SourceCode, material.SourceName), ct),
            material.SourceCode, material.SourceName);
        var unusable = recalled is null ? null : await UnusableAsync(catalog, recalled, ct);

        return Results.Ok(new ResourceCandidatesDto(
            [.. core.Items.Select(c => new ResourceCandidateDto(
                c.Ref.Id, c.Ref.DisplayName, c.Ref.EntityType, c.Ref.MatchedAlias, c.Why, c.Strong, c.Matched))],
            core.More, core.InArchive, core.Unreadable, unusable));
    }

    private static async Task<RecalledUnusableDto?> UnusableAsync(IModuleCatalog catalog, ResourceMatch match, CancellationToken ct)
    {
        var position = (await catalog.RefsAsync(FunctionalTag.RoleNomenclature, [match.NomenclatureId], ct) ?? [])
            .FirstOrDefault(r => r.Id == match.NomenclatureId);
        return position switch
        {
            null => new(match.NomenclatureId, null, ResourceMatchListEndpoints.IssueLost),
            { Archived: true } => new(match.NomenclatureId, position.DisplayName, ResourceMatchListEndpoints.IssueArchived),
            _ => null,
        };
    }

    /// <summary>Материал этой редакции — вместе со строкой: её номер называют журнал и ответ.</summary>
    private static async Task<(EstimateRow Row, EstimateRowMaterial Material)> FindAsync(
        PlanDbContext db, Guid revisionId, Guid resourceId, bool tracked, CancellationToken ct)
    {
        var materials = tracked ? db.EstimateRowMaterials : db.EstimateRowMaterials.AsNoTracking();
        var rows = tracked ? db.EstimateRows : db.EstimateRows.AsNoTracking();
        var found = await (from m in materials
                           join r in rows on m.RowId equals r.Id
                           where m.Id == resourceId && r.RevisionId == revisionId
                           select new { Row = r, Material = m })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Материала нет в этой редакции — перечитайте строки редакции.");
        return (found.Row, found.Material);
    }
}

/// <param name="Clear"><c>true</c> — снять сопоставление материала; остальные поля тогда не читаются.</param>
/// <param name="Remember">Запомнить выбор по коду ресурса (без кода — по наименованию); не названо — запомнить.</param>
public sealed record ResourceMatchRequest(Guid? NomenclatureId, bool? Remember, bool? Clear);

/// <param name="Row">Строка материала целиком — как её покажет следующее чтение.</param>
/// <param name="Version">Новая версия редакции — её называет следующая правка.</param>
public sealed record ResourceMatchResultDto(
    EstimateRowDto Row, string Version, MatchMemory Memory,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HiddenPart>? Hidden);

/// <param name="Items">Действующие похожие, лучший первым, не больше <see cref="ResourceMatchEndpoints.CandidatesLimit" />.</param>
/// <param name="More">Подошло больше, чем показано.</param>
/// <param name="InArchive">Сколько подошло архивных позиций — заводить такую заново не нужно.</param>
/// <param name="Unreadable">Сколько позиций сверить не удалось — это не «не подошли».</param>
/// <param name="RecalledUnusable">Для ресурса запомнена позиция, которую выбрать нельзя.</param>
public sealed record ResourceCandidatesDto(
    IReadOnlyList<ResourceCandidateDto> Items, bool More, int InArchive, int Unreadable, RecalledUnusableDto? RecalledUnusable);

/// <param name="Why">Причины словами ядра, старшая первой.</param>
/// <param name="Strong">«Лучшее совпадение» — ставит только ядро.</param>
public sealed record ResourceCandidateDto(
    Guid Id, string? Name, string Type, string? MatchedAlias, IReadOnlyList<string> Why, bool Strong,
    ModuleLineMatched Matched);

/// <param name="Name">Название; <c>null</c> — позиции больше нет.</param>
/// <param name="Issue"><c>archived</c> — в архиве, <c>lost</c> — удалена.</param>
public sealed record RecalledUnusableDto(Guid Id, string? Name, string Issue);
