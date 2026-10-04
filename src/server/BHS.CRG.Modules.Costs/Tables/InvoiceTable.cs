using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Tables;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>
/// Таблица «Счета на оплату» — зерно «счёт» (ТЗ CORE-33, COST-20.1; задача G1b, issue #1089).
///
/// <para><b>Таблица открыта модулем, суммы — правом на счета</b> (решение владельца 01.10.2026).
/// Ключ таблицы — код модуля: её видит всякий, кому открыт «Счета и накладные», в том числе с одним
/// правом на накладные. Деньги — <c>costs.invoice.read</c>: право на накладные не даёт права на счета
/// (ТЗ COST-29). Колонка суммы у того, кому её не положено, приходит С ПРИЧИНОЙ «нет права на суммы»,
/// а не исчезает.</para>
///
/// <para>⚠️ Это значит, что номер, поставщик и назначение счёта видны и без права на счета — решение
/// принято вслух, и закрывается оно здесь же: колонке достаточно назвать право.</para>
///
/// <para><b>Отбор по объекту меняет смысл суммы, и таблица это говорит</b> (ТЗ CORE-33, COST-20.1;
/// задача G1c, issue #1090). «Объект» — перечень строек и статей, на которые разнесён счёт; условие
/// по нему — «есть часть на этот объект». Под таким отбором «Сумма» показывает ДОЛЮ счёта по разноске
/// на названные объекты и приходит с подписью «доля: …»; полная сумма остаётся в «Сумма к оплате».
/// Без отбора по объекту обе суммы равны.</para>
///
/// <para><b>«Осталось дней» и «Просрочен» считаются от сегодня и не хранятся</b> (ТЗ COST-9.1, CORE-33;
/// задача G1c, issue #1090): источник считает их на чтении от срока «оплатить до» в поясе компании,
/// и по ним работают отбор и сортировка — правило одно на запрос и на клетку (<see cref="InvoiceDue" />).</para>
///
/// <para><b>«Реестр счетов» — готовое представление этой таблицы, а не отдельный отчёт</b> (ТЗ
/// COST-20.1; задача G4, issue #1097): колонки и их порядок — как в таблице, с которой заказчик
/// работает сегодня. С отметкой оплаты (C5, issue #1082) в нём есть дата платежа — «Оплачен», —
/// «Учётный период» и «Суммы по периодам»: счёт, чьи доли вошли в затраты разных месяцев, читается
/// как «40 000,00 (09.2026) + 60 000,00 (10.2026)».
/// ⚠️ ИТОГ суммы по учётному периоду реестр пока не считает: «Сумма» под отбором периода остаётся
/// суммой счёта, а отбор по ДАТЕ СЧЁТА называет свою ось подписью — такой итог с «Затратами по
/// стройке» (COST-20) не сходится и сходиться не должен. Ось периода по оплате — задача G4.</para>
/// </summary>
public static class InvoiceTable
{
    public const string Code = "invoices";

    /// <summary>
    /// Объекты разноски. Ключи двух колонок, за которыми НЕ стоит поле типа, — нарочно не похожие на
    /// ключ поля, которое заказчик допишет в тип: «Объект» и «Сумма» он допишет скорее всего, и поле с
    /// тем же ключом молча спряталось бы за системной колонкой.
    /// </summary>
    public const string ObjectsKey = "ОбъектыРазноски";

    /// <summary>Сумма по отбору: вся сумма счёта либо его доля на названные отбором объекты.</summary>
    public const string AmountKey = "СуммаПоОтбору";

    /// <summary>Сколько дней до срока оплаты; у просроченного счёта — отрицательное.</summary>
    public const string DaysLeftKey = "ДнейДоСрока";

    /// <summary>Срок оплаты прошёл, а счёт оплаты ещё ждёт.</summary>
    public const string OverdueKey = "СрокПросрочен";

