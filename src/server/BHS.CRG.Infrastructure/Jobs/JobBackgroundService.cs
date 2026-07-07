using System.Text.Json;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Jobs;

/// <summary>
/// Разбирает in-process очередь <see cref="JobQueue"/> и выполняет фоновые задачи вне HTTP-реквеста
/// (по одной за раз — распознавание и так упирается в vision, параллелизм не нужен). Каждая задача — в
/// своём DI-scope. Состояние задачи (Running/Progress/Succeeded/Failed) пишется через ОТДЕЛЬНЫЙ контекст,
/// не тот, что у распознавания, — иначе SaveChanges распознавания затирал бы прогресс (разные транзакции).
/// Итог операции по-прежнему уходит в уведомления из самих методов распознавания (handoff к колокольчику).
/// </summary>
public class JobBackgroundService(
    JobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<JobBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            try { await ProcessAsync(jobId, stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Необработанная ошибка выполнения задачи {JobId}", jobId); }
        }
    }

    private async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        JobKind kind;
        Guid targetId, userId;
        string? payload;
        string title;
        // Читаем и помечаем Running в собственном контексте.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null || job.Status != JobStatus.Queued) return; // уже обработана/потеряна
            kind = job.Kind; targetId = job.TargetId; payload = job.Payload; userId = job.UserId; title = job.Title;
            job.Start();
            await db.SaveChangesAsync(ct);
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var pdfRecognition = scope.ServiceProvider.GetRequiredService<DataSetPdfRecognitionService>();

            var lastProgress = DateTimeOffset.MinValue;
            Func<int, int, Task> reportPages = async (cur, total) =>
            {
                var now = DateTimeOffset.UtcNow;
                if (cur != total && now - lastProgress < TimeSpan.FromSeconds(1.5)) return;
                lastProgress = now;
                await UpdateJobAsync(jobId, j => j.ReportProgress($"{cur} из {total} листов"), ct);
            };

            switch (kind)
            {
                case JobKind.RecognizeGostSet:
                    await pdfRecognition.RecognizePdfSourceAsync(targetId, confirm: true, ct, reportPages);
                    break;

                case JobKind.RecognizeDocument:
                    await pdfRecognition.RecognizeDocumentAsync(targetId, ParseFirstPageIndex(payload), ct, reportPages);
                    break;

                case JobKind.RecognizeTable:
                    await pdfRecognition.RecognizeDocumentTableAsync(targetId, ParseFirstPageIndex(payload), ct);
                    break;

                default:
                    throw new InvalidOperationException($"Неизвестный вид задачи: {kind}");
            }

            await UpdateJobAsync(jobId, j => j.Succeed(), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Фоновая задача {JobId} ({Kind}) завершилась ошибкой", jobId, kind);
            await UpdateJobAsync(jobId, j => j.Fail(ex.Message), CancellationToken.None);
            // Единая точка публикации ошибки задачи в колокольчик (handoff: задача ушла из индикатора → всплыла ошибкой).
            await PublishFailureAsync(userId, title, ex.Message);
        }
    }

    private async Task PublishFailureAsync(Guid userId, string title, string error)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
            await notifications.PublishAsync(NotificationSeverity.Error, $"Ошибка: {title}", error, "Фоновые задачи", userId: userId);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось опубликовать уведомление об ошибке задачи"); }
    }

    private async Task UpdateJobAsync(Guid jobId, Action<Job> mutate, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;
        mutate(job);
        await db.SaveChangesAsync(ct);
    }

    private static int ParseFirstPageIndex(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return 0;
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.TryGetProperty("firstPageIndex", out var v) ? v.GetInt32() : 0;
    }
}
