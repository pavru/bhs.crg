using System.Data;
using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Endpoints;

public sealed record ReportVersionAuthorDto(Guid? Id, string Name);

public sealed record ReportVersionDto(int Number, string Reason, ReportVersionAuthorDto Author, DateTimeOffset At);

internal sealed record ArchivedPhoto(Guid Id, string? Path, string? PreviewPath, DateTimeOffset? StoredAt);

internal sealed record ReportVersionContent(
    decimal? Hours, string? Note, IReadOnlyList<ReportLineDto> Lines, IReadOnlyList<ArchivedPhoto> Photos,
    DateTimeOffset UpdatedAt)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string Capture(Report report) => JsonSerializer.Serialize(new ReportVersionContent(
        report.Hours, report.Note, ReportViews.LinesOf(report),
        [.. report.Lines.SelectMany(l => l.Photos).Select(p => new ArchivedPhoto(p.Id, p.BlobPath, p.PreviewBlobPath, p.StoredAt))],
        report.UpdatedAt), Json);

    internal static ReportVersionContent Read(string json) => JsonSerializer.Deserialize<ReportVersionContent>(json, Json)!;
}

public sealed class ReportHistory(
    WorkDbContext db, ReportReads reads, ReportViews views, IModuleUser user, IModulePeriods periods)
{
    public const string Corrected = "corrected";
    public const string AtClosing = "at-closing";

    public async Task<ReportDto> GetAsync(ReportViewer viewer, Guid id, string? view, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var report = await reads.FindAsync(viewer, id, ct) ?? throw new NotFoundException(ReportDesk.NotFoundText);
        if (view is not (null or Corrected or AtClosing))
            throw new InvalidRequestException("Параметр view — corrected или at-closing.");

        var dto = await views.OneAsync(viewer, report, ct: ct);
        if (view != AtClosing || !ReportReads.ContentVisible(report, viewer, user.Id)
            || await periods.ClosingAsync(report.ConstructionId, report.Date, ct) is not { } closing)
            return dto;

        var version = dto.Versions.Count > 0
            ? await reads.VersionAtClosingAsync(viewer, user.Id, id, report.CurrentVersion, closing.Id, ct)
            : null;
        var content = version is null ? null : ReportVersionContent.Read(version.Content);
        var late = (content?.Photos.Select(p => (p.Id, p.StoredAt))
                    ?? report.Lines.SelectMany(l => l.Photos).Select(p => (p.Id, p.StoredAt)))
            .Where(p => p.StoredAt > closing.At).Select(p => p.Id).ToHashSet();
        if (content is null && late.Count == 0) return dto;

        var lines = (content?.Lines ?? dto.Lines).Select(line => line with
        {
            Photos = [.. line.Photos.Select(PhotoAtClosing)],
        }).ToList();
        var pending = lines.Sum(line => line.Photos.Count(p => p.State == "pending"));
        var markCodes = dto.Marks.Where(m => m.Code != ReviewMarks.PhotosPending).Select(m => m.Code).ToList();
        if (pending > 0) markCodes.Add(ReviewMarks.PhotosPending);
        return dto with
        {
            View = AtClosing, Hours = content is null ? dto.Hours : content.Hours, Note = content is null ? dto.Note : content.Note,
            Lines = lines, UpdatedAt = content?.UpdatedAt ?? dto.UpdatedAt,
            PhotosPending = pending, Marks = ReviewMarks.Dtos(markCodes, pending),
        };

        ReportPhotoDto PhotoAtClosing(ReportPhotoDto photo)
        {
            if (late.Contains(photo.Id)) return photo with { State = "pending" };
            return version is not null && photo.State == "stored"
                ? photo with { VersionUrl = $"/api/work/photos/{photo.Id}?reportId={id}&number={version.Number}" }
                : photo;
        }
    }
}
