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
/// Под отбором по УЧЁТНОМУ периоду «Сумма» — деньги счёта, вошедшие в названные месяцы, в клетке и в
/// итоге; «Сумма к оплате» и НДС остаются счетами целиком и говорят это подписью. Отбор по ДАТЕ СЧЁТА
/// суммы не сужает и называет свою ось подписью — такой итог с «Затратами по стройке» (COST-20) не
/// сходится и сходиться не должен.</para>
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

    /// <summary>
    /// Разделы строек, на которые разнесён счёт, — «Комарова 36 / 4 эт.»; доля на стройку целиком —
    /// «Комарова 36 / без раздела» (задача G5b, issue #1198). Условие — «есть часть на этот раздел», и
    /// «Сумму» оно сужает так же, как условие по объекту: до долей на названные разделы.
    /// </summary>
    public const string SectionsKey = "РазделыРазноски";

    /// <summary>
    /// Сумма по отбору: вся сумма счёта — либо то, что из неё отбор назвал: доля на названные объекты,
    /// деньги названных учётных месяцев, доля объекта в этих месяцах.
    /// </summary>
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
    /// Подпись итогов «Суммы к оплате» и НДС под отбором по УЧЁТНОМУ периоду: отбор находит счёт по
    /// любому из его месяцев, а эти итоги складывают счета целиком. Без оговорки они читаются как
    /// «затраты месяца» — и со счётом на два месяца такими не являются. Деньги названных месяцев — в
    /// колонке «Сумма» (задача G4, issue #1097): она под таким отбором сужается, как под отбором по объекту.
    /// </summary>
    public const string WholeInvoicesNote = "счета целиком, а не деньги названного периода";

    /// <summary>
    /// Куда идти за затратами периода. Отдельного переключателя «оси» у реестра нет (решение владельца
    /// 05.10.2026, ревизия Архитектора): ось — колонка, на которой стоит условие, и подпись называет
    /// нужную по имени. «Не по оплате» вело бы человека к колонке «Оплачен» — а это третья ось.
    /// </summary>
    private const string CostsPath = "затраты периода — отбор «Учётный период»";

    /// <summary>
    /// Подпись суммы под отбором по ДАТЕ СЧЁТА: чем период назван — и где то, чем он не является.
    /// Итог под таким отбором — счета, выставленные в периоде; с затратами он не сходится.
    /// </summary>
    public const string ByIssueDateNote = "период — по дате счёта; " + CostsPath;

    /// <summary>
    /// Подпись суммы под отбором по ДНЮ ПЛАТЕЖА («Оплачен»). Ось, больше всех похожая на «затраты за
    /// период» — и с ними не сходящаяся: счёт идёт целиком, а доли, перенесённые закрытием периода,
    /// лежат в другом месяце.
    /// </summary>
    public const string ByPaidOnNote = "период — по дню платежа; " + CostsPath;

    /// <summary>Отбор назвал период и датой счёта, и днём платежа.</summary>
    public const string ByIssueDateAndPaidOnNote = "период — по дате счёта и дню платежа; " + CostsPath;

    /// <summary>Подпись оси периода; null — ни датой счёта, ни днём платежа отбор период не называет.</summary>
    internal static string? AxisNote(bool byIssueDate, bool byPaidOn) => (byIssueDate, byPaidOn) switch
    {
        (true, true) => ByIssueDateAndPaidOnNote,
        (true, false) => ByIssueDateNote,
        (false, true) => ByPaidOnNote,
        _ => null,
    };

    /// <summary>Код готового представления «Реестр счетов» (ТЗ COST-20.1).</summary>
    public const string RegistryView = "registry";

    private const string Amounts = "суммы";

    /// <summary>
    /// Денежные колонки, которые отбор НЕ сужает: под любым отбором это счёт целиком. Поэтому их ИТОГ
    /// под отбором периода подписывается — осью «по дате счёта» либо словами «счета целиком» под
    /// учётным периодом. «Сумма» сюда не входит: она сужается сама и подпись у неё своя.
    /// </summary>
    internal static readonly IReadOnlyList<string> WholeInvoiceMoney =
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
            new(SectionsKey, "Раздел", ModuleTableColumnKind.List),
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
            //
            // «Раздел» в реестре — ОТБОРОМ, а не колонкой (G5b, issue #1198): колонки здесь — таблица
            // заказчика, и раздела в ней нет. Ссылка отчёта отбирает по нему и без колонки, а что именно
            // названо, говорит подпись «Суммы»; колонку человек включает сам.
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
                    ObjectsKey, SectionsKey, InvoiceRequisites.PaymentKey, PeriodKey,
                ]),
        ],
        InvoiceBreakdown.Declaration);
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
        // одно из двух. Раздел стройки — своей колонкой: отбор «по стройке» — по стройке целиком.
        var shares = InvoiceShares.Of(await places.LoadAsync(ct));

        // «Сегодня» — одно на весь ответ: и отбору, и клеткам. Спроси мы его дважды, запрос на
        // границе суток отобрал бы «просроченные» по вчерашнему дню, а признак показал бы по сегодняшнему.
        var today = await clock.TodayAsync(ct);

        var calendar = InvoicePeriods.Labels(today);
        var sql = Sql(names, shares, calendar, today);
        var selected = sql.Where(db.Invoices.AsNoTracking(), query.Filter);

        // Одна строка по ключу — тот же отбор и ещё одно условие: счёт вне отбора не приходит. Ключ,
        // который не разбирается, — «такого счёта нет», а не все счета.
        if (query.Row is { } key)
            selected = Guid.TryParse(key, out var only) ? selected.Where(i => i.Id == only) : selected.Where(_ => false);

        // Отбор НАЗЫВАЕТ объекты — «Сумма» становится долей счёта на них (ТЗ CORE-33); называет учётный
        // период — деньгами счёта, вошедшими в названные месяцы (G4); то и другое — долями на объект в
        // эти месяцы. Иначе это сумма счёта целиком, и считает её запрос, как любую числовую колонку.
        var naming = TableFilters.Naming(query.Filter, InvoiceTable.ObjectsKey);
        // Раздел сужает сумму так же, как объект: до долей на названные разделы (G5b, issue #1198).
        var sections = TableFilters.Naming(query.Filter, InvoiceTable.SectionsKey);
        var months = TableFilters.Naming(query.Filter, InvoiceTable.PeriodKey);
        // Отбор называет период ДАТОЙ СЧЁТА — вторая ось; под ней сумма не сужается, а подписывается.
        var byIssueDate = TableFilters.Naming(query.Filter, InvoiceRequisites.DateKey).Count > 0;
        var byPaidOn = TableFilters.Naming(query.Filter, InvoiceTable.PaidOnKey).Count > 0;
        var narrowed = naming.Count > 0 || sections.Count > 0 || months.Count > 0;
        var shareTotal = narrowed && query.Totals?.ContainsKey(InvoiceTable.AmountKey) == true;
        var shareCells = narrowed && query.Columns.Contains(InvoiceTable.AmountKey);

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

        // Какие деньги счёта отбор назвал: долю спрашиваем ВМЕСТЕ с её месяцем, а не двумя списками —
        // под «(объект А и 09) или (объект Б и 10)» названы две пары, а не А и Б в обоих месяцах.
        // Раздел — третье в той же связке: «объект + раздел + месяц» спрашивают у ОДНОЙ доли.
        // У остатка объекта нет: условие по объекту его не пропускает. Дня нет у доли неоплаченного
        // счёта — тогда о периоде не спрашиваем вовсе: условия по нему долю не отсеивают.
        bool Admitted(InvoiceAllocation? part, DateOnly? day)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [InvoiceTable.ObjectsKey] = part is null ? null : shares.Label(part),
                [InvoiceTable.SectionsKey] = part is null ? null : shares.Section(part),
            };
            if (day is { } on) values[InvoiceTable.PeriodKey] = InvoicePeriods.Label(calendar, on);
            return TableFilters.Admits(query.Filter, values);
        }
        var byPeriod = months.Count > 0;
        bool IsNamed(PostedMoney m) => InvoiceMoney.IsNamed(m, Admitted, byPeriod);

        // Деньги счетов по частям — ОДНИМ проходом на всё, что из них складывается: «Сумму» под отбором,
        // учётные месяцы и расшифровку строки (см. InvoiceMoney). По счетам СТРАНИЦЫ; по всему отбору —
        // только ради итога «Суммы»: он обязан считаться по всему отбору, а запросом доля не считается.
        var ids = invoices.Select(i => i.Id).ToList();
        var scope = shareTotal ? selected : db.Invoices.AsNoTracking().Where(i => ids.Contains(i.Id));
        var moneyRead = shareTotal || shareCells || query.Row is not null
                        || query.Columns.Contains(InvoiceTable.PeriodKey) || query.Columns.Contains(InvoiceTable.PeriodSumsKey);
        var parts = moneyRead || query.Columns.Contains(InvoiceTable.ObjectsKey) || query.Columns.Contains(InvoiceTable.SectionsKey)
            ? await db.InvoiceAllocations.AsNoTracking()
                .Where(a => scope.Select(i => i.Id).Contains(a.InvoiceId)).ToListAsync(ct)
            : [];
        // Неоплаченные счета деньгам нужны не всегда: учётного дня у них нет, и там, где спрашивают
        // только про месяцы, читать их строки и считать разноску незачем. Нужны они расшифровке строки и
        // «Сумме» под отбором по объекту — доля на объект есть и у неоплаченного счёта.
        var unpaidToo = query.Row is not null || ((shareTotal || shareCells) && !byPeriod);
        var owners = unpaidToo ? scope : scope.Where(i => i.Payment == InvoicePaymentState.Paid);
        var money = moneyRead
            ? await InvoiceMoney.ReadAsync(db,
                shareTotal
                    ? await InvoiceMoney.HeadsAsync(owners, ct)
                    : [.. InvoiceMoney.Heads(invoices).Where(h => unpaidToo || h.Paid)],
                shareTotal ? owners.Select(i => i.Id) : null, parts, ct)
            : InvoiceMoney.None;
        // Счёта в деньгах может не быть: неоплаченный не читали, а под итогом деньги читаются ВТОРЫМ
        // запросом по всему отбору — счёт страницы за это время могли удалить или увести из отбора.
        // Пустая клетка, а не отказ всему реестру (ревью PR #1199).
        IReadOnlyList<PostedMoney> MoneyOf(Guid id) => money.GetValueOrDefault(id) ?? [];

        // «Сумма» под сужающим отбором — названные деньги счёта; иначе — счёт целиком, из самой записи.
        var amounts = shareTotal || shareCells
            ? money.ToDictionary(m => m.Key, m => InvoiceMoney.Named(m.Value, IsNamed))
            : null;
        if (shareTotal) totals[InvoiceTable.AmountKey] = InvoiceShares.Total(amounts!.Values);

        // Учётные месяцы — из тех же денег и той же функцией, что у формы счёта.
        var periods = query.Columns.Contains(InvoiceTable.PeriodKey) || query.Columns.Contains(InvoiceTable.PeriodSumsKey)
            ? invoices.ToDictionary(i => i.Id, i => InvoicePeriods.Of(MoneyOf(i.Id), narrowed ? IsNamed : null))
            : InvoicePeriods.None;

        // Чем сужены деньги: по объекту — до долей на него, по разделу — до долей на раздел, по учётному
        // периоду — до названных месяцев. Подпись одна на «Сумму» и на её расшифровку, «Суммы по
        // периодам»: сужены они одинаково.
        var sums = Joined(
            Joined(naming.Count > 0 ? shares.Note(naming) : null, sections.Count > 0 ? shares.SectionNote(sections) : null),
            months.Count > 0 ? InvoiceTable.NamedPeriodsNote : null);

        // Что сумма значит под этим отбором — колонке «Сумма» (у неё меняется и смысл клетки) и под
        // КАЖДЫМ денежным итогом. Ось периода — свойство отбора, а не одной колонки: под отбором по
        // дате счёта итог «Суммы к оплате» — тоже «за счета, выставленные в периоде», и без оговорки
        // он читается как то, что сходится с затратами по стройке.
        //
        // Оговорка оси — дата счёта, день платежа — только пока учётный период отбором НЕ назван: с ним
        // «Сумма» сужена именно по учётному периоду, и оговорка спорила бы с соседней подписью
        // (ревизия Архитектора). «Сумма к оплате» и НДС под учётным периодом — счета целиком.
        var axis = months.Count == 0 ? InvoiceTable.AxisNote(byIssueDate, byPaidOn) : null;
        var note = Joined(sums, axis);
        Annotate(totals, InvoiceTable.AmountKey, note);
        foreach (var whole in InvoiceTable.WholeInvoiceMoney)
            Annotate(totals, whole, months.Count > 0 ? InvoiceTable.WholeInvoicesNote : axis);

        // Счётчик «ждут позиции» — по счетам страницы, одним запросом; условие то же, что у колонки
        // в запросе (см. Sql).
        var unmatched = query.Columns.Contains(InvoiceTable.UnmatchedKey)
            ? await db.InvoiceLines.AsNoTracking()
                .Where(l => ids.Contains(l.InvoiceId) && l.NomenclatureId == null)
                .GroupBy(l => l.InvoiceId).Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Key, g => g.Count, ct)
            : [];

        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (note is not null) notes[InvoiceTable.AmountKey] = note;
        if (sums is not null) notes[InvoiceTable.PeriodSumsKey] = sums;

        // Одна строка по ключу — с расшифровкой: счёт по объектам и учётным месяцам (боковая панель).
        var breakdown = query.Row is not null && invoices is [var opened]
            ? InvoiceBreakdown.Of(opened, MoneyOf(opened.Id), shares, IsNamed, narrowed)
            : null;

        var objects = shares.Objects(parts);
        var listed = shares.SectionsOf(parts);
        return new(
            [.. invoices.Select(i => Row(i, names, query.Columns, objects, listed, amounts, unmatched, today,
                periods.GetValueOrDefault(i.Id)))],
            count, totals, notes.Count == 0 ? null : notes,
            [.. invoices.Select(i => i.Id.ToString())], breakdown);
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
        Dictionary<Guid, string> names, InvoiceShares shares,
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
                shares.Labels, InvoiceShares.Lost)
            // Раздел доли — то же выражение, что InvoiceShares.SectionKey: раздел либо, у доли на стройку
            // целиком, сама стройка («без раздела»). Доли на статьи вне строек в перечень не идут —
            // отсеяны ЗДЕСЬ, а не пустым ключом: пустой ключ построитель счёл бы «разделом, которого нет».
            .List(InvoiceTable.SectionsKey,
                i => db.InvoiceAllocations.Where(a => a.InvoiceId == i.Id && a.ConstructionId != null)
                    .Select(a => a.SectionId ?? a.ConstructionId),
                shares.Sections, InvoiceShares.LostSection)
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

    /// <param name="amounts">Деньги счетов под сужающим отбором — доли на названные объекты, деньги
    /// названных месяцев; null — отбор не называет ни того, ни другого, и «Сумма» — сумма счёта целиком.</param>
    private static IReadOnlyDictionary<string, object?> Row(
        Invoice invoice, Dictionary<Guid, string> names, IReadOnlySet<string> open,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> objects,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> sections, IReadOnlyDictionary<Guid, decimal?>? amounts,
        Dictionary<Guid, int> unmatched, DateOnly today, InvoiceMonths? periods)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [InvoiceRequisites.NumberKey] = invoice.Number,
            [InvoiceRequisites.DateKey] = invoice.IssuedOn,
            [InvoiceRequisites.SupplierKey] = Name(invoice.SupplierId, names),
            [InvoiceRequisites.PayerKey] = Name(invoice.PayerId, names),
            [InvoiceRequisites.PurposeKey] = invoice.Purpose,
            [InvoiceTable.ObjectsKey] = objects.GetValueOrDefault(invoice.Id) ?? [],
            [InvoiceTable.SectionsKey] = sections.GetValueOrDefault(invoice.Id) ?? [],
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
            row[InvoiceTable.PeriodSumsKey] = InvoicePeriods.Sums(periods?.Named ?? []);

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
