using BHS.CRG.Application.Jobs;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Jobs;

/// <summary>
/// Постановка задач в фон и запрос активных. Создаёт запись Job(Queued) в БД (источник истины) и толкает
/// id в <see cref="JobQueue"/>; выполнение — в <see cref="JobBackgroundService"/> вне HTTP-реквеста.
/// </summary>
public class JobService(AppDbContext db, JobQueue queue) : IJobService
{
    public async Task<Guid> EnqueueAsync(JobKind kind, Guid userId, Guid targetId, string title, string? payload, CancellationToken ct)
    {
        var job = Job.Create(kind, userId, targetId, title, payload);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        queue.Enqueue(job.Id);
        return job.Id;
    }

    public async Task<IReadOnlyList<JobDto>> GetActiveForUserAsync(Guid userId, CancellationToken ct)
    {
        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => j.UserId == userId && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);
        return jobs.Select(Dto).ToList();
    }

    public async Task<JobDto?> GetAsync(Guid jobId, Guid userId, CancellationToken ct)
    {
        // Чужую не отдаём — так же, как CancelAsync: список активных всегда был «мои», и запрос по
        // id не должен оказаться дырой в этом правиле только потому, что id угадывать не принято.
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);
        return job is null ? null : Dto(job);
    }

    private static JobDto Dto(Job j) => new(
        j.Id, j.Kind.ToString(), j.TargetId, j.Status.ToString(), j.Title, j.Progress, j.CreatedAt,
        j.StartedAt, j.FinishedAt, j.Error);

    public Task<bool> HasActiveForTargetAsync(Guid userId, Guid targetId, CancellationToken ct)
        => db.Jobs.AsNoTracking().AnyAsync(j => j.UserId == userId && j.TargetId == targetId
            && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct);

    public Task<bool> HasActiveOfKindAsync(JobKind kind, CancellationToken ct)
        => db.Jobs.AsNoTracking().AnyAsync(j => j.Kind == kind
            && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct);

    public async Task<bool> CancelAsync(Guid jobId, Guid userId, CancellationToken ct)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);
        if (job is null || !job.TryCancel()) return false; // не найдена/чужая или уже стартовала/завершена
        await db.SaveChangesAsync(ct);
        // id остаётся в очереди — фоновый сервис при извлечении увидит Status != Queued и пропустит.
        return true;
    }
}
