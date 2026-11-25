using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Closing;
using BHS.CRG.Modules.Work.Crew;
using BHS.CRG.Modules.Work.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Endpoints;

public sealed class ReportCorrection(
    WorkDbContext db, ReportReads reads, ReportWriter writer, ReportViews views, ReportPeriods periods, CrewInputs crew,
    ReportPeople people, ReportJournal journal, ReportActivity activity, TimeProvider time)
{
    public const string Path = ReportEndpoints.Path + "/{id:guid}/correction";

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(Path, PostAsync).WithTags("Отчёты")
            .RequireAuthorization(AppPolicies.Permission(WorkModule.ReportReview));

    private static Task<IResult> PostAsync(
        Guid id, ReportRequest? body, HttpRequest request, IModuleReaders readers, ManagerSites sites,
        ReportCorrection correction, CancellationToken ct) =>
        ReportRefusals.RunAsync(async () =>
            Results.Ok(await correction.ApplyAsync(await EntryReportEndpoints.ViewerAsync(readers, sites, ct),
                id, body, request, ct)));

    private async Task<ReportDto> ApplyAsync(
        ReportViewer viewer, Guid id, ReportRequest? body, HttpRequest request, CancellationToken ct)
    {
        var actor = await people.ActorAsync(viewer, ct);
        Report report;
        string executor;
        string reason;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var boundaries = await periods.UnderWriteLockAsync(ct);
            report = await reads.FindAsync(viewer, id, ct) ?? throw new NotFoundException(ReportDesk.NotFoundText);
            if (!ReportReads.ContentVisible(report, viewer, people.UserId))
                throw new NotFoundException(ReportDesk.NotFoundText);
            if (!ReportPeriods.IsClosed(boundaries, report.ConstructionId, report.Date)
                || await periods.ClosingAsync(report, ct) is not { } closing)
                throw new ConflictException("Исправление не нужно: период открыт, правьте обычным порядком.");

            var seen = SeenVersions.Required(request, "отчёта");
            if (seen != db.VersionOf(report)) throw new ConflictException(ReportDesk.Stale);
            var content = body ?? throw new InvalidRequestException("Укажите причину и содержимое исправленного отчёта.");
            reason = CheckedReason(content.Reason);
            if ((content.Id is { } named && named != id)
                || (content.EmployeeId is { } employee && employee != report.EmployeeId))
                throw new InvalidRequestException("Идентификатор отчёта и сотрудник при исправлении не меняются.");

            var header = content.Header();
            if (report.KeyChanges(header.ConstructionId, header.Date, header.Shift))
                throw new InvalidRequestException("При исправлении закрытого отчёта стройка, дата и смена не меняются.");
            if (content.Lines is null)
                throw new InvalidRequestException("Укажите все строки исправленного отчёта (lines).");
            var lines = content.LineValues();
            var changesCrew = report.Lines.Any(l => l.IsCrew) || lines.Any(l => l.IsCrew);
            if (changesCrew) await crew.LockAsync(report, ct);
            var seq = await db.NextReportSeqAsync(report.EmployeeId, ct);
            // Под замком сотрудника перечитываем отчёт и фото: доставка файла могла пройти без смены xmin отчёта.
            db.ChangeTracker.Clear();
            report = await reads.FindAsync(viewer, id, ct) ?? throw new NotFoundException(ReportDesk.NotFoundText);
            if (seen != db.VersionOf(report)) throw new ConflictException(ReportDesk.Stale);

            executor = await people.ExecutorAsync(report.EmployeeId, ct);
            var now = time.GetUtcNow();
            var change = Guid.NewGuid();
            var before = ReportJournal.Capture(report);
            db.Add(new ReportVersion(report, closing.Id, ReportVersionContent.Capture(report), reason, actor, now));
            // Снятые файлы остаются у снимка, который их содержит. В резервную копию они попадут по этому пути.
            await writer.ChangeAsync(report, header, lines, seq, now, ct, closingId: closing.Id);
            report.AdvanceVersion();
            if (changesCrew) await crew.LinesChangedAsync(report, before, ct);
            journal.Append(ReportJournalKinds.Corrected, report, before, actor, executor, change, seq,
                deviceTime: null, now, reason);
            await db.SaveChangesAsync(ct);
            // Протокол называет xmin уже записанного отчёта. Он фиксируется той же транзакцией.
            db.Add(ReportChange.Of(seq, report, change, ReportChangeKind.Corrected, null, now, db.VersionOf(report)));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(ReportDesk.Stale);
        }

        await activity.RecordAsync(ReportActions.Corrected, report, executor, before: null, after: "исправлен");
        return await views.OneAsync(viewer, report, ct: ct);
    }

    private static string CheckedReason(string? reason) => reason?.Trim() switch
    {
        null or "" => throw new InvalidRequestException("Укажите причину исправления отчёта."),
        { Length: > 1000 } => throw new InvalidRequestException("Причина исправления отчёта должна содержать не более 1000 символов."),
        var text => text,
    };
}
