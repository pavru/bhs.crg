using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Periods;

namespace BHS.CRG.Infrastructure.Backup;

public partial class BackupService
{
    /// <summary>
    /// Закрытия учётного периода из копии (ТЗ CORE-35, issue #1081).
    ///
    /// Только ДОПИСЫВАНИЕ недостающих — как у журнала: запись неизменяема, и обновление существующей
    /// упёрлось бы в защиту контекста. Собственные закрытия экземпляра остаются на месте, поэтому
    /// после восстановления граница — позднейшая из приехавшей и здешней: копия не может «открыть»
    /// период, который здесь закрыли позже.
    ///
    /// Идёт через службу закрытия: прямой доступ к набору есть только у неё
    /// (сторож <c>PeriodClosureInventoryTests</c>).
    /// </summary>
    private async Task RestorePeriodClosuresAsync(
        BackupPeriodClosure[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;

        var valid = new List<PeriodClosure>();
        foreach (var item in items)
        {
            // IsDefined — потому что TryParse принимает и числа: «5» разобралось бы в значение,
            // которого в перечислении нет, легло бы в таблицу, и каждое чтение границ падало бы на
            // нём — а строку из дописываемой таблицы уже не убрать (ревью PR #1189).
            if (!Enum.TryParse<PeriodClosureKind>(item.Kind, out var kind) || !Enum.IsDefined(kind)
                || !Enum.TryParse<PeriodContourKind>(item.Contour, out var contour) || !Enum.IsDefined(contour)
                || (contour == PeriodContourKind.Construction) != (item.ConstructionId is not null)
                || (kind == PeriodClosureKind.Reopen) != (item.CancelsId is not null))
            {
                warnings.Add($"Закрытие периода по {item.Through:dd.MM.yyyy}: запись не разобрана " +
                             $"(вид «{item.Kind}», контур «{item.Contour}»), пропущена.");
                continue;
            }

            valid.Add(PeriodClosure.Restore(item.Id, kind, contour, item.ConstructionId, item.From,
                item.Through, item.At, item.ById, item.ByName, item.Reason, item.CancelsId));
        }

        var added = await periods.ImportAsync(valid, ct);
        db.ChangeTracker.Clear();
        stats.Count("Закрытия периода", added, 0);
    }
}
