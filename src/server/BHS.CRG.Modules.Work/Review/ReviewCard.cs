using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Closing;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Work.Review;

public static class ReviewCard
{
    public const string Path = "/api/work/review/reports/{id:guid}";

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Path, GetAsync).WithTags("Отчёты")
            .RequireAuthorization(AppPolicies.AnyPermission(WorkModule.ReportReview, WorkModule.ReportRead));

    private static Task<IResult> GetAsync(
        Guid id, int? number, IModuleReaders readers, ManagerSites sites, ReportHistory history, ReportPeriods periods,
        ReportPeople people, IModuleConstructions constructions, IModuleWorkTypes workTypes, IModuleUnits units,
        IModuleEmployees employees, CancellationToken ct) => ReportRefusals.RunAsync(async () =>
    {
        var reader = await readers.ForRequestAsync(ct);
        var manager = reader.Allows(WorkModule.ReportReview) ? await sites.ReachAsync(reader, ct) : null;
        var reach = reader.Allows(WorkModule.ReportRead) ? ModuleReach.All : manager!;
        var own = reach.Kind == ModuleReachKind.Sites && reader.Allows(WorkModule.ReportOwn) && reader.UserId is { } account
            ? (await employees.OfAccountAsync(account, ct))?.Id
            : null;
        var card = await history.ReadCardAsync(ReportViewer.Of(reach, alsoOwn: own), id, number, ct);
        var report = card.Header;
        var executor = await people.ExecutorAsync(report.EmployeeId, ct);
        var site = (await constructions.ListAsync(ct)).FirstOrDefault(c => c.Id == report.ConstructionId);
        var label = site?.Name ?? "Стройка недоступна";
        if (card.Content is null)
            return Results.Ok(new DraftPresence(report.Id, executor, label, site?.TimeZoneId, report.WindowEndsAt));

        var lines = card.Content.Lines;
        var types = lines.Count == 0 ? [] : await workTypes.GetAsync([.. lines.Select(l => l.WorkTypeId).Distinct()], ct) ?? [];
        var measures = lines.Count == 0 ? [] : await units.GetAsync([.. lines.Select(l => l.UnitId).Distinct()], ct) ?? [];
        var actions = report.Status == ReportStatus.Submitted && card.ViewedVersion == report.CurrentVersion
            && manager?.Covers(report.ConstructionId, own: false) == true
            && !ReportPeriods.IsClosed(await periods.CurrentAsync(ct), report.ConstructionId, report.Date)
                ? ReviewQueue.Decisions : [];
        return Results.Ok(new
        {
            id, status = card.Content.Status, employeeName = executor, constructionName = label,
            timeZoneId = site?.TimeZoneId, card.ViewedVersion, card.AtClosingVersion, actions,
            report = card.Content with { AcceptWouldMark = actions.Count > 0 && card.Content.AcceptWouldMark },
            workTypes = types.ToDictionary(t => t.Id, t => t.DisplayName),
            workVariants = types.SelectMany(t => t.Variants).ToDictionary(v => v.Id, v => v.Name),
            units = measures.ToDictionary(u => u.Id, u => u.Name),
            sections = (site?.Sections ?? []).ToDictionary(s => s.Id, s => s.Name),
        });
    });

    private sealed record DraftPresence(
        Guid Id, string EmployeeName, string ConstructionName, string? TimeZoneId, DateTimeOffset? AutoSubmitAt)
    {
        public string Status => "draft";

        public IReadOnlyList<string> Actions => [];
    }
}
