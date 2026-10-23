using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Удаление позиции перечня работ (ТЗ CORE-11, issue #964).
///
/// <para>⚠️ Адреса у этой команды нет, и это не упущение: позиции заводит сервер по «найди или
/// создай» — портом модуля <c>IModulePlanItems</c> (CORE-12), — а человек их ни заводит, ни удаляет.
/// Позиция, оставшаяся без держателей после упавшего импорта, — не мусор, а ключ: её найдёт повтор.
/// Команда существует потому, что правило «удаляется только без ссылок» относится к самому перечню, а
/// не к экрану: появись дверь раньше правила, она пришла бы без него.</para>
/// </summary>
public sealed record DeleteWorkPlanItemCommand(Guid Id) : IRequest;

public sealed class WorkPlanItemHandlers(
    IRepository<WorkPlanItem> repo, IRecordHolders holders)
    : IRequestHandler<DeleteWorkPlanItemCommand>
{
    public async Task Handle(DeleteWorkPlanItemCommand cmd, CancellationToken ct)
    {
        var item = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();

        // Ссылки на позицию живут в таблицах модулей — позиции смет, строки отчётов, разноска затрат
        // (ТЗ CORE-11). Позиция — общая точка модулей: удаление из-под них оставило бы план, факт и
        // акты без того, к чему они относятся.
        (await holders.FindAsync([cmd.Id], ct)).EnsureNone("позицию перечня работ");

        repo.Remove(item);
        await repo.SaveChangesAsync(ct);
    }
}
