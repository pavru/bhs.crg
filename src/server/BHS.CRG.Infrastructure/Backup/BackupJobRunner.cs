using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.Updates;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Снятие копии как фоновая задача (issue #831) — ручной и плановой (issue #832).
///
/// Почему не в HTTP-запросе. Копия с библиотекой качества — это минуты чтения базы и перекачки
/// сканов из хранилища; запрос столько не живёт (прокси разрывает его молча), а браузер держал бы
/// вкладку открытой всё это время. В фоне же операция видна пилюлей задач, переживает reload и
/// закрытие вкладки, а её отказ приходит в колокольчик, а не пропадает вместе с ответом.
///
/// Плановая копия отличается от ручной тремя вещами, и все три — следствие того, что её никто не
/// ждёт у экрана: она убирает за собой прежние плановые, о своём успехе молчит (иначе колокольчик
/// каждое утро начинался бы с новости «всё хорошо»), а свой отказ записывает в след службы —
/// чтобы он был виден в списке копий, а не только в тот момент, когда пришёл.
/// </summary>
public sealed class BackupJobRunner(
    BackupService service,
    BackupFileStore store,
    ServiceStateStore stateStore,
    IIntegrationSettings settings,
    INotificationService notifications)
{
    public async Task<BackupFileInfo> RunAsync(
        Guid userId, bool scheduled, Func<int, int, Task>? progress, CancellationToken ct)
    {
        if (!scheduled) return await RunManualAsync(userId, progress, ct);
        return await RunScheduledAsync(progress, ct);
    }

    private async Task<BackupFileInfo> RunManualAsync(Guid userId, Func<int, int, Task>? progress, CancellationToken ct)
    {
        // Проверка повторяется здесь, хотя эндпоинт уже отказал бы: между постановкой в очередь и
        // выполнением каталог мог наполниться другой задачей, а «не влезло» обязано быть отказом,
        // а не молчанием.
        store.EnsureRoomForNewCopy();

        var info = await ExportAsync(progress, ct);

        // Успех — в колокольчик: копия снимается минутами, и к её концу человек уже не смотрит на
        // экран настроек. Отказ туда же кладёт общий обработчик фоновых задач.
        await notifications.PublishAsync(NotificationSeverity.Info, "Резервная копия создана",
            $"{info.FileName} — {FormatSize(info.SizeBytes)}.", "Резервное копирование", userId, ct: ct);
        return info;
    }

    private async Task<BackupFileInfo> RunScheduledAsync(Func<int, int, Task>? progress, CancellationToken ct)
    {
        var keep = (await settings.GetEffectiveAsync(ct)).Backup.KeepCount;
        var state = await stateStore.LoadAsync<BackupScheduleState>(BackupScheduleStateKeys.Schedule, ct);

        try
        {
            // Имена копий, которых в каталоге больше нет (администратор удалил их сам), из списка
            // своих вычёркиваем: иначе он растёт вечно и хранит историю вместо состояния.
            var present = store.List().Select(f => f.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            state.Managed.RemoveAll(n => !present.Contains(n));

            // Уборка ДО снятия, а не только после. Иначе расписание запирает само себя: каталог
            // вмещает BACKUP_KEEP_COUNT копий, седьмая плановая упирается в предел, отказ приходит
            // раньше уборки — и так каждую ночь, вечно. Освобождаем ровно одно место, оставляя
            // keep−1 прежних; последняя плановая при этом цела (PruneScheduled не опускается
            // ниже одной).
            Forget(state, store.PruneScheduled(state.Managed, keep - 1));
            store.EnsureRoomForNewCopy();

            var info = await ExportAsync(progress, ct);

            state.Managed.Add(info.FileName);
            state.LastFileName = info.FileName;
            state.LastSuccessAt = DateTimeOffset.UtcNow;
            state.LastError = null;
            state.LastErrorAt = null;

            // Уборка ПОСЛЕ: теперь плановых снова keep.
            Forget(state, store.PruneScheduled(state.Managed, keep));
            await stateStore.SaveAsync(BackupScheduleStateKeys.Schedule, state, ct);
            return info;
        }
        catch (Exception ex)
        {
            // Плановую копию никто не ждёт у экрана, поэтому отказ обязан остаться записанным:
            // в списке копий видно, что последняя ночь не удалась и почему. Уведомление положит
            // общий обработчик задач — общесистемное, потому что у плановой задачи нет владельца.
            state.LastError = Refusals.TextOr(ex, "Внутренняя ошибка — подробности в журнале сервера.");
            state.LastErrorAt = DateTimeOffset.UtcNow;
            await stateStore.SaveAsync(BackupScheduleStateKeys.Schedule, state, CancellationToken.None);
            throw;
        }
    }

    private async Task<BackupFileInfo> ExportAsync(Func<int, int, Task>? progress, CancellationToken ct)
    {
        var temp = store.CreateTempPath();
        try
        {
            var summary = await service.ExportToFileAsync(temp, progress, ct);
            return store.Publish(temp, BackupFileStore.BuildFileName(summary.CreatedAt, summary.AppVersion));
        }
        catch
        {
            store.TryDelete(temp);
            throw;
        }
    }

    /// <summary>Убранное вычёркиваем из списка своих — иначе он растёт вечно именами файлов, которых нет.</summary>
    private static void Forget(BackupScheduleState state, IReadOnlyList<string> deleted)
    {
        if (deleted.Count == 0) return;
        state.Managed.RemoveAll(n => deleted.Contains(n, StringComparer.OrdinalIgnoreCase));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.#} ГБ",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.#} МБ",
        >= 1024 => $"{bytes / 1024d:0.#} КБ",
        _ => $"{bytes} Б",
    };
}
