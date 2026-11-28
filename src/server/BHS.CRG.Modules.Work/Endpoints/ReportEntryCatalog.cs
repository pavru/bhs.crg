using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Work.Endpoints;

internal static class ReportEntryCatalog
{
    private const int Limit = 100;

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/work/entry/sites/{siteId:guid}/catalog/{kind}", GetAsync)
            .WithTags("Отчёты")
            .RequireAuthorization(AppPolicies.AnyPermission(WorkModule.ReportOwn, WorkModule.ReportReview));

    private static Task<IResult> GetAsync(
        Guid siteId, string kind, DateOnly? date, Guid? reportId, string? q, Guid? typeId, bool? union, bool? archived,
        IModuleReaders readers,
        IModuleUser user, IModuleEmployees employees, ManagerSites managers, ReportEntrySites entry,
        ReportReads reports, IModuleConstructions constructions, IModuleWorkTypes types, IModuleUnits units,
        IModuleCatalog catalog, IModuleAcceptanceFields fields, CancellationToken ct) => ReportRefusals.RunAsync(async () =>
    {
        var reader = await readers.ForRequestAsync(ct);
        var reach = reader.Allows(WorkModule.ReportReview)
            ? await managers.ReachAsync(reader, ct) : ModuleReach.OfSites([]);
        var (employee, _) = reader.Allows(WorkModule.ReportOwn)
            ? await WorkAccounts.OfUserAsync(user, employees, ct) : (null, null);
        var managerSite = reach.Covers(siteId, own: false);
        Report? report = null;
        if (reportId is { } id)
        {
            var viewer = ReportViewer.Of(reach, alsoOwn: employee?.Id);
            report = await reports.FindAsync(viewer, id, ct);
            if (report?.ConstructionId != siteId || !ReportReads.ContentVisible(report, viewer, reader.UserId))
                throw new NotFoundException(ReportDesk.NotFoundText);
        }
        else if (!managerSite && (employee is null
            || !(await entry.ForEmployeeAsync(employee, ModuleReach.All, date, ct)).Any(s => s.Id == siteId)))
        {
            throw new NotFoundException(ReportDesk.SiteNotYours);
        }

        var site = (await constructions.ListAsync(ct)).FirstOrDefault(s => s.Id == siteId)
            ?? throw new NotFoundException(ReportDesk.SiteNotYours);
        var lines = report?.Lines.ToArray() ?? [];
        return kind switch
        {
            "sections" => Results.Ok(new { items = site.Sections }),
            "work-types" => await WorkTypesAsync(types, q, lines, ct),
            "units" => await UnitsAsync(units, q, lines, ct),
            "materials" => await MaterialsAsync(catalog, q, lines, ct),
            "field-references" when typeId is { } target =>
                await FieldReferencesAsync(fields, siteId, target, union == true, archived == true, q, ct),
            _ => Results.NotFound(),
        };
    });

    private static async Task<IResult> FieldReferencesAsync(IModuleAcceptanceFields fields,
        Guid siteId, Guid target, bool union, bool archived, string? query, CancellationToken ct)
        => await fields.ChoicesAsync(WorkModule.ModuleCode, WorkRecordTypes.LineDetailsCode,
            siteId, target, union, archived, query, Limit, ct) is { } choices
            ? Results.Ok(choices)
            : throw new NotFoundException("Тип ссылки отсутствует в схеме сведений строки.");

    private static async Task<IResult> WorkTypesAsync(IModuleWorkTypes types, string? q, ReportLine[] lines, CancellationToken ct)
    {
        var found = await types.SearchAsync(q, Limit + 1, ct);
        var ids = lines.Select(l => l.WorkTypeId).Distinct().ToArray();
        var selected = ids.Length == 0 ? [] : await types.GetAsync(ids, ct) ?? [];
        return Choice(found?.Items.Select(t => t with { Variants = t.Variants.Where(v => !v.Archived).ToArray() }), selected,
            found is null ? IModuleWorkTypes.NotSetUp : null);
    }

    private static async Task<IResult> UnitsAsync(IModuleUnits units, string? q, ReportLine[] lines, CancellationToken ct)
    {
        var found = await units.SearchAsync(q, Limit + 1, ct);
        var ids = lines.Select(l => l.UnitId).Distinct().ToArray();
        var selected = ids.Length == 0 ? [] : await units.GetAsync(ids, ct) ?? [];
        return Choice(found?.Items, selected, found is null ? IModuleUnits.NotSetUp : null);
    }

    private static async Task<IResult> MaterialsAsync(IModuleCatalog catalog, string? q, ReportLine[] lines, CancellationToken ct)
    {
        var found = await SearchMaterialsAsync(catalog, q, Limit + 1, ct);
        var ids = lines.SelectMany(l => l.Materials).Select(m => m.NomenclatureId).Distinct().ToArray();
        var selected = ids.Length == 0 ? [] : await catalog.RefsAsync(FunctionalTag.RoleNomenclature, ids, ct) ?? [];
        return Choice(found?.Items, selected, found is null ? "Справочник номенклатуры не заведён." : null);
    }

    internal static Task<ModuleCatalogChoice?> SearchMaterialsAsync(
        IModuleCatalog catalog, string? query, int limit, CancellationToken ct) =>
        catalog.SearchAsync(FunctionalTag.RoleNomenclature, query, limit, ct);

    private static IResult Choice<T>(IEnumerable<T>? items, IReadOnlyList<T> selected, string? reason)
    {
        var list = items?.ToArray() ?? [];
        return Results.Ok(new { items = list.Take(Limit), selected, reason, limit = Limit, more = list.Length > Limit });
    }
}
