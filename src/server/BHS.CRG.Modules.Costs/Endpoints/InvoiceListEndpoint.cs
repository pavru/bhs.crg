using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Список счетов на оплату (<c>GET /api/costs/invoices</c>) и его отборы. Отдельно от остальных адресов
/// счёта (<see cref="InvoiceEndpoints" />): у списка свои заботы — очереди, пометки строк, счётчики.
/// </summary>
public static class InvoiceListEndpoint
{
    /// <summary>
    /// Реестр счетов — плоским списком, без реквизитов (ТЗ COST-9.1: отбор по сроку и состоянию).
    ///
    /// <para>Названия поставщиков берутся ОДНИМ обращением к справочнику и раскладываются по
    /// идентификаторам: запрос на строку превратил бы открытие реестра в сотню обращений, и заметно
    /// это стало бы у заказчика, а не здесь.</para>
    ///
    /// <para>⚠️ Постраничности здесь пока нет, и это named-решение, а не недосмотр: экран реестра со
    /// своей сеткой, отбором и сохранёнными представлениями приезжает задачей G4 (issue #1097), и
    /// страницы обязаны совпасть с тем, что попросит сетка. Отдай мы сейчас произвольные первые сто,
    /// «счёт есть, а в списке его нет» стало бы поведением, на которое кто-нибудь успел бы
    /// опереться.</para>
    /// </summary>
    /// <param name="needsParsing">Отбор «Разобрать» (ТЗ COST-6.2): только счета, у которых есть строки
    /// без позиции номенклатуры. Отбором, а не сортировкой: это рабочая очередь снабженца, и счета,
    /// которые разбирать не надо, в ней мешают.
    ///
    /// <para>⚠️ Счёт БЕЗ строк вовсе в этот отбор НЕ попадает, хотя разбирать его тоже надо. Причина:
    /// «строк нет» и «строки ждут позиции» — разные работы, и вторую делают по скану, который уже
    /// разобран наполовину. Очередь «строк нет вовсе» — это отбор по состоянию «черновик», он приезжает
    /// с сеткой реестра (G4, issue #1097) вместе с остальными сохранёнными представлениями.</para>
    ///
    /// <para>⚠️ ОТКЛОНЁННЫЙ счёт в отбор тоже не попадает, сколько бы строк у него ни ждало позиции:
    /// «не платим» — значит и не разбираем, переход «разобран» на нём отвечает отказом. Очередь, в
    /// которой стоит то, что разобрать нельзя, перестаёт быть очередью (issue #1166).</para></param>
    /// <param name="fix">Отбор «наведите порядок» (issue #1186): <c>lost</c> — счета с удалённой
    /// записью, которые можно исправить; <c>archived</c> — неоплаченные счета с записью из архива. Те же
    /// множества, что у готовых отборов таблицы счетов: число на чипе считается там, и разойдись
    /// правила — под чипом «3» стояло бы два счёта.</param>
    /// <param name="unrecognized">Отбор «Не распознано» (issue #1077): черновики со сканом без строк,
    /// чей скан сейчас не читается. Определение — в <see cref="InvoiceListRecognition" />.</param>
    public static async Task<Ok<IReadOnlyList<InvoiceListItem>>> ListAsync(
        CostsDbContext db, IModuleCatalog catalog, InvoiceReferenceTrouble trouble, ILoggerFactory logs,
        InvoiceListRecognition recognition,
        CancellationToken ct, bool needsParsing = false, string? fix = null, bool unrecognized = false)
    {
        if (fix is not (null or InvoiceListReferences.FixLost or InvoiceListReferences.FixArchived))
            throw new InvalidRequestException(
                $"Отбор списка счетов «{fix}» не известен: бывают «{InvoiceListReferences.FixLost}» и «{InvoiceListReferences.FixArchived}».");

        // Ссылки не на месте — на каждое чтение списка и с архивом: пометку несёт каждая строка, а
        // видно в строке только поставщика — счёт с удалённой позицией выглядел бы чистым.
        var marks = await InvoiceListMarks.ReadAsync(
            trouble, required: fix is not null, logs.CreateLogger(typeof(InvoiceEndpoints)), ct);

        // Очередь «Разобрать» отбирает БАЗА (issue #1171), и запрос идёт от строк без позиции, а не от
        // счетов: так частичный индекс ix_invoice_lines_unmatched — он заведён ровно под этот отбор —
        // получает работу. Раньше реестр читал все счета и отбирал очередь в памяти: индекс при этом не
        // читал никто, он только удорожал запись строк.
        var selected = db.Invoices.AsNoTracking();
        if (needsParsing)
            selected = selected.Where(i => i.State != InvoiceState.Rejected
                && db.InvoiceLines.Where(l => l.NomenclatureId == null).Select(l => l.InvoiceId).Contains(i.Id));

        if (fix is not null)
        {
            // Подзапросом, а не сравнением с массивом: его база сворачивает в хеш, а сравнение исполняет
            // перебором на каждую строку — счетов с архивной записью бывают тысячи (замер в InvoiceTable).
            var named = fix == InvoiceListReferences.FixLost ? marks!.Fixable : marks!.Archived;
            var keys = db.Database.SqlQuery<Guid>($"SELECT unnest({named}) AS \"Value\"");
            selected = selected.Where(i => keys.Contains(i.Id));
            if (fix == InvoiceListReferences.FixArchived)
                selected = selected.Where(i => i.Payment != InvoicePaymentState.Paid);
        }

        // Распознавание — на каждое чтение списка: пометку «читается» и «не распознан» несёт строка.
        var scans = await recognition.ReadAsync(ct);
        if (unrecognized)
        {
            var failed = InvoiceListRecognition.Unrecognized(scans);
            selected = selected.Where(i => failed.Contains(i.Id));
        }

        var invoices = await selected
            .OrderByDescending(i => i.IssuedOn)
            .ThenByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        // Счётчики строк — ОДНИМ группирующим запросом, без чтения самих строк: на экране нужны два
        // числа на счёт, а не строки. Запрос на счёт превратил бы открытие реестра в сотню обращений —
        // той же ценой, что уже названа у названий поставщиков. У очереди — только по её счетам:
        // группировать всю таблицу ради десятка счетов незачем.
        var counted = db.InvoiceLines.AsNoTracking();
        if (fix is not null)
        {
            // Под отбором «наведите порядок» счетов бывают тысячи — тем же подзапросом, каким отобран
            // список, а не массивом их ключей: массив база сверяла бы перебором (ревью PR #1241).
            counted = counted.Where(l => selected.Select(i => i.Id).Contains(l.InvoiceId));
        }
        else if (needsParsing)
        {
            var queued = invoices.Select(i => i.Id).ToList();
            counted = counted.Where(l => queued.Contains(l.InvoiceId));
        }

        var counters = await counted
            .GroupBy(l => l.InvoiceId)
            .Select(g => new
            {
                InvoiceId = g.Key,
                Count = g.Count(),
                Unmatched = g.Count(l => l.NomenclatureId == null),
            })
            .ToListAsync(ct);

        // Словарём «счёт → (строк, ждут позиции)». Сумм здесь нет НАРОЧНО: реестру они не нужны, а
        // подставленный нуль в поле суммы — это неправда, на которую кто-нибудь однажды сошлётся.
        var lines = counters.ToDictionary(c => c.InvoiceId, c => (Count: c.Count, Unmatched: c.Unmatched));

        var names = await SupplierNamesAsync(catalog, invoices, ct);

        return TypedResults.Ok<IReadOnlyList<InvoiceListItem>>(
            [.. invoices.Select(i => InvoiceViews.Item(
                i,
                i.SupplierId is { } id && names.TryGetValue(id, out var supplier) ? supplier.DisplayName : null,
                i.SupplierId is { } key && names.TryGetValue(key, out var found) && found.Archived,
                lines.TryGetValue(i.Id, out var total) ? total.Count : 0,
                lines.TryGetValue(i.Id, out var waiting) ? waiting.Unmatched : 0) with
                {
                    References = marks?.Of(i),
                    Recognition = scans.GetValueOrDefault(i.Id),
                })]);
    }

