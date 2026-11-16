using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Offers;

/// <summary>
/// Всё, что КП берёт у сметы и у справочников, — одним местом (change <c>plan-13-offers</c>): строки редакции
/// для переноса, базы наценки по строкам (решение В-8), заказчик.
///
/// <para>⚠️ <b>Строки редакции здесь читаются ОДНОЙ редакцией</b> — той, из которой КП сделано. Это не план
/// стройки (<c>PlanRows.Current</c>): КП читает редакцию такой, какой она была при создании, и редакция
/// зафиксирована — база не плывёт (ТЗ PLAN-5). Перепись — <c>PlanRowsInventoryTests</c>.</para>
/// </summary>
public sealed class OfferDesk(PlanDbContext db, IModuleCatalog catalog)
{
    /// <summary>Строки редакции в порядке файла — для переноса в КП.</summary>
    internal Task<List<EstimateRow>> RevisionRowsAsync(Guid revisionId, CancellationToken ct) =>
        db.EstimateRows.AsNoTracking().Where(r => r.RevisionId == revisionId).OrderBy(r => r.Order).ToListAsync(ct);

    /// <summary>
    /// Базы наценки — итоги строк редакции в текущих ценах с НР и СП (решение В-8) по идентификатору строки.
    /// Себестоимость: читает её расчёт, а в ответ она идёт только с <c>plan.estimate.cost</c>.
    /// </summary>
    internal Task<Dictionary<Guid, decimal?>> BaseTotalsAsync(Guid revisionId, CancellationToken ct) =>
        db.EstimateRows.AsNoTracking().Where(r => r.RevisionId == revisionId)
            .ToDictionaryAsync(r => r.Id, r => r.Total, ct);

    /// <summary>Следующий порядковый номер КП в году. Гонку двух заведений ловит уникальный ключ.</summary>
    internal async Task<int> NextSequenceAsync(int year, CancellationToken ct) =>
        (await db.Offers.Where(o => o.Year == year).MaxAsync(o => (int?)o.Sequence, ct) ?? 0) + 1;

    /// <summary>
    /// Заказчик, названный ЭТОЙ правкой: организация справочника по роли, на месте и не в архиве (ТЗ
    /// CORE-34.4). Прежний заказчик, оставленный как был, не проверяется — архив не делает КП непереносимым.
    /// </summary>
    internal async Task EnsureCustomerAsync(Guid? customerId, Guid? stored, CancellationToken ct)
    {
        if (customerId is not { } id || id == stored) return;
        var refs = await catalog.RefsAsync(FunctionalTag.RoleOrganization, [id], ct)
            ?? throw new ConflictException(
                "Справочник организаций не назначен, поэтому заказчика КП выбрать не из чего. Ни один тип не несёт " +
                "тэг типа «Справочник организаций» (либо несут несколько). Назначает администратор: «Настройка " +
                "системы» → «Сопоставление полей».");
        var found = refs.FirstOrDefault(r => r.Id == id)
            ?? throw new InvalidRequestException("Заказчика нет в справочнике организаций — его удалили либо адрес неверен.");
        if (found.Archived)
            throw new InvalidRequestException(
                $"Организация «{found.DisplayName}» в архиве — заказчиком нового КП её не выбрать. Верните её из архива " +
                "или выберите другую.");
    }

    /// <summary>Названия заказчиков одним обращением; <c>null</c> — справочника нет, назвать нечем.</summary>
    internal async Task<IReadOnlyDictionary<Guid, ModuleCatalogRef>?> CustomersAsync(
        IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, ModuleCatalogRef>();
        var refs = await catalog.RefsAsync(FunctionalTag.RoleOrganization, wanted, ct);
        return refs?.ToDictionary(r => r.Id);
    }
}
