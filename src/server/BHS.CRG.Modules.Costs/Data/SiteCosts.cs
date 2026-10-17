namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Число отчёта: сколько счетов и сколько денег.</summary>
/// <param name="Corrections">Из них — корректировки закрытых периодов (PR 6d E2): строки книги, легшие днём
/// исправления, а не своим днём. Бывают только во взгляде «на момент закрытия»; null — таких нет либо
/// взгляд «с исправлениями», где их нет по устройству.</param>
public sealed record CostFigure(int Invoices, decimal Amount, CostCorrections? Corrections = null)
{
    /// <summary>
    /// Те же счета и деньги — без корректировок: реестр их не знает, и число с ними под ссылкой ему всё равно
    /// равно. Сравни записи целиком — стрелка пропала бы у каждого числа с корректировкой.
    /// </summary>
    public bool Same(CostFigure? other) => other is not null && Invoices == other.Invoices && Amount == other.Amount;
}

/// <summary>
/// Корректировки закрытых периодов в числе отчёта (PR 6d E2, разбор Дизайнера п. 7 «Месяц, где лежит разница»).
/// </summary>
/// <param name="Invoices">Счета, чьи корректировки здесь не ноль, — тем же правилом «ноль не считается», что у
/// строки. Счёт считается и тогда, когда сама строка его не считает: свои +30 и корректировка −30 одного
/// счёта дают строке ноль, а корректировка в ней всё равно есть (решение владельца 17.10.2026 — правило
/// строки не меняется).</param>
/// <param name="Whole">Число целиком из корректировок: ни у одного счёта в нём нет своих денег. Клиенту из
/// чисел этого не вывести — один счёт бывает и в своих деньгах строки, и в корректировке.</param>
public sealed record CostCorrections(int Invoices, decimal Amount, bool Whole);

/// <summary>Строка отчёта — стройка, статья вне строек или контрагент.</summary>
/// <param name="Id">Чья строка; null — строка не про одну запись: «поставщик не указан», удалённые
/// объекты или поставщики одной строкой.</param>
/// <param name="Linked">Есть ли у реестра отбор, под которым его итог равен этому числу. Нет — стрелки
/// у строки нет: ссылка, по которой цифры не сходятся, хуже её отсутствия.</param>
/// <param name="Registry">Как эту строку зовёт реестр, если иначе, чем отчёт: раздел в отчёте стройки —
/// «4 эт.», а колонка «Раздел» реестра — «Комарова 36 / 4 эт.». Отбор ссылки берёт это название.</param>
/// <param name="Corrections">Из них — корректировки закрытых периодов (<see cref="CostFigure.Corrections" />).</param>
public sealed record CostLine(
    Guid? Id, string Name, int Invoices, decimal Amount, bool Linked = true, string? Registry = null,
    CostCorrections? Corrections = null);

/// <summary>Раздел доли — одно название на реестр, второе на отчёт стройки и панель строки.</summary>
/// <param name="Registry">Как раздел зовёт колонка «Раздел» реестра: «Комарова 36 / 4 эт.». По нему же
/// идёт её отбор — и по нему, а не по идентификатору, отчёт складывает доли в строки.</param>
/// <param name="Short">Коротко, без стройки: она стоит рядом.</param>
/// <param name="Id">Сам раздел; null — «без раздела» или «раздел удалён»: не название, а его отсутствие.</param>
public sealed record SectionName(string Registry, string Short, Guid? Id);

/// <summary>Счёт так, как его видит отчёт о затратах.</summary>
/// <param name="VatTotal">«В том числе НДС» из шапки — запасной источник НДС там, где его нет у строки.</param>
/// <param name="Unmatched">Есть строки без позиции номенклатуры.</param>
/// <param name="VatByLines">НДС назван хоть у одной строки счёта — тогда «в том числе НДС» шапки уже
/// разложен по строкам, и на строку без своего НДС его долю брать нельзя.</param>
/// <param name="Parsed">Счёт разобран. Затратам всё равно — спрашивает диалог закрытия периода.</param>
/// <param name="Booked">У счёта есть книга исправлений (E2, issue #1100): во взгляде «на момент
/// закрытия» его деньги — строки книги, а не <paramref name="Money" />.</param>
public sealed record CostInvoice(
    Guid Id, Guid? SupplierId, decimal? Total, decimal? VatTotal, bool Unmatched, bool VatByLines,
    IReadOnlyList<PostedMoney> Money, bool Parsed = true, bool Booked = false);

