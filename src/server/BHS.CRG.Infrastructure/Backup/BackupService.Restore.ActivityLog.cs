using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Activity;

namespace BHS.CRG.Infrastructure.Backup;

public partial class BackupService
{
    /// <summary>
    /// Журнал действий из копии (ТЗ CORE-28).
    ///
    /// Только ДОПИСЫВАНИЕ и только новых идентификаторов: обновления здесь нет и быть не может —
    /// запись журнала неизменяема, и upsert поверх собственной истории экземпляра стёр бы её.
    /// Поэтому и счётчик один: «добавлено».
    ///
    /// Идёт через службу журнала, а не через набор напрямую: прямой доступ есть только у неё
    /// (сторож <c>ActivityLogInventoryTests</c>).
    /// </summary>
    private async Task RestoreActivityLogAsync(
        BackupActivityRecord[] items, RestoreStats stats, CancellationToken ct)
    {
        if (items.Length == 0) return;

        var added = await journal.ImportAsync(
            [.. items.Select(i => ActivityRecord.Create(
                i.Action, i.ActorId, i.ActorName, i.TargetId, i.TargetLabel, i.Before, i.After,
                // Копия старого формата сессии не несёт — запись ложится одиночной (ТЗ CORE-28.2).
                id: i.Id, occurredAt: i.OccurredAt, sessionId: i.SessionId, seriesHead: i.SeriesHead ?? true,
                // Копия до канала (issue #1336) его не несёт — канал выводится по автору, тем же правилом,
                // что у миграции (ActivityChannel.ForLegacy). Неизвестный код (копия более новой версии) —
                // по тому же правилу: записать то, чего эта версия не умеет показать, значит сломать отбор.
                channel: ActivityChannel.Find(i.Channel)))],
            ct);

        db.ChangeTracker.Clear();
        stats.Count("Журнал действий", added, 0);
    }
}
