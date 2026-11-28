using BHS.CRG.Domain.Common;
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
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Modules.Work.Devices;

internal sealed class FieldReferences(WorkDbContext db, IModuleConstructions constructions, IModuleEmployees employees,
    IModuleWorkTypes workTypes, IModuleUnits units, IModulePlanItems positions, IModuleCatalog catalog,
    CrewCandidates crew, FieldReferencePages pages, FieldMaterialHints materialHints, IFieldReferenceRevisions revisions)
{
    internal const string Path = "/references/{kind}";

    internal static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Path, GetAsync).RequireAuthorization(FieldReportEndpoints.DevicePolicy)
            .RequireRateLimiting("field-exchange");

    private static async Task<IResult> GetAsync(string kind, DateOnly? date, Guid? siteId, string? cursor, int? limit,
        HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var device = context.Features.Get<FieldDeviceAccess>()!;
        if (device.RevokedOn is not null) return FieldReportEndpoints.Refusal(403, "device-revoked");
        var take = limit ?? FieldReferencePages.DefaultLimit;
        var scoped = kind is "participants" or "issued-materials" or "estimated-materials";
        var dated = scoped || kind is "sites" or "sections" or "positions";
        if (!dated && kind is not ("classifier" or "units" or "materials"))
            return FieldReportEndpoints.Refusal(404, "unknown-reference");
        if (cursor is { Length: > 2048 } || take is < 1 or > FieldReferencePages.MaxLimit ||
            dated != date.HasValue || scoped != siteId.HasValue)
            return FieldReportEndpoints.Refusal(400, "invalid-request");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            // При повторе зависимые порты читают свежие данные, включая отслеживаемые EF типы и настройки.
            await using var scope = context.RequestServices.CreateAsyncScope();
            var references = scope.ServiceProvider.GetRequiredService<FieldReferences>();
            var page = await references.ReadAsync(device, kind, date, siteId, cursor, take, ct);
            if (page is not null) return Results.Ok(page);
        }
        return FieldReportEndpoints.Refusal(503, "reference-changing");
    }

    private async Task<object?> ReadAsync(FieldDeviceAccess device, string kind, DateOnly? date,
        Guid? siteId, string? cursor, int limit, CancellationToken ct)
    {
        var scope = FormattableString.Invariant($"{device.DeviceId:D}/{device.EmployeeId:D}/{kind}/{date:yyyy-MM-dd}/{siteId:D}");
        var revision = await ReadRevisionAsync(kind, ct);
        // Доступ подтверждается до обращения к кэшу. Ключ снимка содержит только подтверждённые стройки.
        var allowed = date is { } day ? await AssignedSiteIdsAsync(device.EmployeeId, day, siteId, ct) : [];
        var snapshotKey = FormattableString.Invariant($"{kind}/{date:yyyy-MM-dd}/{siteId:D}/{string.Join(',', allowed)}");
        return await pages.ReadAsync(snapshotKey, revision, scope, cursor, limit,
            () => LoadAsync(device, kind, date, siteId, allowed, ct), () => ReadRevisionAsync(kind, ct), ct);
    }

    private async Task<string> ReadRevisionAsync(string kind, CancellationToken ct)
    {
        var core = await revisions.ReadAsync(kind, ct);
        return kind switch
        {
            "participants" => core + "|" + await db.Database.SqlQuery<string>($"""
                SELECT COALESCE(string_agg(writer || ':' || revision, ',' ORDER BY writer), '') AS "Value"
                FROM work.field_reference_revisions
                """).SingleAsync(ct),
            "issued-materials" or "estimated-materials" => core + "|" + await materialHints.ReadRevisionAsync(kind, ct),
            _ => core,
        };
    }

    private async Task<FieldReferenceSnapshot> LoadAsync(FieldDeviceAccess device, string kind, DateOnly? date,
        Guid? siteId, Guid[] allowed, CancellationToken ct)
    {
        switch (kind)
        {
            case "classifier":
                var classifier = await workTypes.ListAsync(ct);
                return FieldReferenceSnapshot.Of(classifier?.Select(type => type with { Variants = type.Variants.OrderBy(v => v.Id).ToArray() }),
                    type => type.Id);
            case "units":
                return FieldReferenceSnapshot.Of((await units.SearchAsync(null, int.MaxValue, ct))?.Items, unit => unit.Id);
            case "materials":
                var materials = await ReportEntryCatalog.SearchMaterialsAsync(catalog, null, int.MaxValue, ct);
                return FieldReferenceSnapshot.Of(materials?.Items.Select(m => new { m.Id, m.DisplayName }), m => m.Id);
            case "sites":
                var sites = await LoadSitesAsync(allowed, ct);
                var companyTimeZoneId = await constructions.CompanyTimeZoneIdAsync(ct);
                var addresses = await constructions.AddressesAsync(allowed, ct);
                return FieldReferenceSnapshot.Of(sites.Select(s => new { s.Id, s.Name, s.TimeZoneId, Address = addresses.GetValueOrDefault(s.Id) }),
                    s => s.Id, new { companyTimeZoneId });
            case "sections":
                var parents = await LoadSitesAsync(allowed, ct);
                return FieldReferenceSnapshot.Of(parents.SelectMany(s => s.Sections.Select(section =>
                    new { ConstructionId = s.Id, section.Id, section.Name })), s => s.Id);
            case "positions":
                var all = await positions.ForSitesAsync(allowed, ct);
                return FieldReferenceSnapshot.Of(all.Select(p => new { p.Id, p.Key.ConstructionId, p.Key.SectionId, p.Key.WorkTypeId, p.Key.UnitId }),
                    p => p.Id);
            case "participants":
                return allowed.Length > 0
                    ? FieldReferenceSnapshot.Of(await crew.ListAsync(siteId!.Value, date!.Value, ct), p => p.Id)
                    : new("not-assigned", []);
            case "issued-materials" or "estimated-materials":
                var site = allowed.Length > 0
                    ? new AssignedFieldSite(device.DeviceId, device.EmployeeId, siteId!.Value, date!.Value)
                    : null;
                return await materialHints.ReadAsync(kind, site, ct);
            default:
                throw new NotFoundException("Неизвестный полевой справочник.");
        }
    }

    private async Task<Guid[]> AssignedSiteIdsAsync(Guid employeeId, DateOnly date, Guid? siteId, CancellationToken ct)
    {
        var employee = (await employees.GetAsync([employeeId], ct))?.SingleOrDefault();
        if (employee is null) return [];
        var allowed = await AssignmentGate.EntryAssignments(db, employee, date)
            .Where(a => siteId == null || a.ConstructionId == siteId)
            .Select(a => a.ConstructionId).Distinct().ToArrayAsync(ct);
        if (allowed.Length == 0) return [];
        var existing = await constructions.ExistingIdsAsync(allowed, ct);
        Array.Sort(existing);
        return existing;
    }

    private async Task<ModuleConstruction[]> LoadSitesAsync(Guid[] allowed, CancellationToken ct)
    {
        if (allowed.Length == 0) return [];
        var ids = allowed.ToHashSet();
        return (await constructions.ListAsync(ct)).Where(s => ids.Contains(s.Id)).ToArray();
    }
}