/// <summary>
/// Деньги отчёта одним зерном: чьи и что. Зерно — и деньги основной таблицы, и строка книги
/// исправлений: отчёт складывает их одной функцией в обоих взглядах (E2, spec v3.3 п. 1.7), иначе «с
/// исправлениями» и «на момент закрытия» разошлись бы ещё и реализацией.
/// </summary>
/// <param name="Invoice">Чей это счёт — по нему считается «сколько счетов».</param>
/// <param name="Unmatched">У счёта есть строки без позиции номенклатуры — сейчас, а не на момент
/// закрытия: так их найдёт реестр.</param>
public sealed record CostMoney(Guid Invoice, bool Unmatched, Grain Grain);

/// <summary>НДС строки счёта: сумма строки и сколько в ней НДС.</summary>
public sealed record LineVat(decimal? Amount, decimal? VatAmount);

/// <summary>Затраты за период — посчитанные; названий здесь нет, их даёт вызывающий.</summary>
/// <param name="Sites">По стройкам — на экране «Все стройки»; на экране стройки пусто.</param>
/// <param name="Articles">Статьи вне строек — там же, отдельной группой (ТЗ COST-10.1).</param>
/// <param name="Lost">Деньги на объектах, которых больше нет (стройку удалили в ядре, статью — в
/// справочнике), — одним числом: реестр называет их все одинаково, и отбор по этому названию находит
/// их вместе. null — таких нет.</param>
/// <param name="Unallocated">Деньги оплаченных счетов, не лёгшие ни на один объект; null — таких нет.</param>
/// <param name="Suppliers">По контрагентам — на экране стройки; ключ null — «поставщик не указан».</param>
/// <param name="Sections">По разделам — на экране стройки, второй срез ТОЙ ЖЕ суммы (задача G5b, issue
/// #1198). Строка — НАЗВАНИЕ раздела в реестре, а не раздел: отбор реестра идёт по названию, и два
/// раздела, названные одинаково, под ссылкой сложились бы — значит, и в отчёте они одна строка, иначе
/// число строки и итог реестра разошлись бы молча (ревью PR #1209). По той же причине одной строкой
/// идут разделы, которых больше нет.</param>
/// <param name="Unmatched">Из затрат — счета со строками без позиции номенклатуры; null — таких нет.</param>
/// <param name="VatUnknown">Под «без НДС»: деньги, из которых НДС вычесть нечем, — учтены полной
/// суммой; null — таких нет либо суммы показаны с НДС. Во взгляде «на момент закрытия» — строки книги с
/// признаком «НДС неизвестен» их суммой без НДС (spec п. 1.3 шаг 4): признак исторический, и строка,
/// которая правит прежнюю заглушку, бывает и отрицательной.</param>
/// <param name="LostSuppliers">На экране стройки — деньги поставщиков, которых больше нет в справочнике,
/// одной строкой (реестр их не называет); в <paramref name="Suppliers" /> их нет. null — таких нет.</param>
public sealed record SiteCostsResult(
    IReadOnlyDictionary<Guid, CostFigure> Sites,
    IReadOnlyDictionary<Guid, CostFigure> Articles,
    CostFigure? Lost,
    CostFigure? Unallocated,
    IReadOnlyList<(Guid? Supplier, CostFigure Figure)> Suppliers,
    IReadOnlyList<(SectionName Section, CostFigure Figure)> Sections,
    CostFigure Total,
    CostFigure? Unmatched,
    CostFigure? VatUnknown,
    CostFigure? LostSuppliers = null);

