using System.Text.Json;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Generation;
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

            var lastProgress = DateTimeOffset.MinValue;
            Func<string, int, int, Task> report = async (unit, cur, total) =>
            {
                var now = DateTimeOffset.UtcNow;
                if (cur != total && now - lastProgress < TimeSpan.FromSeconds(1.5)) return;
                lastProgress = now;
                await UpdateJobAsync(jobId, j => j.ReportProgress($"{cur} из {total} {unit}"), ct);
            };

            switch (kind)
            {
                case JobKind.RecognizeGostSet:
                    // issue #38: targetId = fileId (набор-centric), источников не создаёт.
                    await scope.ServiceProvider.GetRequiredService<DataSetPdfRecognitionService>()
                        .RecognizeFileAsync(targetId, confirm: true, ct, (c, t) => report("листов", c, t));
                    break;

                case JobKind.RecognizeDocument:
                    await scope.ServiceProvider.GetRequiredService<DataSetPdfRecognitionService>()
                        .RecognizeDocumentAsync(targetId, ParseFirstPageIndex(payload), ct, (c, t) => report("листов", c, t));
                    break;

                case JobKind.RecognizeTable:
                    await scope.ServiceProvider.GetRequiredService<DataSetPdfRecognitionService>()
                        .RecognizeDocumentTableAsync(targetId, ParseFirstPageIndex(payload), ct);
                    break;

                case JobKind.AssembleDocumentSet:
                    await scope.ServiceProvider.GetRequiredService<DocumentSetAssemblyService>()
                        .AssembleAsync(targetId, ParseInstanceIds(payload), userId, ct, (c, t) => report("документов", c, t));
                    break;

                case JobKind.AuditQualityLinks:
                    // targetId = setId. Итог сохраняется одной строкой на комплект и читается
                    // отдельным запросом — из HTTP-реквеста этот прогон уже не помещался (#628).
                    await scope.ServiceProvider.GetRequiredService<IQualitySetAuditRunner>()
                        .RunAndStoreAsync(targetId, userId, (c, t) => report("документов", c, t), ct);
                    break;

                case JobKind.CreateBackup:
                    // Цели у задачи нет: копия снимается со всей системы, а не с объекта. Прогресс
                    // честный — по числу файлов, которые перекачиваются из хранилища в архив.
                    await scope.ServiceProvider.GetRequiredService<BHS.CRG.Infrastructure.Backup.BackupJobRunner>()
                        .RunAsync(userId, ParseFlag(payload, "scheduled"), ParseBackupScope(payload),
                            (c, t) => report("файлов", c, t), ct);
                    break;

                case JobKind.SendEmail:
                    var (subj, body, emailKind, to) = ParseEmailPayload(payload);
                    var emailSvc = scope.ServiceProvider.GetRequiredService<BHS.CRG.Infrastructure.Email.DocumentSetEmailService>();
                    if (emailKind == "document")
                        await emailSvc.SendDocumentAsync(targetId, to, subj, body, userId, ct);
                    else
                        await emailSvc.SendSetAsync(targetId, to, subj, body, userId, ct);
                    break;

                default:
                    throw new InvalidOperationException($"Неизвестный вид задачи: {kind}");
            }

            await UpdateJobAsync(jobId, j => j.Succeed(), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Фоновая задача {JobId} ({Kind}) завершилась ошибкой", jobId, kind);
            // Второй выход наружу помимо HTTP-ответа, и правило здесь то же (issue #691): наш отказ
            // человек читает дословно, чужое сообщение — нет. Через эту дверь в колокольчик уходил,
            // например, вывод компилятора шаблона со всеми путями временной папки.
            var text = Refusals.TextOr(ex, $"Внутренняя ошибка. Задача {jobId} — подробности в журнале сервера.");
            await UpdateJobAsync(jobId, j => j.Fail(text), CancellationToken.None);
            // Единая точка публикации ошибки задачи в колокольчик (handoff: задача ушла из индикатора → всплыла ошибкой).
            await PublishFailureAsync(userId, title, text);
        }
    }

    private async Task PublishFailureAsync(Guid userId, string title, string error)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
            // Задача без владельца — системная (её поставило расписание, а не человек). Уведомление,
            // посланное «пользователю Guid.Empty», не увидел бы никто, то есть ночная неудача
            // копирования пропала бы бесследно; общесистемное видно всем вошедшим — не только
            // администраторам. Адресовать его одним администраторам нечем: видимость уведомления
            // задаётся владельцем, а не ролью. Для нашего случая это приемлемо (пользователи —
            // сотрудники одной компании с равным допуском, см. issue #675), но текст такого отказа
            // пишется с оглядкой: его прочитает и тот, кто не знает, что такое deploy/.env.
            await notifications.PublishAsync(NotificationSeverity.Error, $"Ошибка: {title}", error,
                "Фоновые задачи", userId: userId == Guid.Empty ? null : userId);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Не удалось опубликовать уведомление об ошибке задачи"); }
    }

    /// <summary>
    /// Состав копии из payload — {"scope":"Full"} (issue #833). Неизвестное значение читаем как
    /// «настройка»: копия меньше ожидаемой заметна сразу (в списке виден состав), а вот полная
    /// копия там, где просили конфигурационную, съела бы место молча.
    /// </summary>
    private static BHS.CRG.Application.Backup.BackupScope ParseBackupScope(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return BHS.CRG.Application.Backup.BackupScope.Configuration;
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.TryGetProperty("scope", out var v)
               && v.ValueKind == JsonValueKind.String
               && string.Equals(v.GetString(), "Full", StringComparison.OrdinalIgnoreCase)
            ? BHS.CRG.Application.Backup.BackupScope.Full
            : BHS.CRG.Application.Backup.BackupScope.Configuration;
    }

    /// <summary>Булев флаг из payload задачи — напр. {"scheduled":true}.</summary>
    private static bool ParseFlag(string? payload, string name)
    {
        if (string.IsNullOrEmpty(payload)) return false;
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
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

    // Письмо: {"subject":..,"body":..,"kind":"set"|"document","to":[...]}; targetId — setId (set) или instanceId (document).
    private static (string? subject, string? body, string kind, IReadOnlyList<string> to) ParseEmailPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return (null, null, "set", []);
        using var doc = JsonDocument.Parse(payload);
        string? Get(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var to = doc.RootElement.TryGetProperty("to", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
        return (Get("subject"), Get("body"), Get("kind") ?? "set", to);
    }

    // Подмножество документов для сборки комплекта — {"instanceIds":[...]}; отсутствует/пусто → весь комплект.
    private static IReadOnlyList<Guid>? ParseInstanceIds(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("instanceIds", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var ids = arr.EnumerateArray().Where(e => e.TryGetGuid(out _)).Select(e => e.GetGuid()).ToList();
        return ids.Count > 0 ? ids : null;
    }
}
