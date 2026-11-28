using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Crew;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Devices;

internal sealed class FieldReferences(WorkDbContext db, IModuleConstructions constructions, IModuleEmployees employees,
    IModuleWorkTypes workTypes, IModuleUnits units, IModulePlanItems positions, IModuleCatalog catalog,
    CrewCandidates crew, FieldReferencePages pages, FieldMaterialHints materialHints)
{
    internal const string Path = "/references/{kind}";

    internal static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Path, GetAsync).RequireAuthorization(FieldReportEndpoints.DevicePolicy)
            .RequireRateLimiting("field-exchange");

    private static async Task<IResult> GetAsync(string kind, DateOnly? date, Guid? siteId, string? cursor, int? limit,
        HttpContext context, FieldReferences references, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var device = context.Features.Get<FieldDeviceAccess>()!;
        if (device.RevokedOn is not null) return FieldReportEndpoints.Refusal(403, "device-revoked");
        var take = limit ?? FieldReferencePages.DefaultLimit;
        var scoped = kind is "participants" or "issued-materials" or "estimated-materials";
        var dated = scoped || kind is "sites" or "sections" or "positions";
        if (cursor is { Length: > 2048 } || take is < 1 or > FieldReferencePages.MaxLimit ||
            dated != date.HasValue || scoped != siteId.HasValue)
            return FieldReportEndpoints.Refusal(400, "invalid-request");

        return await references.ReadAsync(device, kind, date, siteId, cursor, take, ct);
    }

    private async Task<IResult> ReadAsync(FieldDeviceAccess device, string kind, DateOnly? date,
        Guid? siteId, string? cursor, int limit, CancellationToken ct)
    {
        var scope = FormattableString.Invariant($"{device.DeviceId:D}/{device.EmployeeId:D}/{kind}/{date:yyyy-MM-dd}/{siteId:D}");
        switch (kind)
        {
            case "classifier":
                var classifier = await workTypes.ListAsync(ct);
                return Page(classifier?.Select(type => type with { Variants = type.Variants.OrderBy(v => v.Id).ToArray() }),
                    type => type.Id);
            case "units":
                return Page((await units.SearchAsync(null, int.MaxValue, ct))?.Items, unit => unit.Id);
            case "materials":
                var materials = await ReportEntryCatalog.SearchMaterialsAsync(catalog, null, int.MaxValue, ct);
                return Page(materials?.Items.Select(m => new { m.Id, m.DisplayName }), m => m.Id);
            case "sites":
                var sites = await AssignedSitesAsync(device.EmployeeId, date!.Value, ct);
                var companyTimeZoneId = await constructions.CompanyTimeZoneIdAsync(ct);
                var addresses = await constructions.AddressesAsync([.. sites.Select(s => s.Id)], ct);
                return Page(sites.Select(s => new { s.Id, s.Name, s.TimeZoneId, Address = addresses.GetValueOrDefault(s.Id) }),
                    s => s.Id, new { companyTimeZoneId });
            case "sections":
                var parents = await AssignedSitesAsync(device.EmployeeId, date!.Value, ct);
                return Page(parents.SelectMany(s => s.Sections.Select(section =>
                    new { ConstructionId = s.Id, section.Id, section.Name })), s => s.Id);
            case "positions":
                var allowed = await AssignedSitesAsync(device.EmployeeId, date!.Value, ct);
                var all = await positions.ForSitesAsync([.. allowed.Select(s => s.Id)], ct);
                return Page(all.Select(p => new { p.Id, p.Key.ConstructionId, p.Key.SectionId, p.Key.WorkTypeId, p.Key.UnitId }),
                    p => p.Id);
            case "participants":
                var eligible = await AssignedSitesAsync(device.EmployeeId, date!.Value, ct);
                return eligible.Any(s => s.Id == siteId)
                    ? Page(await crew.ListAsync(siteId!.Value, date.Value, ct), p => p.Id)
                    : Results.Ok(pages.Page<CrewCandidates.Candidate>(scope, cursor, limit, "not-assigned", []));
            case "issued-materials" or "estimated-materials":
                var assigned = await AssignedSitesAsync(device.EmployeeId, date!.Value, ct);
                var site = assigned.Any(s => s.Id == siteId)
                    ? new AssignedFieldSite(device.DeviceId, device.EmployeeId, siteId!.Value, date.Value)
                    : null;
                return Results.Ok(await materialHints.ReadAsync(kind, site, scope, cursor, limit, ct));
            default:
                return FieldReportEndpoints.Refusal(404, "unknown-reference");
        }

        IResult Page<T>(IEnumerable<T>? items, Func<T, Guid> key, object? metadata = null) =>
            Results.Ok(pages.Page(scope, cursor, limit, items is null ? "not-configured" : "ok",
                items?.OrderBy(key).ToArray() ?? [], metadata));
    }

    private async Task<IReadOnlyList<ModuleConstruction>> AssignedSitesAsync(Guid employeeId, DateOnly date, CancellationToken ct)
    {
        var employee = (await employees.GetAsync([employeeId], ct))?.SingleOrDefault();
        if (employee is null) return [];
        var allowed = await AssignmentGate.EntryAssignments(db, employee, date)
            .Select(a => a.ConstructionId).Distinct().ToListAsync(ct);
        if (allowed.Count == 0) return [];
        var ids = allowed.ToHashSet();
        return (await constructions.ListAsync(ct)).Where(s => ids.Contains(s.Id)).ToArray();
    }
}