/// <summary>
/// «Затраты по стройке» (задача G5, issue #1098, ТЗ COST-20): деньги оплаченных счетов, вошедшие в
/// учётные месяцы периода.
///
/// <para><b>Складываются те же строки, что в реестре</b> (<see cref="PostedMoney" />): каждое число
/// отчёта ведёт в «Реестр счетов» с готовым отбором и обязано равняться итогу «Суммы» там. Отчёт,
/// посчитанный своим проходом, сверял бы с реестром реализацию, а не цифру.</para>
///
/// <para><b>Без НДС</b> — вычитанием: НДС части пропорционален её доле в строке. НДС не назван НИ У
/// ОДНОЙ строки счёта — берётся доля «в том числе НДС» из шапки; нет и её — часть учтена ПОЛНОЙ суммой,
/// и это названо числом, а не спрятано (решение владельца 05.10.2026): «ставка не указана» — не «без
/// НДС». Назван у части строк — строка без своего НДС тоже идёт полной суммой: НДС шапки тогда уже лежит
/// в строках, и его доля на «пустую» строку вычла бы один и тот же налог дважды (ревью PR #1200). Копейки
/// округляются у каждой части, поэтому сумма «без НДС» по частям может отличаться от «сумма минус НДС»
/// счёта на копейки.</para>
/// </summary>
public static class SiteCosts
{
    /// <param name="invoices">Оплаченные неотклонённые счета, у которых в период вошла хоть часть денег.</param>
    /// <param name="from">Первый день периода.</param>
    /// <param name="through">Последний день периода.</param>
    /// <param name="site">Стройка — тогда строки по контрагентам; null — все стройки.</param>
    /// <param name="withVat">Суммы как в бумаге; иначе — без НДС, где его есть чем вычесть.</param>
    /// <param name="known">Объекты, у которых есть название; null — известны все.</param>
    /// <param name="section">Как назвать раздел доли на стройку — ТОТ ЖЕ вызов, каким реестр называет
    /// клетку «Раздел»: два определения «какой это раздел» разошлись бы на первой же нестандартной доле.
    /// null — срез по разделам не нужен.</param>
    public static SiteCostsResult Of(
        IReadOnlyList<CostInvoice> invoices, IReadOnlyDictionary<Guid, LineVat> lines,
        DateOnly from, DateOnly through, Guid? site, bool withVat, IReadOnlySet<Guid>? known = null,
        Func<AllocationTarget, SectionName>? section = null) =>
        Of(Corrected(invoices, lines), from, through, site, withVat, known, section);

    /// <summary>
    /// Взгляд «с исправлениями» (spec п. 1.7): деньги основной таблицы — зёрнами, тем же
    /// «без НДС», что у книги (<see cref="InvoiceBookPosting.Grains" />).
    /// </summary>
    public static IReadOnlyList<CostMoney> Corrected(IEnumerable<CostInvoice> invoices, IReadOnlyDictionary<Guid, LineVat> lines) =>
        [.. invoices.SelectMany(i => InvoiceBookPosting.Grains(i, lines).Select(g => new CostMoney(i.Id, i.Unmatched, g)))];

