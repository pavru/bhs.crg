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
/// Список запомненных сопоставлений ресурсов: прочитать, перенацелить, забыть (change <c>plan-08-matching</c>,
/// ТЗ PLAN-9.1, D8 зонтика) — по образцу <see cref="RateMatchListEndpoints" />.
///
/// <para>Запомненное держит позицию номенклатуры от удаления (ТЗ CORE-34.1), и снять держатель можно только
/// здесь. Ключ не правится и руками не заводится: код и наименование — слова файла. Забытое не трогает
/// материалов, которые оно уже сопоставило: их пометка сама скажет «с тех пор забыли».</para>
/// </summary>
public static class ResourceMatchListEndpoints
{
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    /// <summary>Почему запомненное не подставится: позиции нет либо она в архиве.</summary>
    public const string IssueLost = RateMatchListEndpoints.IssueLost;
    public const string IssueArchived = RateMatchListEndpoints.IssueArchived;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var matches = endpoints.MapGroup("/api/plan/resource-matches").WithTags("Сметы");
        matches.MapGet("/", ListAsync).RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateRead));
        matches.MapPut("/{id:guid}", PointAsync).RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateEdit));
        matches.MapDelete("/{id:guid}", ForgetAsync).RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateEdit));
    }

    private static async Task<IResult> ListAsync(
        PlanDbContext db, IModuleCatalog catalog, CancellationToken ct, string? query = null, int skip = 0, int? take = null)
    {
        if (skip < 0 || take is <= 0 or > MaxTake)
            throw new InvalidRequestException($"Порция списка задана неверно: «skip» не меньше нуля, «take» от 1 до {MaxTake}.");

        var selected = db.ResourceMatches.AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            // По словам файла — коду и наименованию. Название позиции живёт в справочнике ядра.
            var pattern = "%" + query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            selected = selected.Where(m => EF.Functions.ILike(m.SourceText, pattern, "\\"));
        }

        var total = await selected.CountAsync(ct);
        // Отслеживаемыми — ради версии записи: она теневое свойство и вне отслеживания не читается.
        var page = await selected.OrderBy(m => m.SourceText).ThenBy(m => m.Kind).ThenBy(m => m.Id)
            .Skip(skip).Take(take ?? DefaultTake).ToListAsync(ct);
        var names = await NamesAsync(catalog, page, ct);
        return Results.Ok(new ResourceMatchListDto([.. page.Select(m => View(db, m, names))], total));
    }

    /// <summary>Перенацелить на другую позицию номенклатуры — действующую, как при сопоставлении.</summary>
    private static async Task<IResult> PointAsync(
        Guid id, ResourceMatchPointRequest body, HttpRequest request, PlanDbContext db, IModuleCatalog catalog,
        IModuleUser user, IModuleActivityLog log, CancellationToken ct)
    {
        if (body.NomenclatureId is not { } nomenclatureId)
            throw new InvalidRequestException("Позиция номенклатуры не названа («nomenclatureId»): перенацелить запомненное некуда.");
        var match = await SeenAsync(db, id, request, ct);
        var before = await NamesAsync(catalog, [match], ct);
        var position = await ResourceMatching.ResolveAsync(catalog, nomenclatureId, ct);

        if (match.NomenclatureId != position.Id)
        {
            var was = Label(match, before);
            match.Point(match.SourceText, position.Id, user.Id, user.Name);
            await db.SaveChangesAsync(ct);
            await log.RecordAsync(EstimateActions.ResourceMatchPointed, match.Id.ToString(), Subject(match),
                before: was, after: position.DisplayName, ct: ct);
        }

        return Results.Ok(View(db, match, new Dictionary<Guid, ModuleCatalogRef> { [position.Id] = position }));
    }

    private static async Task<IResult> ForgetAsync(
        Guid id, HttpRequest request, PlanDbContext db, IModuleCatalog catalog, IModuleActivityLog log, CancellationToken ct)
    {
        var match = await SeenAsync(db, id, request, ct);
        var names = await NamesAsync(catalog, [match], ct);

        db.ResourceMatches.Remove(match);
        await db.SaveChangesAsync(ct);
        await log.RecordAsync(EstimateActions.ResourceMatchForgotten, match.Id.ToString(), Subject(match),
            before: Label(match, names), ct: ct);
        return Results.NoContent();
    }

    /// <summary>Запомненное — той версии, которую человек видел. Без версии — 400, с устаревшей — 409.</summary>
    private static async Task<ResourceMatch> SeenAsync(PlanDbContext db, Guid id, HttpRequest request, CancellationToken ct)
    {
        var match = await db.ResourceMatches.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException("Такого запомненного сопоставления нет — его могли забыть, пока список был открыт.");
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(match), "Запомненное сопоставление", "список запомненного");
        return match;
    }

    /// <summary>Позиции записей — словарём; справочник не назначен — отказ, а не «всё удалено».</summary>
    private static async Task<Dictionary<Guid, ModuleCatalogRef>> NamesAsync(
        IModuleCatalog catalog, IReadOnlyList<ResourceMatch> matches, CancellationToken ct) =>
        matches.Count == 0
            ? []
            : (await catalog.RefsAsync(FunctionalTag.RoleNomenclature, [.. matches.Select(m => m.NomenclatureId).Distinct()], ct)
                ?? throw new ConflictException(EstimateRowReferences.NoNomenclature)).ToDictionary(r => r.Id);

    private static string Subject(ResourceMatch match) =>
        match.Kind == ResourceMatchKind.Code ? $"Ресурс с кодом «{match.SourceText}»" : $"Ресурс «{match.SourceText}»";

    private static string Label(ResourceMatch match, Dictionary<Guid, ModuleCatalogRef> names) =>
        names.GetValueOrDefault(match.NomenclatureId)?.DisplayName ?? "позиция удалена";

    private static ResourceMatchDto View(PlanDbContext db, ResourceMatch match, Dictionary<Guid, ModuleCatalogRef> names)
    {
        var position = names.GetValueOrDefault(match.NomenclatureId);
        var issue = position is null ? IssueLost : position.Archived ? IssueArchived : null;
        return new(match.Id, db.VersionOf(match), match.Kind == ResourceMatchKind.Code ? "code" : "name", match.SourceText,
            match.NomenclatureId, position?.DisplayName, issue, match.UpdatedAt, match.UpdatedByName);
    }
}

public sealed record ResourceMatchPointRequest(Guid? NomenclatureId);

/// <param name="Version">Версия записи — её называет правка заголовком <c>If-Match</c>.</param>
/// <param name="Kind"><c>code</c> — узнаётся по коду ресурса, <c>name</c> — по наименованию.</param>
/// <param name="Issue">Почему запомненное НЕ подставится: <c>lost</c> — позиции нет, <c>archived</c> — в архиве;
/// <c>null</c> — подставится.</param>
public sealed record ResourceMatchDto(
    Guid Id, string Version, string Kind, string SourceText, Guid NomenclatureId, string? NomenclatureName,
    string? Issue, DateTimeOffset UpdatedAt, string? UpdatedBy);

/// <param name="Total">Сколько запомненного под отбором всего: без числа порция читалась бы как весь список.</param>
public sealed record ResourceMatchListDto(IReadOnlyList<ResourceMatchDto> Items, int Total);
