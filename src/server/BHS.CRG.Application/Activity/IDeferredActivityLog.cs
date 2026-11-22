using BHS.CRG.Domain.Activity;

namespace BHS.CRG.Application.Activity;

/// <summary>Дописывает отдельное отложенное событие журнала с уже сохранённым идентификатором.</summary>
public interface IDeferredActivityLog
{
    Task AppendOnceAsync(ActivityRecord record, CancellationToken ct);
}