    /// <summary>
    /// Сколько строк счёта стоит без позиции номенклатуры; пусто — ни одной. То же число, что счётчик
    /// в списке счетов: без колонки оно читается по одному счёту за раз — открывая каждый.
    ///
    /// <para>⚠️ Это ФАКТ о строках, а не очередь «Разобрать»: отклонённый счёт в очередь не входит
    /// (issue #1166), а строки без позиции у него остаются — и счётчик в списке счетов у него прежний.
    /// Очередь в реестре — «не пусто» вместе с «Состояние документа ≠ Отклонён»; что это одно и то же,
    /// держит тест.</para>
    /// </summary>
    public const string UnmatchedKey = "СтрокБезПозиции";

    /// <summary>
    /// Дата платежа; пусто — счёт не оплачен. ⚠️ Это день ПЛАТЕЖА, а не учётная дата: в затраты доли
    /// счёта входят каждая своим днём, по периоду своей стройки (ТЗ COST-16).
    /// </summary>
    public const string PaidOnKey = "ДатаПлатежа";

    /// <summary>
    /// Учётные месяцы оплаченного счёта — «09.2026»; у неоплаченного пусто. Перечень, потому что это
    /// поле НИЖНЕГО зерна: учётная дата — у доли разноски, и счёт на две стройки бывает в двух месяцах.
    /// Условие — «есть доля в этом месяце».
    /// </summary>
    public const string PeriodKey = "УчётныйПериод";

    /// <summary>
    /// Деньги счёта по учётным месяцам: «40 000,00 (09.2026) + 60 000,00 (10.2026)». Отдельной колонкой
    /// от месяцев — деньги закрыты правом на счета, а месяцы нет (ревизия Архитектора).
    ///
    /// <para>Под отбором, называющим период, — только названные месяцы: «что из этого счёта вошло в
    /// октябрь». Поэтому колонка объявлена зависящей от отбора — по ней не отбирают и не сортируют.</para>
    /// </summary>
    public const string PeriodSumsKey = "СуммыПоПериодам";

    /// <summary>Подпись «Сумм по периодам» под отбором, называющим период.</summary>
    public const string NamedPeriodsNote = "только периоды, названные отбором";

    /// <summary>
    /// Подпись денежных итогов под отбором по УЧЁТНОМУ периоду: отбор находит счёт по любому из его
    /// месяцев, а итог складывает счета целиком. Без оговорки он читается как «затраты месяца» — и со
    /// счётом на два месяца таким не является. Итог денег периода — задача G4 (#1097).
    /// </summary>
    public const string WholeInvoicesNote = "счета целиком, а не деньги названного периода";

    /// <summary>
    /// Подпись суммы под отбором по ДАТЕ СЧЁТА: чем период назван — и чем он НЕ является. Оси у реестра
    /// две — дата счёта и учётный период (C5); каждая называет себя своей подписью.
    /// </summary>
    public const string ByIssueDateNote = "период — по дате счёта, не по оплате";

    /// <summary>Код готового представления «Реестр счетов» (ТЗ COST-20.1).</summary>
    public const string RegistryView = "registry";

    private const string Amounts = "суммы";

    /// <summary>
    /// Денежные колонки, чей ИТОГ под отбором периода называет ось подписью. «Сумма» сюда не входит:
    /// её подпись длиннее — она же называет долю — и приходит ещё и к заголовку.
    /// </summary>
    internal static readonly IReadOnlyList<string> MoneyByIssueDate =
        [InvoiceRequisites.TotalKey, InvoiceRequisites.VatTotalKey];

