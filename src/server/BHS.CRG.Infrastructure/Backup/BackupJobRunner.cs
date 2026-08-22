using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Notifications;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Снятие копии как фоновая задача (issue #831).
///
/// Почему не в HTTP-запросе. Копия с библиотекой качества — это минуты чтения базы и перекачки
/// сканов из хранилища; запрос столько не живёт (прокси разрывает его молча), а браузер держал бы
/// вкладку открытой всё это время. В фоне же операция видна пилюлей задач, переживает reload и
/// закрытие вкладки, а её отказ приходит в колокольчик, а не пропадает вместе с ответом.
/// </summary>
public sealed class BackupJobRunner(
    BackupService service,
    BackupFileStore store,
    INotificationService notifications)
{
    public async Task<BackupFileInfo> RunAsync(Guid userId, Func<int, int, Task>? progress, CancellationToken ct)
    {
        // Проверка повторяется здесь, хотя эндпоинт уже отказал бы: между постановкой в очередь и
        // выполнением каталог мог наполниться другой задачей, а «не влезло» обязано быть отказом,
        // а не молчанием.
        store.EnsureRoomForNewCopy();

        var temp = store.CreateTempPath();
        BackupFileInfo info;
        try
        {
            var summary = await service.ExportToFileAsync(temp, progress, ct);
            info = store.Publish(temp, BackupFileStore.BuildFileName(summary.CreatedAt, summary.AppVersion));
        }
        catch
        {
            store.TryDelete(temp);
            throw;
        }

        // Успех — в колокольчик: копия снимается минутами, и к её концу человек уже не смотрит на
        // экран настроек. Отказ туда же кладёт общий обработчик фоновых задач.
        await notifications.PublishAsync(NotificationSeverity.Info, "Резервная копия создана",
            $"{info.FileName} — {FormatSize(info.SizeBytes)}.", "Резервное копирование", userId, ct: ct);
        return info;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.#} ГБ",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:0.#} МБ",
        >= 1024 => $"{bytes / 1024d:0.#} КБ",
        _ => $"{bytes} Б",
    };
}