    private static async Task<Dictionary<Guid, ModuleCatalogEntry>> SupplierNamesAsync(
        IModuleCatalog catalog, List<Invoice> invoices, CancellationToken ct)
    {
        if (!invoices.Any(i => i.SupplierId is not null)) return [];

        // Спрашиваем ТОЛЬКО когда есть что разрешать (условие выше). Это не экономия запроса: вида
        // «Организация» на чистой установке может не быть вовсе — тип заводит человек, — и порт на
        // неизвестный вид отвечает отказом. Счетов без этого типа тоже нет (их не завести), поэтому
        // пустой реестр остаётся пустым реестром, а не превращается в отказ.
        //
        // Вид записи — КОД ТИПА, тот же, которым названа цель поля «Поставщик». В списке приходят и
        // записи подтипов: подтип организации — организация, и живые поставщики заказчика лежат
        // именно подтипом.
        // Вида нет (ноль) — значит и организаций нет: реестр остаётся реестром, а не превращается в
        // отказ. Счёт со ссылкой на организацию при этом покажет «организация не найдена» — что
        // правда: тип, на который он ссылается, из системы исчез.
        // Показ: это названия поставщиков УЖЕ заведённых счетов. Архивный поставщик у счёта
        // закрытого периода обязан читаться по имени, а признак реестр ставит значком (issue #1185).
        var organizations = await catalog.ListAsync(CostsRecordTypes.OrganizationCode, RecordsFor.Display, ct);
        return organizations?.ToDictionary(o => o.Id) ?? [];
    }
}
