using System.Data;
using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Endpoints;

public sealed record ReportVersionAuthorDto(Guid? Id, string Name);

public sealed record ReportVersionDto(int Number, string Reason, ReportVersionAuthorDto Author, DateTimeOffset At);

internal sealed record ArchivedPhoto(Guid Id, string? Path, string? PreviewPath, DateTimeOffset? StoredAt, Guid? StoredInClosingId);

internal sealed record ReportVersionContent(
    decimal? Hours, string? Note, IReadOnlyList<ReportLineDto> Lines, IReadOnlyList<ArchivedPhoto> Photos,
    DateTimeOffset UpdatedAt)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string Capture(Report report) => JsonSerializer.Serialize(new ReportVersionContent(
        report.Hours, report.Note, ReportViews.LinesOf(report),
        [.. report.Lines.SelectMany(l => l.Photos)
            .Select(p => new ArchivedPhoto(p.Id, p.BlobPath, p.PreviewBlobPath, p.StoredAt, p.StoredInClosingId))],
        report.UpdatedAt), Json);

    internal static ReportVersionContent Read(string json) => JsonSerializer.Deserialize<ReportVersionContent>(json, Json)!;
}

public sealed class ReportHistory(
    WorkDbContext db, ReportReads reads, ReportViews views, IModuleUser user, IModulePeriods periods)
{
    public const string Corrected = "corrected";
    public const string AtClosing = "at-closing";

    internal async Task<ReviewCardRead> ReadCardAsync(ReportViewer viewer, Guid id, int? number, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var header = await reads.ReviewHeaderAsync(viewer, id, ct) ?? throw new NotFoundException(ReportDesk.NotFoundText);
        if (header.Status == ReportStatus.Draft)
        {
            if (number is not null) throw new NotFoundException(ReportDesk.NotFoundText);
            return new(header, null, null, null);
        }

        var report = await reads.FindAsync(viewer, id, ct) ?? throw new NotFoundException(ReportDesk.NotFoundText);
        var dto = await views.OneAsync(viewer, report, ct: ct);
        var closing = dto.Versions.Count == 0 ? null : await periods.ClosingAsync(report.ConstructionId, report.Date, ct);
        var atClosing = closing is { } period
            ? await reads.VersionAtClosingAsync(viewer, user.Id, id, dto.CurrentVersion, period.Id, ct)
            : null;
        if (number is null || number == dto.CurrentVersion) return new(report, dto, dto.CurrentVersion, atClosing?.Number);
        if (number <= 0 || number > dto.CurrentVersion) throw new NotFoundException(ReportDesk.NotFoundText);
        var selected = atClosing?.Number == number ? atClosing
            : await reads.VersionAsync(viewer, user.Id, id, number.Value, ct);
        if (selected is null) throw new NotFoundException(ReportDesk.NotFoundText);

        var content = ReportVersionContent.Read(selected.Content);
        var late = selected.Number == atClosing?.Number && closing is { } boundary
            ? content.Photos.Where(p => p.StoredInClosingId is { } recorded
                ? recorded == boundary.Id : p.StoredAt > boundary.At).Select(p => p.Id).ToHashSet()
            : [];
        var archived = await views.WithAcceptanceAsync(WithPastContent(dto, content, late, selected.Number,
            selected.Number == atClosing?.Number ? AtClosing : "archived"), ct);
        return new(report, archived, selected.Number, atClosing?.Number);
    }

    public async Task<ReportDto> GetAsync(ReportViewer viewer, Guid id, string? view, CancellationToken ct)
    {
        var historical = view == AtClosing;
        await using var tx = await db.Database.BeginTransactionAsync(
            historical ? IsolationLevel.ReadCommitted : IsolationLevel.RepeatableRead, ct);
        if (historical)
        {
            // Порт читает своим соединением. Границы и содержимое неподвижны после обоих замков.
            await OpenPeriodWrite.HoldAsync(db, ct);
            var employee = await reads.VisibleEmployeeAsync(viewer, id, ct) ?? throw new NotFoundException(ReportDesk.NotFoundText);
            await db.LockReportsAsync(employee, ct);
        }
        var report = await reads.FindAsync(viewer, id, ct) ?? throw new NotFoundException(ReportDesk.NotFoundText);
        if (view is not (null or Corrected or AtClosing))
            throw new InvalidRequestException("Параметр view — corrected или at-closing.");

        var dto = await views.OneAsync(viewer, report, ct: ct);
        if (!historical || !ReportReads.ContentVisible(report, viewer, user.Id)
            || await periods.ClosingAsync(report.ConstructionId, report.Date, ct) is not { } closing)
            return dto;

        var version = dto.Versions.Count > 0
            ? await reads.VersionAtClosingAsync(viewer, user.Id, id, report.CurrentVersion, closing.Id, ct)
            : null;
        var content = version is null ? null : ReportVersionContent.Read(version.Content);
        var late = (content?.Photos.Select(p => (p.Id, p.StoredAt, p.StoredInClosingId))
                    ?? report.Lines.SelectMany(l => l.Photos).Select(p => (p.Id, p.StoredAt, p.StoredInClosingId)))
            .Where(p => p.StoredInClosingId is { } recorded ? recorded == closing.Id : p.StoredAt > closing.At)
            .Select(p => p.Id).ToHashSet();
        if (content is null && late.Count == 0) return dto;

        return await views.WithAcceptanceAsync(WithPastContent(dto, content, late, version?.Number, AtClosing), ct);
    }

    private static ReportDto WithPastContent(
        ReportDto dto, ReportVersionContent? content, IReadOnlySet<Guid> late, int? number, string view)
    {
        var lines = (content?.Lines ?? dto.Lines).Select(line => line with
        {
            Photos = [.. line.Photos.Select(PhotoAtClosing)],
        }).ToList();
        var pending = lines.Sum(line => line.Photos.Count(p => p.State == "pending"));
        var markCodes = dto.Marks.Where(m => m.Code != ReviewMarks.PhotosPending).Select(m => m.Code).ToList();
        if (pending > 0) markCodes.Add(ReviewMarks.PhotosPending);
        return dto with
        {
            View = view, Hours = content is null ? dto.Hours : content.Hours, Note = content is null ? dto.Note : content.Note,
            Lines = lines, UpdatedAt = content?.UpdatedAt ?? dto.UpdatedAt,
            PhotosPending = pending, Marks = ReviewMarks.Dtos(markCodes, pending),
        };

        ReportPhotoDto PhotoAtClosing(ReportPhotoDto photo)
        {
            photo = photo with { CanDeliver = null };
            if (late.Contains(photo.Id)) return photo with { State = "pending" };
            return number is not null && photo.State == "stored"
                ? photo with { VersionUrl = $"/api/work/photos/{photo.Id}?reportId={dto.Id}&number={number}" }
                : photo;
        }
    }
}

internal sealed record ReviewCardRead(Report Header, ReportDto? Content, int? ViewedVersion, int? AtClosingVersion);