    /// <summary>Затраты за период — по зёрнам любого взгляда.</summary>
    /// <param name="money">Зёрна с деньгами и днём: основной таблицы или книги исправлений.</param>
    /// <param name="suppliers">Поставщики, которые есть в справочнике, — на экране стройки; остальные
    /// идут одной строкой <see cref="SiteCostsResult.LostSuppliers" />. null — есть все.</param>
    public static SiteCostsResult Of(
        IReadOnlyList<CostMoney> money, DateOnly from, DateOnly through, Guid? site, bool withVat,
        IReadOnlySet<Guid>? known = null, Func<AllocationTarget, SectionName>? section = null,
        IReadOnlySet<Guid>? suppliers = null)
    {
        bool Known(Guid? id) => id is { } key && known?.Contains(key) != false;

        var entries = money
            .Where(m => m.Grain.Day >= from && m.Grain.Day <= through)
            .Where(m => site is null || m.Grain.Cell.Target?.ConstructionId == site)
            .Select(m => new Entry(m, m.Grain.Cell.Target, withVat ? m.Grain.Gross : m.Grain.Net, !withVat && m.Grain.VatUnknown))
            .ToList();

        // «N счетов» строки — счета, чьи деньги в ней не ноль (решение владельца 16.10.2026, разбор
        // Дизайнера): исправление, перенёсшее деньги на другой объект, оставляет в книге «+120 и −120», и
        // «1 счёт, 0,00» читалось бы как «счёт здесь есть». Деньги строки от этого не меняются. Строка, где
        // не посчитан ни один счёт, — ноль из нулей: её нет вовсе, как нет «Удалённых», когда их не набралось.
        //
        // Итог считает счёт, посчитанный хоть в одной строке ОСНОВНОГО среза, — собранного здесь же, из тех
        // строк, что уйдут на экран (ревью PR #1392: список «строк экрана» отдельно от них разошёлся бы с
        // ними). Основной срез — тот, где счёт стоит в одной строке: объекты у всех строек, поставщики у одной.
        // Разделы — второй срез, их числа счетов в итог не складывались и раньше (счёт на два раздела — в двух
        // строках); итог по ним не считается, иначе «1 счёт» стоял бы над пустым списком поставщиков.
        // Не по сумме счёта целиком: перенос между объектами даёт счёту ноль в итоге, но в двух строках он есть.
        var inTotal = new HashSet<Guid>();
        CostFigure Row(IEnumerable<Entry> rows, bool main = true)
        {
            var list = rows.ToList();
            var counted = list.GroupBy(r => r.Money.Invoice).Where(g => g.Sum(r => r.Amount) != 0).Select(g => g.Key).ToList();
            if (main) inTotal.UnionWith(counted);
            return new(counted.Count, list.Sum(r => r.Amount), Corrections(list));
        }
        // Корректировки — отдельным числом у каждой цифры (PR 6d): тем же правилом счёта, что у строки, но по
        // перенесённым деньгам. «Целиком» — своих денег нет ни у одного счёта: тогда строка вся из корректировок.
        static CostCorrections? Corrections(List<Entry> list)
        {
            var shifted = list.Where(r => r.Money.Grain.Shifted).ToList();
            if (shifted.Count == 0) return null;
            var whole = list.Where(r => !r.Money.Grain.Shifted).GroupBy(r => r.Money.Invoice).All(g => g.Sum(r => r.Amount) == 0);
            var figure = new CostCorrections(
                shifted.GroupBy(r => r.Money.Invoice).Count(g => g.Sum(r => r.Amount) != 0), shifted.Sum(r => r.Amount), whole);
            return figure is { Invoices: 0, Amount: 0 } ? null : figure;
        }
        // «Из них» — только из счетов итога: счёт, которого итог не считает, не считается и в расшифровке.
        // ownMoney — счёт ещё и с ненулевыми деньгами в самой расшифровке: признак «НДС неизвестен» стоит у
        // строки денег, и «−120 без НДС и +120 с НДС» у счёта — не «учтён полной суммой». «Без позиции» —
        // признак счёта, и счёт итога в нём — весь.
        CostFigure Part(IEnumerable<Entry> rows, bool ownMoney)
        {
            var list = rows.ToList();
            return new(list.GroupBy(r => r.Money.Invoice).Count(g => inTotal.Contains(g.Key) && (!ownMoney || g.Sum(r => r.Amount) != 0)),
                list.Sum(r => r.Amount));
        }
        static CostFigure? Some(CostFigure figure) => figure.Invoices == 0 ? null : figure;
        Dictionary<Guid, CostFigure> Rows(IEnumerable<IGrouping<Guid, Entry>> groups) =>
            groups.Select(g => (g.Key, Figure: Row(g))).Where(g => g.Figure.Invoices > 0).ToDictionary(g => g.Key, g => g.Figure);

        Dictionary<Guid, CostFigure> sites = [], articles = [];
        CostFigure? lost = null, unallocated = null, lostSuppliers = null;
        List<(Guid? Supplier, CostFigure Figure)> bySupplier = [];
        List<(SectionName Section, CostFigure Figure)> bySection = [];
        if (site is null)
        {
            sites = Rows(entries.Where(e => Known(e.Target?.ConstructionId)).GroupBy(e => e.Target!.Value.ConstructionId!.Value));
            articles = Rows(entries.Where(e => Known(e.Target?.ArticleId)).GroupBy(e => e.Target!.Value.ArticleId!.Value));
            lost = Some(Row(entries.Where(e => e.Target is { } target && !Known(target.ConstructionId ?? target.ArticleId))));
            unallocated = Some(Row(entries.Where(e => e.Target is null)));
        }
        else
        {
            // Поставщик — ячейки зерна: у строки книги он тот, что был при записи (spec п. 0.3). Удалённые из
            // справочника — ОДНОЙ строкой, собранной из денег, а не из чисел строк (ревью PR #1392): во взгляде
            // книги счёт, у которого исправление сменило поставщика, стоит у обоих, и сложенные числа дали бы
            // «2 счёта, 0,00» за один счёт.
            bool Listed(Guid? supplier) => supplier is not { } id || suppliers?.Contains(id) != false;
            bySupplier = [.. entries.Where(e => Listed(e.Money.Grain.Cell.SupplierId))
                .GroupBy(e => e.Money.Grain.Cell.SupplierId)
                .Select(g => (g.Key, Figure: Row(g))).Where(g => g.Figure.Invoices > 0)];
            lostSuppliers = Some(Row(entries.Where(e => !Listed(e.Money.Grain.Cell.SupplierId))));
            // Тот же набор долей, сгруппированный иначе: сумма строк обоих срезов — один итог. А число
            // счетов по разделам в итог НЕ складывается: счёт на два раздела стоит в двух строках.
            // Группа — название в реестре: одноимённые разделы и разделы, которых больше нет, — одной
            // строкой, и счёт на два таких раздела в ней — один счёт.
            // ⚠️ Чьим именем названа строка, решает ПРАВИЛО, а не порядок чтения из базы: «без раздела»
            // побеждает раздел, названный так же (строка тогда — не название, а его отсутствие), а из
            // тёзок берётся один и тот же. Возьми мы первую попавшуюся долю, вид и место строки на
            // экране менялись бы от запроса к запросу (так и упал тест на master после PR #1209).
            if (section is not null)
                bySection = [.. entries
                    .Select(e => (Entry: e, Section: section(e.Target!.Value)))
                    .GroupBy(e => e.Section.Registry, StringComparer.Ordinal)
                    .Select(g => (g.Select(e => e.Section).OrderBy(s => s.Id is not null).ThenBy(s => s.Id).First(),
                        Figure: Row(g.Select(e => e.Entry), main: false)))
                    .Where(g => g.Figure.Invoices > 0)];
        }

        // Итог — после всех строк основного среза: его счета собраны ими.
        return new(sites, articles, lost, unallocated, bySupplier, bySection,
            new(inTotal.Count, entries.Sum(e => e.Amount), Corrections(entries)),
            Some(Part(entries.Where(e => e.Money.Unmatched), ownMoney: false)),
            withVat ? null : Some(Part(entries.Where(e => e.VatUnknown), ownMoney: true)),
            lostSuppliers);
    }

