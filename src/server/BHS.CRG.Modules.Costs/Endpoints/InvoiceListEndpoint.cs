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
    /// <param name="needsParsing">Отбор «Разобрать» (ТЗ COST-6.2): все недоведённые счета — черновики
    /// (<see cref="Queue" />). Отбором, а не сортировкой: это рабочая очередь, и счета, которые
    /// разбирать не надо, в ней мешают.</param>
    /// <param name="fix">Отбор «наведите порядок» (issue #1186): <c>lost</c> — счета с удалённой
    /// записью, которые можно исправить; <c>archived</c> — неоплаченные счета с записью из архива. Те же
    /// множества, что у готовых отборов таблицы счетов: число на чипе считается там, и разойдись
    /// правила — под чипом «3» стояло бы два счёта.</param>
    /// <param name="unrecognized">Отбор «Не распознано» (issue #1077): черновики со сканом без строк,
    /// чей скан сейчас не читается. Определение — в <see cref="InvoiceListRecognition" />.</param>
    public static async Task<Ok<IReadOnlyList<InvoiceListItem>>> ListAsync(
        CostsDbContext db, IModuleCatalog catalog, InvoiceReferenceTrouble trouble, ILoggerFactory logs,
        InvoiceListRecognition recognition, InvoiceSnapshots snapshots, IModulePeriods periods, IModuleClock clock,
        CancellationToken ct, bool needsParsing = false, string? fix = null, bool unrecognized = false)
    {
        if (fix is not (null or InvoiceListReferences.FixLost or InvoiceListReferences.FixArchived))
            throw new InvalidRequestException(
                $"Отбор списка счетов «{fix}» не известен: бывают «{InvoiceListReferences.FixLost}» и «{InvoiceListReferences.FixArchived}».");

        // Ссылки не на месте — на каждое чтение списка и с архивом: пометку несёт каждая строка, а
        // видно в строке только поставщика — счёт с удалённой позицией выглядел бы чистым.
        var marks = await InvoiceListMarks.ReadAsync(
            trouble, required: fix is not null, logs.CreateLogger(typeof(InvoiceEndpoints)), ct);

        var selected = db.Invoices.AsNoTracking();
        if (needsParsing) selected = Queue(selected);

        if (fix is not null)
        {
            // Подзапросом, а не сравнением с массивом: его база сворачивает в хеш, а сравнение исполняет
            // перебором на каждую строку — счетов с архивной записью бывают тысячи (замер в InvoiceTable).
            var named = fix == InvoiceListReferences.FixLost ? marks!.Fixable : marks!.Archived;
            var keys = db.Database.SqlQuery<Guid>($"SELECT unnest({named}) AS \"Value\"");
            selected = selected.Where(i => keys.Contains(i.Id));
            if (fix == InvoiceListReferences.FixArchived)
                selected = InvoiceReferenceTrouble.InWork(selected);
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
        if (fix is not null || needsParsing || unrecognized)
        {
            // Под отбором счетов бывают тысячи — «наведите порядок», а с issue #1292 и «Разобрать»: это
            // все черновики. Поэтому тем же подзапросом, каким отобран список, а не массивом их ключей:
            // массив база сверяла бы перебором (ревью PR #1241, #1302). Группировать ради отбора всю
            // таблицу тоже незачем (ревью PR #1259).
            counted = counted.Where(l => selected.Select(i => i.Id).Contains(l.InvoiceId));
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
        // «Сегодня» — один раз на список и в поясе компании: строки одного ответа считают сроки от одного дня.
        var today = await clock.TodayAsync(ct);
        var gaps = await ReadinessAsync(invoices, snapshots, periods, required: needsParsing,
            logs.CreateLogger(typeof(InvoiceListEndpoint)), ct);

        return TypedResults.Ok<IReadOnlyList<InvoiceListItem>>(
            [.. invoices.Select(i => InvoiceViews.Item(
                i,
                i.SupplierId is { } id && names.TryGetValue(id, out var supplier) ? supplier.DisplayName : null,
                i.SupplierId is { } key && names.TryGetValue(key, out var found) && found.Archived,
                lines.TryGetValue(i.Id, out var total) ? total.Count : 0,
                lines.TryGetValue(i.Id, out var waiting) ? waiting.Unmatched : 0,
                today) with
                {
                    References = marks?.Of(i),
                    Recognition = scans.GetValueOrDefault(i.Id),
                    Readiness = gaps?.GetValueOrDefault(i.Id),
                })]);
    }

    /// <summary>
    /// Очередь «Разобрать»: все недоведённые счета — те, что в состоянии «Черновик» (решение владельца
    /// 11.10.2026, issue #1292).
    ///
    /// <para>Раньше отбор шёл от строк без позиции номенклатуры, и черновик, у которого позиции
    /// подставлены, но не выбран плательщик или не сделана разноска, не стоял ни под одним отбором: в
    /// работе, где счёт передают из рук в руки, он терялся тихо.</para>
    ///
    /// <para>Отбирает БАЗА, по колонке состояния. Условия готовности в запрос не ложатся — «ссылки
    /// живы» знает ядро, «разнесён» считается с допуском из настроек, — а запрос, который их повторял
    /// бы, стал бы второй реализацией условий. Чего счёту не хватает, строка называет отдельно
    /// (<see cref="ReadinessAsync" />) — той же функцией, что открытый счёт. Индекса под отбор нет
    /// нарочно: на 21 тысяче счетов последовательный проход — 2,5 мс (замер в issue #1292).</para>
    ///
    /// <para>⚠️ «Черновик» и «перечень готовности не пуст» — не одно и то же, и оба расхождения
    /// оставлены в очереди осознанно. Черновик, у которого всё готово, а «Разобран» не нажат, не
    /// доведён: исключи его — он снова не стоял бы нигде. Оплаченный черновик, запертый закрытым
    /// периодом, разобрать нельзя, пока период не откроют, но отобрать замок запросом значило бы
    /// повторить в SQL правила периодов.</para>
    ///
    /// <para>ОТКЛОНЁННЫЙ счёт в очередь не входит по определению: «не платим» — значит и не разбираем
    /// (issue #1166).</para>
    /// </summary>
    internal static IQueryable<Invoice> Queue(IQueryable<Invoice> invoices) =>
        invoices.Where(i => i.State == InvoiceState.Draft);

    /// <summary>
    /// Чего не хватает до «Разобран» черновикам списка — пунктами, по счёту (issue #1292).
    ///
    /// <para>Считает <see cref="InvoiceReadiness" /> по снимку <see cref="InvoiceSnapshots" /> — тем же
    /// кодом, что ответ чтения счёта: причина в строке и перечень открытого счёта расходиться не могут.
    /// Снимок берётся пачкой: по одному запросу на список, а не на счёт.</para>
    ///
    /// <para>⚠️ <b>Общий список от причин не зависит</b>, как и от пометок ссылок: это вспомогательные
    /// данные, и отказ опроса ядра не должен отнимать у человека сами счета. Тогда причин нет ни у
    /// одной строки (<c>null</c>) — «не прочитано», а не «всё готово». Под отбором «Разобрать» иначе
    /// (<paramref name="required" />): причина там — то, за чем человек пришёл, и отказ остаётся
    /// отказом. Так же ошибка в самом расчёте не прячется за пустыми строками навсегда: под очередью
    /// она видна тем же отказом, каким ответит открытый счёт (ревью PR #1302).</para>
    ///
    /// <para>⚠️ Причины читаются не более чем у <see cref="ReadinessLimit" /> черновиков — тех, что
    /// правили последними. Список не страничный, а снимок поднимает строки и части разноски целиком:
    /// без предела цена открытия списка росла бы с числом черновиков, на каждое сохранение счёта и
    /// каждые пять секунд, пока читается чей-то скан. У остальных причин нет (<c>null</c>) — та же
    /// «не прочитано». В работе черновиков десятки; предел — страховка, а не режим.</para>
    /// </summary>
    private static async Task<Dictionary<Guid, IReadOnlyList<string>>?> ReadinessAsync(
        List<Invoice> invoices, InvoiceSnapshots snapshots, IModulePeriods periods, bool required, ILogger log,
        CancellationToken ct)
    {
        var drafts = invoices.Where(i => i.State == InvoiceState.Draft)
            .OrderByDescending(i => i.UpdatedAt).Take(ReadinessLimit).ToList();
        if (drafts.Count == 0) return [];

        try
        {
            var seen = await snapshots.OfAsync(drafts, ct);

            // Запертому закрытым периодом перечня нет и в открытом счёте (InvoiceViews): «Разобран» ему
            // откажет замок. Заперт бывает только оплаченный, и границы спрашиваются только ради них.
            var boundaries = drafts.Any(i => i.Payment == InvoicePaymentState.Paid)
                ? await periods.BoundariesAsync(ct)
                : null;

            return drafts
                .Where(i => boundaries is null || i.Payment != InvoicePaymentState.Paid
                    || ClosedPeriodGuard.LockOf(i, seen[i.Id].Parts, boundaries) is null)
                .ToDictionary(i => i.Id, i => (IReadOnlyList<string>)[.. seen[i.Id].Readiness.Gaps.Select(g => g.Label)]);
        }
        catch (Exception e) when (!required && e is not OperationCanceledException)
        {
            // Ошибкой, а не предупреждением: за этим может стоять и дефект расчёта, а не только
            // недоступное ядро, — и на экране его не видно ничем, кроме отсутствия строчки.
            log.LogError(e, "Список счетов отдан без причин «чего не хватает»: чтение готовности отказало");
            return null;
        }
    }

    /// <summary>У скольких черновиков списка читаются причины — см. <see cref="ReadinessAsync" />.</summary>
    internal const int ReadinessLimit = 200;

    /// <summary>
    /// Число у чипа «Разобрать» (<c>GET /api/costs/invoices/queues/parsing</c>) — тем же условием, каким
    /// отбирает список: число и строки под ним расходиться не могут.
    ///
    /// <para>Отдельным адресом, а не полем общего ответа чисел (ревью PR #1302): тот падает вместе с
    /// обратным опросом ядра и спрашивается только у того, кто счёт может править. А это число — счёт
    /// по своей таблице: ему нечем отказать, и нужно оно каждому, кто видит очередь.</para>
    /// </summary>
    public static async Task<Ok<InvoiceParsingCount>> ParsingCountAsync(CostsDbContext db, CancellationToken ct) =>
        TypedResults.Ok(new InvoiceParsingCount(await Queue(db.Invoices.AsNoTracking()).CountAsync(ct)));

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
        var organizations = await catalog.ListAsync(CostsDirectories.Organization, RecordsFor.Display, ct);
        return organizations?.ToDictionary(o => o.Id) ?? [];
    }
}