    public static ModuleTable Declaration { get; } = new(
        Code,
        "Счета на оплату",
        "счёт",
        Requires: "costs",
        ModuleTableIsolation.None,
        "Отдаёт все счета экземпляра — всем, кому открыт модуль «Счета и накладные»; суммы — только с " +
        "правом «видеть счета на оплату»",
        [
            new(InvoiceRequisites.NumberKey, "Номер счёта", ModuleTableColumnKind.Text),
            new(InvoiceRequisites.DateKey, "Дата счёта", ModuleTableColumnKind.Date),
            new(InvoiceRequisites.SupplierKey, "Поставщик", ModuleTableColumnKind.Text),
            new(InvoiceRequisites.PayerKey, "Плательщик", ModuleTableColumnKind.Text),
            new(InvoiceRequisites.PurposeKey, "Назначение", ModuleTableColumnKind.Text),
            new(ObjectsKey, "Объект", ModuleTableColumnKind.List),
            new(AmountKey, "Сумма", ModuleTableColumnKind.Number, "costs.invoice.read", Amounts,
                DependsOnFilter: true),
            new(InvoiceRequisites.TotalKey, "Сумма к оплате", ModuleTableColumnKind.Number,
                "costs.invoice.read", Amounts),
            new(InvoiceRequisites.VatTotalKey, "В том числе НДС", ModuleTableColumnKind.Number,
                "costs.invoice.read", Amounts),
            new(InvoiceRequisites.ShippedOnKey, "Дата отгрузки", ModuleTableColumnKind.Date),
            new(InvoiceRequisites.DeferralKey, "Отсрочка, дней", ModuleTableColumnKind.Number),
            new(InvoiceRequisites.DueDateKey, "Оплатить до", ModuleTableColumnKind.Date),
            new(DaysLeftKey, "Осталось дней", ModuleTableColumnKind.Number),
            new(OverdueKey, "Просрочен", ModuleTableColumnKind.Boolean),
            new(UnmatchedKey, "Строк без позиции", ModuleTableColumnKind.Number),
            // Состояния — закрытые перечни: отбор по ним выбирают из списка, а слово вне перечня —
            // отказ, а не «таких счетов нет» (G1d, issue #1091).
            new(InvoiceRequisites.StateKey, "Состояние документа", ModuleTableColumnKind.Choice,
                Options: [.. Enum.GetValues<InvoiceState>().Select(InvoiceRequisites.Label)]),
            new(InvoiceRequisites.PaymentKey, "Состояние оплаты", ModuleTableColumnKind.Choice,
                Options: [.. Enum.GetValues<InvoicePaymentState>().Select(InvoiceRequisites.Label)]),
            new(PaidOnKey, "Оплачен", ModuleTableColumnKind.Date),
            new(PeriodKey, "Учётный период", ModuleTableColumnKind.List),
            new(PeriodSumsKey, "Суммы по периодам", ModuleTableColumnKind.Text, "costs.invoice.read", Amounts,
                DependsOnFilter: true),
        ],
        typeof(InvoiceTableRows),
        CostsRecordTypes.InvoiceCode,
        [
            // Порядок — как в реестре заказчика (ТЗ COST-20.1): контрагент, сумма, номер и дата,
            // отгрузка, отсрочка, оплатить до, осталось дней, оплата, объект, компания, назначение.
            //
            // «Сумма к оплате» стоит сразу за «Суммой» нарочно: под отбором по объекту первая
            // становится долей, и одинокая доля неотличима от полной суммы (ревизия Дизайнера).
            new(RegistryView, "Реестр счетов",
                [
                    InvoiceRequisites.SupplierKey, AmountKey, InvoiceRequisites.TotalKey,
                    InvoiceRequisites.NumberKey, InvoiceRequisites.DateKey, InvoiceRequisites.ShippedOnKey,
                    InvoiceRequisites.DeferralKey, InvoiceRequisites.DueDateKey, DaysLeftKey,
                    InvoiceRequisites.PaymentKey, PaidOnKey, PeriodKey, PeriodSumsKey, ObjectsKey,
                    InvoiceRequisites.PayerKey, InvoiceRequisites.PurposeKey, UnmatchedKey,
                ],
                Totals: [new(AmountKey, "sum"), new(InvoiceRequisites.TotalKey, "sum")],
                Pinned: 1,
                Filters:
                [
                    InvoiceRequisites.DateKey, InvoiceRequisites.PayerKey, InvoiceRequisites.SupplierKey,
                    ObjectsKey, InvoiceRequisites.PaymentKey, PeriodKey,
                ]),
        ]);
}