    /// <summary>Зерно отчёта: деньги, их объект, сумма в выбранном виде и признак «НДС неизвестен».</summary>
    private readonly record struct Entry(CostMoney Money, AllocationTarget? Target, decimal Amount, bool VatUnknown);

    /// <summary>
    /// «К оплате»: неоплаченные неотклонённые счета — от периода не зависит, неоплаченный счёт не
    /// принадлежит ни одному (ТЗ COST-16). По всем стройкам — суммы к оплате; по стройке — доли на неё.
    ///
    /// <para>«Без НДС» — ТЕМ ЖЕ правилом, что у затрат, и по всем стройкам, и по одной: считай их
    /// порознь (шапкой и строками), «к оплате» по стройкам не складывалось бы в общее (ревью PR #1200).</para>
    /// </summary>
    /// <returns>Число — и, под «без НДС», сколько в нём учтено полной суммой (null — нисколько).</returns>
    public static (CostFigure Figure, CostFigure? VatUnknown) Payable(
        IReadOnlyList<CostInvoice> unpaid, IReadOnlyDictionary<Guid, LineVat> lines, Guid? site, bool withVat)
    {
        // С НДС и по всем стройкам деньги по частям не нужны: это сумма к оплате из самой записи, и
        // вызывающий вправе их не читать вовсе.
        if (site is null && withVat) return (new(unpaid.Count, unpaid.Sum(i => i.Total ?? 0)), null);

        var entries = unpaid
            // Счёт без суммы к оплате по всем стройкам не стоит ничего — так его видит и реестр.
            .Where(i => site is not null || i.Total is not null)
            .SelectMany(i => i.Money
                .Where(m => m.Amount is not null && (site is null || m.Part?.ConstructionId == site))
                .Select(m =>
                {
                    var (amount, unknown) = withVat ? (m.Amount!.Value, false) : Net(m, i, lines);
                    return (Invoice: i.Id, Amount: amount, VatUnknown: unknown);
                }))
            .ToList();
        var blind = entries.Where(e => e.VatUnknown).ToList();

        return (
            new(site is null ? unpaid.Count : entries.Select(e => e.Invoice).Distinct().Count(), entries.Sum(e => e.Amount)),
            blind.Count == 0 ? null : new(blind.Select(e => e.Invoice).Distinct().Count(), blind.Sum(e => e.Amount)));
    }

    /// <summary>Деньги части без НДС — и признак, что вычесть было нечем.</summary>
    public static (decimal Amount, bool VatUnknown) Net(
        PostedMoney money, CostInvoice invoice, IReadOnlyDictionary<Guid, LineVat> lines)
    {
        var amount = money.Amount!.Value;
        // НДС строки — точнее шапки: в одном счёте бывают строки с разной ставкой. Шапка — только когда
        // строки об НДС молчат все: иначе её НДС уже разложен по строкам.
        var (whole, vat) = money.Part?.LineId is { } line && lines.TryGetValue(line, out var own) && own is { Amount: not null and not 0, VatAmount: not null }
            ? (own.Amount, own.VatAmount)
            : invoice.VatByLines ? (null, null) : (invoice.Total, invoice.VatTotal);

        return whole is { } basis && basis != 0 && vat is { } tax
            ? (amount - Math.Round(amount * tax / basis, 2, MidpointRounding.AwayFromZero), false)
            : (amount, true);
    }
}
