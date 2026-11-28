using System.Data;
using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Work.Closing;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Submission;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Devices;

internal static class FieldReportEndpoints
{
    internal const string Path = "/api/field/v1/reports";
    internal const string DevicePolicy = "field-device-own-report";

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/reports")
            .RequireAuthorization(DevicePolicy)
            .RequireRateLimiting("field-exchange");
        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("", CreateAsync);
        group.MapPut("/{id:guid}", ChangeAsync);
        group.MapPost("/{id:guid}/submit", SubmitAsync);
    }

    private static Task<IResult> ListAsync(long? since, int? limit, HttpContext context,
        DeviceSessions sessions, WorkDbContext db, ReportReads reads, ReportViews views, CancellationToken ct) => RunAsync(async () =>
    {
        var viewer = Viewer(context, sessions, writing: false);
        var after = since ?? 0;
        var take = limit ?? ReportEndpoints.DefaultLimit;
        if (after < 0 || take is < 1 or > ReportEndpoints.MaxLimit)
            return Refusal(400, "invalid-request");

        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var feed = await reads.SinceAsync(viewer, after, take, ct);
        var items = await views.ManyAsync(viewer, feed.Items, ct, versions: feed.Versions);
        return Results.Ok(new { items = items.Select(FieldReportBody.Of), feed.Deleted, feed.Cursor, feed.More });
    });

    private static Task<IResult> GetAsync(Guid id, HttpContext context, DeviceSessions sessions,
        ReportHistory history, CancellationToken ct) =>
        RunAsync(async () => Results.Ok(FieldReportBody.Of(
            await history.GetAsync(Viewer(context, sessions, writing: false), id, null, ct))));

    private static Task<IResult> CreateAsync(ReportRequest body, HttpContext context, DeviceSessions sessions,
        ReportDesk desk, ReportReads reads, CancellationToken ct) => RunAsync(async () =>
    {
        var viewer = Viewer(context, sessions, writing: true);
        var me = viewer.EmployeeId!.Value;
        if (body.EmployeeId is { } employee && employee != me) return Refusal(400, "invalid-request");
        try
        {
            return await desk.CreateAsync(Path, viewer, me, body, enteredBy: null, ct);
        }
        catch (ReportKeyTakenException taken)
        {
            var existing = await reads.FindAsync(viewer, taken.ExistingId, ct);
            return Results.Conflict(new
            {
                reason = existing?.EnteredByUserId is null ? "report-key-taken" : "manager-report-conflict",
                existingId = taken.ExistingId,
            });
        }
    });

    private static Task<IResult> ChangeAsync(Guid id, ReportRequest body, HttpContext context, DeviceSessions sessions,
        ReportDesk desk, CancellationToken ct) => RunAsync(() =>
        desk.ChangeAsync(Viewer(context, sessions, writing: true), id, body, context.Request, ct));

    private static Task<IResult> SubmitAsync(Guid id, SubmitRequest body, HttpContext context, DeviceSessions sessions,
        ReportDesk desk, CancellationToken ct) => RunAsync(() =>
        desk.SubmitAsync(Viewer(context, sessions, writing: true), id, body, context.Request, ct));

    private static ReportViewer Viewer(HttpContext context, DeviceSessions sessions, bool writing)
    {
        context.Response.Headers.CacheControl = "no-store";
        var access = context.Features.Get<FieldDeviceAccess>()!;
        if (!writing && access.RevokedOn is not null) throw new FieldReportRefusal(403, "device-revoked");
        if (writing)
        {
            var supplied = context.Request.Headers["X-Field-Version-Code"];
            if (!int.TryParse(supplied, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                throw new FieldReportRefusal(400, "invalid-request");
            if (version < WorkModule.MinimumFieldSenderVersionCode)
                throw new FieldReportRefusal(409, "client-outdated");
        }
        return ReportViewer.ForField(access.EmployeeId, new FieldReportDelivery(sessions, access.DeviceId,
            context.Request.Headers.Authorization.ToString()[7..]));
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> run)
    {
        try { return await run(); }
        catch (FieldReportRefusal e) { return Refusal(e.Status, e.Reason); }
        catch (ReportPeriodClosedException) { return Refusal(409, "period-closed"); }
        catch (ReportVersionConflictException) { return Refusal(409, "version-conflict"); }
        catch (NotFoundException) { return Refusal(404, "report-not-found"); }
        catch (InvalidRequestException) { return Refusal(400, "invalid-request"); }
        catch (ReportStatusException e)
        {
            return Results.Conflict(new { reason = ReportStatusException.ReasonCode, status = Report.StatusCode(e.Status) });
        }
        catch (RequiredMissingException e)
        {
            return Results.UnprocessableEntity(new { reason = "required-missing", requiredMissing = e.Missing });
        }
        catch (ConflictException) { return Refusal(409, "report-conflict"); }
    }

    private static IResult Refusal(int status, string reason) => Results.Json(new { reason }, statusCode: status);
}

internal static class FieldReportBody
{
    // Мобильный договор содержит поля Work, которые редактирует или отображает приложение.
    internal static object Of(ReportDto report) => new
    {
        report.Id, report.ConstructionId, report.ConstructionName, report.Date, report.Shift, report.Hours,
        report.Status, report.Note, report.Seq, report.Version, report.AssignedOnDate, report.EnteredBy,
        CreatedAt = AtDatabasePrecision(report.CreatedAt),
        UpdatedAt = AtDatabasePrecision(report.UpdatedAt),
        report.Replayed, report.AutoSubmitAt, SubmittedAt = AtDatabasePrecision(report.SubmittedAt),
        report.SubmittedBy, report.RequiredMissing, report.AlreadySubmitted, report.ReturnComment, report.PeriodClosed,
        report.HasWebFields,
        Lines = report.Lines.Select(line => new
        {
            line.Id, line.Position, line.WorkTypeId, line.VariantId, line.Volume, line.UnitId, line.SectionId,
            line.Location, line.Comment, line.IsCrew, line.WorkPlanItemId, line.Materials, line.Participants,
            Photos = line.Photos.Select(photo => new { photo.Id, photo.State, photo.OriginalName }),
            line.CrewRole,
        }),
    };

    // PostgreSQL хранит timestamptz с точностью до микросекунды. Ответ на запись совпадает с перечитанным.
    private static DateTimeOffset? AtDatabasePrecision(DateTimeOffset? value) =>
        value is { } timestamp
            ? timestamp.AddTicks(-(timestamp.Ticks % TimeSpan.TicksPerMicrosecond))
            : null;
}