/// <summary>
/// Строки таблицы счетов. Поля, которые заказчик дописал в тип, лежат в <see cref="Invoice.Data" /> и
/// приходят теми же ключами — их колонки ядро берёт из схемы типа.
/// </summary>
public sealed class InvoiceTableRows(
    CostsDbContext db, IModuleCatalog catalog, AllocationPlacesSource places, IModuleClock clock)
    : IModuleTableRows
{
    private static readonly IReadOnlyDictionary<InvoiceState, string> States =
        Enum.GetValues<InvoiceState>().ToDictionary(s => s, InvoiceRequisites.Label);

    private static readonly IReadOnlyDictionary<InvoicePaymentState, string> Payments =
        Enum.GetValues<InvoicePaymentState>().ToDictionary(p => p, InvoiceRequisites.Label);

    public async Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct)
    {
        // Названия организаций — одним списком: вида «Организация» на чистой установке может не быть
        // вовсе (см. InvoiceEndpoints.SupplierNamesAsync). Нужны и строкам, и отбору по названию.
        var names = (await catalog.ListAsync(CostsRecordTypes.OrganizationCode, ct))
            ?.ToDictionary(o => o.Id, o => o.DisplayName) ?? [];

        // Объекты разноски — стройки и статьи вне строек одним списком названий: цель части — ровно
        // одно из двух. Раздел стройки в перечень не идёт: отбор «по стройке» — по стройке целиком.
        var known = await places.LoadAsync(ct);
        var shares = new InvoiceShares(known.Sites.Select(s => (s.Id, s.Name))
            .Concat(known.Articles.Select(a => (a.Id, a.Name)))
            .ToDictionary(o => o.Id, o => o.Name));

        // «Сегодня» — одно на весь ответ: и отбору, и клеткам. Спроси мы его дважды, запрос на
        // границе суток отобрал бы «просроченные» по вчерашнему дню, а признак показал бы по сегодняшнему.
        var today = await clock.TodayAsync(ct);

        var sql = Sql(names, shares.Labels, InvoicePeriods.Labels(today), today);
        var selected = sql.Where(db.Invoices.AsNoTracking(), query.Filter);

        // Одна строка по ключу — тот же отбор и ещё одно условие: счёт вне отбора не приходит. Ключ,
        // который не разбирается, — «такого счёта нет», а не все счета.
        if (query.Row is { } key)
            selected = Guid.TryParse(key, out var only) ? selected.Where(i => i.Id == only) : selected.Where(_ => false);

        // Отбор НАЗЫВАЕТ объекты — «Сумма» становится долей счёта на них (ТЗ CORE-33). Иначе это сумма
        // счёта целиком, и считает её запрос, как любую числовую колонку.
        var naming = TableFilters.Naming(query.Filter, InvoiceTable.ObjectsKey);
        // Отбор называет период ДАТОЙ СЧЁТА — одна из двух осей; вторая, учётный период, — ниже.
        var byIssueDate = TableFilters.Naming(query.Filter, InvoiceRequisites.DateKey).Count > 0;
        var shareTotal = naming.Count > 0 && query.Totals?.ContainsKey(InvoiceTable.AmountKey) == true;
        var shareCells = naming.Count > 0 && query.Columns.Contains(InvoiceTable.AmountKey);

        // Итог и число строк — по всему отбору, ДО страницы (ТЗ CORE-33).
        var count = await selected.CountAsync(ct);
        var totals = new Dictionary<string, TableTotal>(await sql.TotalsAsync(selected,
            shareTotal ? query.Totals!.Where(t => t.Key != InvoiceTable.AmountKey).ToDictionary() : query.Totals,
            ct), StringComparer.Ordinal);

        // Порядок по умолчанию — свежие сверху; он же довершает любую сортировку, иначе строки с
        // равными значениями менялись бы местами от страницы к странице.
        IQueryable<Invoice> page = (sql.OrderBy(selected, query.Sort)?.ThenByDescending(i => i.IssuedOn)
                                    ?? selected.OrderByDescending(i => i.IssuedOn))
            .ThenByDescending(i => i.CreatedAt)
            .ThenBy(i => i.Id);
        if (query.Offset > 0) page = page.Skip(query.Offset);
        if (query.Limit is { } limit) page = page.Take(limit);

        var invoices = await page.ToListAsync(ct);

        // Разноску читаем по счетам СТРАНИЦЫ; по всему отбору — только ради итога доли: он обязан
        // считаться по всему отбору, а посчитать долю запросом нельзя (см. InvoiceShares).
        var ids = invoices.Select(i => i.Id).ToList();
        var scope = shareTotal ? selected : db.Invoices.AsNoTracking().Where(i => ids.Contains(i.Id));
        var partsRead = shareTotal || shareCells || query.Columns.Contains(InvoiceTable.ObjectsKey);
        var parts = partsRead
            ? await db.InvoiceAllocations.AsNoTracking()
                .Where(a => scope.Select(i => i.Id).Contains(a.InvoiceId)).ToListAsync(ct)
            : [];
        var amounts = shareTotal || shareCells
            ? await InvoiceShares.ReadAsync(db, scope, parts, p => naming.Any(c => c.Matches(shares.Label(p))), ct)
            : null;
        if (shareTotal) totals[InvoiceTable.AmountKey] = InvoiceShares.Total(amounts!.Values);

        // Что сумма значит под этим отбором — колонке «Сумма» (у неё меняется и смысл клетки) и под
        // КАЖДЫМ денежным итогом. Ось периода — свойство отбора, а не одной колонки: под отбором по
        // дате счёта итог «Суммы к оплате» — тоже «за счета, выставленные в периоде», и без оговорки
        // он читается как то, что сходится с затратами по стройке.
        var note = shares.Note(naming, byIssueDate);
        Annotate(totals, InvoiceTable.AmountKey, note);
        if (byIssueDate)
            foreach (var money in InvoiceTable.MoneyByIssueDate) Annotate(totals, money, InvoiceTable.ByIssueDateNote);

        // Счётчик «ждут позиции» — по счетам страницы, одним запросом; условие то же, что у колонки
        // в запросе (см. Sql).
        var unmatched = query.Columns.Contains(InvoiceTable.UnmatchedKey)
            ? await db.InvoiceLines.AsNoTracking()
                .Where(l => ids.Contains(l.InvoiceId) && l.NomenclatureId == null)
                .GroupBy(l => l.InvoiceId).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Key, g => g.Count, ct)
            : [];

        // Учётные месяцы — по оплаченным счетам страницы и той же функцией, что у формы счёта. Доли,
        // уже прочитанные ради «Объекта» или доли суммы, второй раз не читаем.
        var periods = query.Columns.Contains(InvoiceTable.PeriodKey) || query.Columns.Contains(InvoiceTable.PeriodSumsKey)
            ? await InvoicePeriods.ReadAsync(db, invoices, partsRead ? parts : null,
                naming.Count > 0 ? p => naming.Any(c => c.Matches(shares.Label(p))) : null, ct)
            : InvoicePeriods.None;
        // Отбор НАЗЫВАЕТ период — «Суммы по периодам» показывают только названные месяцы.
        var months = TableFilters.Naming(query.Filter, InvoiceTable.PeriodKey);

        // Под отбором по учётному периоду итоги — по-прежнему счета целиком, и это сказано под каждым.
        if (months.Count > 0)
            foreach (var money in InvoiceTable.MoneyByIssueDate.Append(InvoiceTable.AmountKey))
                Annotate(totals, money, Joined(totals.GetValueOrDefault(money)?.Note, InvoiceTable.WholeInvoicesNote));

        // «Суммы по периодам» сужают оба отбора: по объекту — до долей на него, по периоду — до месяцев.
        var sums = Joined(naming.Count > 0 ? shares.Note(naming) : null, months.Count > 0 ? InvoiceTable.NamedPeriodsNote : null);
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (note is not null) notes[InvoiceTable.AmountKey] = note;
        if (sums is not null) notes[InvoiceTable.PeriodSumsKey] = sums;

        var objects = shares.Objects(parts);
        return new(
            [.. invoices.Select(i => Row(i, names, query.Columns, objects, amounts, unmatched, today,
                periods.GetValueOrDefault(i.Id), months))],
            count, totals, notes.Count == 0 ? null : notes,
            [.. invoices.Select(i => i.Id.ToString())]);
    }

    /// <summary>Две подписи одной — через «;»; пустые пропускаются.</summary>
    private static string? Joined(string? first, string? second) =>
        (first, second) switch { (null, _) => second, (_, null) => first, _ => $"{first}; {second}" };

    /// <summary>Подпись под итогом колонки — если итог по ней считался.</summary>
    private static void Annotate(Dictionary<string, TableTotal> totals, string key, string? note)
    {
        if (note is not null && totals.TryGetValue(key, out var total)) totals[key] = total with { Note = note };
    }

    /// <summary>
    /// Где лежит каждая колонка таблицы. Описаны ВСЕ объявленные — это проверяет сам построитель;
    /// остальное — поля, которые заказчик дописал в тип, они лежат в <see cref="Invoice.Data" />.
    /// </summary>
    private TableSql<Invoice> Sql(
        Dictionary<Guid, string> names, IReadOnlyDictionary<Guid, string> objects,
        IReadOnlyDictionary<int, string> months, DateOnly today) =>
        TableSql<Invoice>.Describe(InvoiceTable.Declaration, sql => sql
            .Text(InvoiceRequisites.NumberKey, i => i.Number)
            .Date(InvoiceRequisites.DateKey, i => i.IssuedOn)
            .Lookup(InvoiceRequisites.SupplierKey, i => i.SupplierId, names)
            .Lookup(InvoiceRequisites.PayerKey, i => i.PayerId, names)
            .Text(InvoiceRequisites.PurposeKey, i => i.Purpose)
            // Условие по дочернему зерну: в базе это EXISTS по частям разноски счёта.
            .List(InvoiceTable.ObjectsKey,
                i => db.InvoiceAllocations.Where(a => a.InvoiceId == i.Id).Select(a => a.ConstructionId ?? a.ArticleId),
                objects, InvoiceShares.Lost)
            // Без отбора по объекту «Сумма» — сумма счёта, и итог по ней считает запрос. Отбирать и
            // сортировать по ней ядро не даёт: колонка объявлена зависящей от отбора.
            .Number(InvoiceTable.AmountKey, i => i.Total)
            .Number(InvoiceRequisites.TotalKey, i => i.Total)
            .Number(InvoiceRequisites.VatTotalKey, i => i.VatTotal)
            .Date(InvoiceRequisites.ShippedOnKey, i => i.ShippedOn)
            .Number(InvoiceRequisites.DeferralKey, i => i.DeferralDays)
            .Date(InvoiceRequisites.DueDateKey, i => i.DueDate)
            .Number(InvoiceTable.DaysLeftKey, InvoiceDue.DaysLeft(today))
            .Flag(InvoiceTable.OverdueKey, InvoiceDue.Overdue(today))
            // Ноль — пусто, а не «0»: у разобранного счёта клетка молчит, и отбор «не пусто» отдаёт
            // счета, где разбирать есть что. Подзапрос ОДИН и сам даёт NULL, когда строк нет: построитель
            // подставляет выражение в запрос по нескольку раз (отбор, сортировка — дважды, итог), и
            // «count = 0 ? null : count» удваивало бы каждое из них (ревью PR #1177).
            .Number(InvoiceTable.UnmatchedKey, i => db.InvoiceLines
                .Where(l => l.InvoiceId == i.Id && l.NomenclatureId == null)
                .GroupBy(l => l.InvoiceId).Select(g => (decimal?)g.Count()).FirstOrDefault())
            .Choice(InvoiceRequisites.StateKey, i => (InvoiceState?)i.State, States)
            .Choice(InvoiceRequisites.PaymentKey, i => (InvoicePaymentState?)i.Payment, Payments)
            .Date(InvoiceTable.PaidOnKey, i => i.PaidOn)
            // Учётные месяцы — у долей и у остатка: остаток лежит в самом счёте, поэтому перечень —
            // объединение двух подзапросов. По ключу, а не по названию: «09.2026» раньше «01.2027».
            .List(InvoiceTable.PeriodKey,
                i => db.InvoiceAllocations
                    .Where(a => a.InvoiceId == i.Id && a.AccountingOn != null)
                    .Select(a => (int?)(a.AccountingOn!.Value.Year * 100 + a.AccountingOn!.Value.Month))
                    .Concat(db.Invoices
                        .Where(o => o.Id == i.Id && o.RemainderAccountingOn != null)
                        .Select(o => (int?)(o.RemainderAccountingOn!.Value.Year * 100 + o.RemainderAccountingOn!.Value.Month))),
                months, InvoicePeriods.Unknown, byKey: true)
            // Клетку собирает служба строк; запросу тут считать нечего — по колонке не отбирают.
            .Text(InvoiceTable.PeriodSumsKey, i => null)
            .Fields(key => i => i.Data.RootElement.GetProperty(key).GetString()));

    /// <param name="amounts">Доли счетов на названные отбором объекты; null — отбор объектов не называет,
    /// и «Сумма» — сумма счёта целиком.</param>
    private static IReadOnlyDictionary<string, object?> Row(
        Invoice invoice, Dictionary<Guid, string> names, IReadOnlySet<string> open,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> objects, IReadOnlyDictionary<Guid, decimal?>? amounts,
        Dictionary<Guid, int> unmatched, DateOnly today, InvoiceMonths? periods,
        IReadOnlyList<TableFilterCondition> named)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [InvoiceRequisites.NumberKey] = invoice.Number,
            [InvoiceRequisites.DateKey] = invoice.IssuedOn,
            [InvoiceRequisites.SupplierKey] = Name(invoice.SupplierId, names),
            [InvoiceRequisites.PayerKey] = Name(invoice.PayerId, names),
            [InvoiceRequisites.PurposeKey] = invoice.Purpose,
            [InvoiceTable.ObjectsKey] = objects.GetValueOrDefault(invoice.Id) ?? [],
            [InvoiceRequisites.ShippedOnKey] = invoice.ShippedOn,
            [InvoiceRequisites.DeferralKey] = invoice.DeferralDays is { } days ? (decimal)days : null,
            [InvoiceRequisites.DueDateKey] = invoice.DueDate,
            [InvoiceRequisites.StateKey] = InvoiceRequisites.Label(invoice.State),
            [InvoiceRequisites.PaymentKey] = InvoiceRequisites.Label(invoice.Payment),
            [InvoiceTable.PaidOnKey] = invoice.PaidOn,
            [InvoiceTable.PeriodKey] = InvoicePeriods.Cell(periods?.All ?? []),
        };

        // Деньги и срок — только открытые: закрытое ядро всё равно вычистит, но не считать его дешевле,
        // чем считать и выбрасывать.
        if (open.Contains(InvoiceRequisites.TotalKey)) row[InvoiceRequisites.TotalKey] = invoice.Total;
        if (open.Contains(InvoiceRequisites.VatTotalKey)) row[InvoiceRequisites.VatTotalKey] = invoice.VatTotal;
        if (open.Contains(InvoiceTable.AmountKey))
            row[InvoiceTable.AmountKey] = amounts is null ? invoice.Total : amounts.GetValueOrDefault(invoice.Id);

        if (open.Contains(InvoiceTable.PeriodSumsKey))
            row[InvoiceTable.PeriodSumsKey] = InvoicePeriods.Sums(periods?.Named ?? [], named);

        if (open.Contains(InvoiceTable.DaysLeftKey)) row[InvoiceTable.DaysLeftKey] = InvoiceDue.DaysLeftOf(invoice, today);
        if (open.Contains(InvoiceTable.OverdueKey)) row[InvoiceTable.OverdueKey] = InvoiceDue.OverdueOf(invoice, today);
        if (open.Contains(InvoiceTable.UnmatchedKey))
            row[InvoiceTable.UnmatchedKey] = unmatched.TryGetValue(invoice.Id, out var waiting) ? (decimal)waiting : null;

        foreach (var field in invoice.Data.RootElement.EnumerateObject())
            if (!row.ContainsKey(field.Name)) row[field.Name] = Scalar(field.Value);

        return row;
    }

    /// <summary>Ссылка на организацию, которой нет в справочнике, — пусто, а не идентификатор.</summary>
    private static string? Name(Guid? id, Dictionary<Guid, string> names) =>
        id is { } value && names.TryGetValue(value, out var name) ? name : null;

    /// <summary>Скаляр поля заказчика; составное в клетку не ложится — его колонки ядро и не объявит.</summary>
    private static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetDecimal(out var number) ? number : value.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
