using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// То, из чего считается готовность счёта, — и ровно то же, из чего собран ответ чтения: строки, части
/// разноски, состояния записей ядра, названия позиций и сводка разноски.
/// </summary>
/// <param name="Records">Состояния записей ядра. ⚠️ У снимка пачкой — общие на всю пачку: спрашивать
/// по ним можно только о записях, на которые ссылается сам счёт.</param>
/// <param name="Names">Названия позиций; <c>null</c> — вида «Номенклатура» в установке нет. Общие на
/// пачку, как и <paramref name="Records" />.</param>
public sealed record InvoiceSnapshot(
    IReadOnlyList<InvoiceLine> Lines, IReadOnlyList<InvoiceAllocation> Parts, AllocationPlaces Known,
    IReadOnlyDictionary<Guid, ReferenceState> Records, IReadOnlyDictionary<Guid, string?>? Names,
    decimal Tolerance, InvoiceAllocationRead Allocation, IReadOnlySet<Guid> Lost, InvoiceReadiness Readiness);

/// <summary>
/// Снимки счетов для готовности (issue #1291, #1292) — одному счёту и пачке, <b>одним кодом</b>.
///
/// <para>Потребителей три: ответ чтения счёта, переход «разобран» и список счетов, который называет в
/// строке, чего черновику не хватает. Собирай список свой снимок сам, причина в строке разошлась бы с
/// перечнем открытого счёта на первом новом условии — и человек, открыв счёт «осталась разноска», нашёл
/// бы там ещё и плательщика. Поэтому чтение одного счёта — это пачка из одного.</para>
///
/// <para>⚠️ Цена пачки не растёт с числом счетов: строки, части, состояния записей и названия позиций
/// читаются по одному запросу на ВСЮ пачку. Запрос на счёт превратил бы открытие списка в сотни
/// обращений.</para>
/// </summary>
public sealed class InvoiceSnapshots(
    CostsDbContext db, IModuleCatalog catalog, AllocationPlacesSource places, IModuleReferenceTargets targets,
    IModuleSettings settings)
{
    public async Task<InvoiceSnapshot> OfAsync(Invoice invoice, CancellationToken ct) =>
        (await OfAsync([invoice], ct))[invoice.Id];

    public async Task<IReadOnlyDictionary<Guid, InvoiceSnapshot>> OfAsync(
        IReadOnlyList<Invoice> invoices, CancellationToken ct)
    {
        if (invoices.Count == 0) return new Dictionary<Guid, InvoiceSnapshot>();

        var lines = db.InvoiceLines.AsNoTracking();
        var parts = db.InvoiceAllocations.AsNoTracking();
        if (invoices.Count == 1)
        {
            // Один счёт — равенством: это чтение зовётся после каждой записи, и план у него обязан
            // остаться прежним — поиском по индексу внешнего ключа.
            var one = invoices[0].Id;
            lines = lines.Where(l => l.InvoiceId == one);
            parts = parts.Where(a => a.InvoiceId == one);
        }
        else
        {
            // Пачка — подзапросом, а не сравнением с массивом: подзапрос база сворачивает в хеш, а
            // массив сверяла бы перебором на каждую строку (замер в InvoiceTable).
            var ids = invoices.Select(i => i.Id).ToArray();
            var keys = db.Database.SqlQuery<Guid>($"SELECT unnest({ids}) AS \"Value\"");
            lines = lines.Where(l => keys.Contains(l.InvoiceId));
            parts = parts.Where(a => keys.Contains(a.InvoiceId));
        }

        var allLines = await lines.OrderBy(l => l.Ordinal).ToListAsync(ct);
        var allParts = await parts.ToListAsync(ct);
        var known = allParts.Count == 0 ? AllocationPlaces.None : await places.LoadAsync(ct);

        // Записи справочников, на которые ссылаются шапка и строки, — одним вопросом ядру. Спрашивается
        // «есть ли запись», а не «есть ли она в списке организаций»: список не различает удалённую
        // запись, запись другого вида и вид, которого нет в установке (issue #1184).
        var records = await targets.StatesAsync(ReferenceTarget.Record,
            [.. invoices.SelectMany(i => new[] { i.SupplierId, i.PayerId })
                .Concat(allLines.Select(l => l.NomenclatureId))
                .Concat(allParts.Select(p => p.ArticleId)).OfType<Guid>().Distinct()], ct);
        known = known with { Existing = records.Where(r => r.Value != ReferenceState.Lost).Select(r => r.Key).ToHashSet() };

        var tolerance = await settings.GetAsync(CostsSettings.AllocationTolerance, ct);
        var names = await InvoiceEndpoints.NomenclatureNamesAsync(catalog, allLines, ct);
        var lost = records.Where(r => r.Value == ReferenceState.Lost).Select(r => r.Key).ToHashSet();

        var linesOf = allLines.ToLookup(l => l.InvoiceId);
        var partsOf = allParts.ToLookup(a => a.InvoiceId);

        // ⚠️ Готовность считается из уже прочитанного: ни одного запроса сверх тех, что нужны самому
        // ответу. Чтение счёта зовётся после каждой записи, и второй опрос ядра удвоил бы его цену.
        return invoices.ToDictionary(i => i.Id, invoice =>
        {
            IReadOnlyList<InvoiceLine> own = [.. linesOf[invoice.Id]];
            IReadOnlyList<InvoiceAllocation> shares = [.. partsOf[invoice.Id]];
            var allocation = InvoiceAllocations.Read(invoice, own.Select(InvoiceAllocations.Line), shares, known, tolerance);
            return new InvoiceSnapshot(own, shares, known, records, names, tolerance, allocation, lost,
                InvoiceReadiness.Of(invoice, own, names, lost, allocation.Summary));
        });
    }
}
