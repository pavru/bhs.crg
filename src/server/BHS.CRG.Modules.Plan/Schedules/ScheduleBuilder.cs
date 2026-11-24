using System.Data;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Schedules;

internal sealed class ScheduleBuilder(
    PlanDbContext db, IModuleConstructions constructions, IModulePlanItems items, ScheduleViews views)
{
    internal async Task<(ScheduleDto View, bool Created)> CreateAsync(ScheduleCreate request, CancellationToken ct)
    {
        if (request.ConstructionId == Guid.Empty || request.CreationKey == Guid.Empty)
            throw new InvalidRequestException("Назовите стройку и ключ создания графика (creationKey).");
        var name = Schedule.ValidateName(request.Name);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.LockScheduleSiteAsync(request.ConstructionId, ct);
        var existing = await db.Schedules.SingleOrDefaultAsync(
            s => s.ConstructionId == request.ConstructionId && s.CreationKey == request.CreationKey, ct);
        if (existing is not null)
        {
            if (existing.Name != name || existing.RevisionId != request.RevisionId)
                throw new ConflictException("Этот ключ создания уже использован для другого графика.");
            if (existing.RevisionId is { } existingRevision) await db.LockRevisionAsync(existingRevision, ct);
            return (await views.ReadAsync(existing, await SourceAsync(existing, ct), ct), false);
        }

        var schedule = Schedule.Create(request.ConstructionId, request.RevisionId, request.CreationKey, name, DateTimeOffset.UtcNow);
        ScheduleSource? source = null;
        if (schedule.RevisionId is { } revisionId)
        {
            await db.LockRevisionAsync(revisionId, ct);
            var revision = await db.EstimateRevisions.SingleOrDefaultAsync(r => r.Id == revisionId, ct)
                ?? throw new NotFoundException("Редакция сметы не найдена.");
            source = await SourceAsync(schedule, ct);
            await AddPartsAsync(schedule.Id, source!.Parts, ct);
            schedule.Rebuilt(source.Fingerprint, source.Unmapped);
            revision.Freeze(new EstimateFreeze("schedule", $"график «{schedule.Name}»"), DateTimeOffset.UtcNow);
        }
        else if (!(await constructions.ListAsync(ct)).Any(c => c.Id == schedule.ConstructionId))
            throw new NotFoundException("Стройка не найдена.");

        db.Schedules.Add(schedule);
        await db.SaveChangesAsync(ct);
        var view = await views.ReadAsync(schedule, source, ct);
        await transaction.CommitAsync(ct);
        return (view, true);
    }

    internal async Task<ScheduleDto> ReadAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var schedule = await views.LoadAsync(id, ct);
        return await views.ReadAsync(schedule, await SourceAsync(schedule, ct), ct);
    }

    internal async Task<SchedulePreview> PreviewAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        return (await PrepareAsync(await views.LoadAsync(id, ct), ct)).Preview;
    }

    internal async Task<(ScheduleDto? View, SchedulePreview Preview)> RebuildAsync(
        Guid id, string? fingerprint, HttpRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var schedule = await LockAsync(id, ct);
        var version = db.VersionOf(schedule);
        EstimateEndpoints.EnsureSeen(request, version, "График", "график");
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new InvalidRequestException("Сначала запросите предпросмотр и передайте его «fingerprint».");
        if (schedule.RevisionId is { } revision) await db.LockRevisionAsync(revision, ct);
        var prepared = await PrepareAsync(schedule, ct);
        if (prepared.Preview.Fingerprint != fingerprint) return (null, prepared.Preview);

        await AddPartsAsync(id, prepared.Added, ct);
        db.ScheduleWorks.RemoveRange(prepared.Removed);
        foreach (var (work, part) in prepared.Changed) work.Rebuild(part.Volume);
        schedule.Rebuilt(prepared.Source.Fingerprint, prepared.Source.Unmapped);
        await db.SaveChangesAsync(ct);
        var view = await views.ReadAsync(schedule, prepared.Source, ct);
        await transaction.CommitAsync(ct);
        return (view, prepared.Preview);
    }

    internal async Task<Schedule> LockAsync(Guid id, CancellationToken ct)
    {
        var construction = await db.Schedules.Where(s => s.Id == id).Select(s => (Guid?)s.ConstructionId)
            .SingleOrDefaultAsync(ct) ?? throw new NotFoundException("График не найден.");
        await db.LockScheduleSiteAsync(construction, ct);
        return await views.LoadAsync(id, ct);
    }

    internal async Task<ScheduleSource?> SourceAsync(Schedule schedule, CancellationToken ct)
    {
        if (schedule.RevisionId is not { } revision) return null;
        var estimate = await (from r in db.EstimateRevisions
            join e in db.Estimates on r.EstimateId equals e.Id
            where r.Id == revision
            select new { e.ConstructionId, e.SectionId }).SingleAsync(ct);
        if (estimate.ConstructionId != schedule.ConstructionId)
            throw new InvalidRequestException("Редакция сметы относится к другой стройке.");
        return await ScheduleSource.ReadAsync(db, revision, estimate.ConstructionId, estimate.SectionId, ct);
    }

    private async Task AddPartsAsync(Guid id, IReadOnlyList<SchedulePart> parts, CancellationToken ct)
    {
        if (parts.Count == 0) return;
        var keys = parts.Select(part => part.Item).Distinct().ToArray();
        var batch = await items.FindOrCreateManyAsync(keys, ct);
        if (batch.Refusals.Count > 0)
            throw new InvalidRequestException(string.Join("\n", batch.Refusals.Select(r => r.Reason).Distinct()));
        db.ScheduleWorks.AddRange(parts.Select(p =>
            ScheduleWork.Create(id, batch.Items[p.Item].Id, p.RowId, p.Item.SectionId, p.Name, p.Volume)));
    }

    private sealed record Prepared(ScheduleSource Source, SchedulePreview Preview, List<SchedulePart> Added,
        List<ScheduleWork> Removed, List<(ScheduleWork Work, SchedulePart Part)> Changed);

    private async Task<Prepared> PrepareAsync(Schedule schedule, CancellationToken ct)
    {
        var source = await SourceAsync(schedule, ct)
            ?? throw new InvalidRequestException("Для ручного графика нет исходной редакции и распределения.");
        var works = await views.WorksAsync(schedule.Id, ct);
        var existing = works.Where(w => w.SourceRowId.HasValue).ToDictionary(w => (w.SourceRowId!.Value, w.SectionId));
        var added = new List<SchedulePart>();
        var changed = new List<(ScheduleWork Work, SchedulePart Part)>();
        foreach (var part in source.Parts)
        {
            if (!existing.Remove(part.Key, out var work)) added.Add(part);
            else if (work.Volume != part.Volume) changed.Add((work, part));
        }

        var removed = existing.Values.ToList();
        var version = db.VersionOf(schedule);
        var fingerprint = ScheduleSource.Hash($"{schedule.Id:N}:{version}:{source.Fingerprint}");
        var preview = new SchedulePreview(version, fingerprint,
            added.Select(p => new ScheduleDifference(null, p.RowId, p.Item.SectionId, p.Name, p.Volume)).ToList(),
            removed.Select(w => new ScheduleDifference(w.Id, w.SourceRowId!.Value, w.SectionId, w.Name, w.Volume)).ToList(),
            changed.Select(c => new ScheduleVolumeChange(c.Work.Id, c.Part.RowId, c.Part.Item.SectionId,
                c.Part.Name, c.Work.Volume, c.Part.Volume)).ToList(),
            new ScheduleSkipped(source.Unmapped));
        return new Prepared(source, preview, added, removed, changed);
    }
}
