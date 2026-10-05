namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Число отчёта: сколько счетов и сколько денег.</summary>
public sealed record CostFigure(int Invoices, decimal Amount);

/// <summary>Строка отчёта — стройка, статья вне строек или контрагент.</summary>
/// <param name="Id">Чья строка; null — строка не про одну запись: «поставщик не указан», удалённые
/// объекты или поставщики одной строкой.</param>
/// <param name="Linked">Есть ли у реестра отбор, под которым его итог равен этому числу. Нет — стрелки
/// у строки нет: ссылка, по которой цифры не сходятся, хуже её отсутствия.</param>
/// <param name="Registry">Как эту строку зовёт реестр, если иначе, чем отчёт: раздел в отчёте стройки —
/// «4 эт.», а колонка «Раздел» реестра — «Комарова 36 / 4 эт.». Отбор ссылки берёт это название.</param>
public sealed record CostLine(Guid? Id, string Name, int Invoices, decimal Amount, bool Linked = true, string? Registry = null);

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
public sealed record CostInvoice(
    Guid Id, Guid? SupplierId, decimal? Total, decimal? VatTotal, bool Unmatched, bool VatByLines,
    IReadOnlyList<PostedMoney> Money, bool Parsed = true);

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
/// суммой; null — таких нет либо суммы показаны с НДС.</param>
public sealed record SiteCostsResult(
    IReadOnlyDictionary<Guid, CostFigure> Sites,
    IReadOnlyDictionary<Guid, CostFigure> Articles,
    CostFigure? Lost,
    CostFigure? Unallocated,
    IReadOnlyList<(Guid? Supplier, CostFigure Figure)> Suppliers,
    IReadOnlyList<(SectionName Section, CostFigure Figure)> Sections,
    CostFigure Total,
    CostFigure? Unmatched,
    CostFigure? VatUnknown);

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
        Func<InvoiceAllocation, SectionName>? section = null)
    {
        bool Known(Guid? id) => id is { } key && known?.Contains(key) != false;

        var entries = invoices.SelectMany(i => i.Money
                .Where(m => m is { Amount: not null, AccountingOn: { } day } && day >= from && day <= through)
                .Where(m => site is null || m.Part?.ConstructionId == site)
                .Select(m =>
                {
                    var (amount, unknown) = withVat ? (m.Amount!.Value, false) : Net(m, i, lines);
                    return (Invoice: i, Money: m, Amount: amount, VatUnknown: unknown);
                }))
            .ToList();

        static CostFigure Figure<T>(IEnumerable<(CostInvoice Invoice, T Money, decimal Amount, bool VatUnknown)> rows)
        {
            var list = rows.ToList();
            return new(list.Select(r => r.Invoice.Id).Distinct().Count(), list.Sum(r => r.Amount));
        }
        static CostFigure? Some(CostFigure figure) => figure.Invoices == 0 ? null : figure;

        return new(
            site is null
                ? entries.Where(e => Known(e.Money.Part?.ConstructionId))
                    .GroupBy(e => e.Money.Part!.ConstructionId!.Value).ToDictionary(g => g.Key, Figure)
                : new Dictionary<Guid, CostFigure>(),
            site is null
                ? entries.Where(e => Known(e.Money.Part?.ArticleId))
                    .GroupBy(e => e.Money.Part!.ArticleId!.Value).ToDictionary(g => g.Key, Figure)
                : new Dictionary<Guid, CostFigure>(),
            site is null
                ? Some(Figure(entries.Where(e => e.Money.Part is { } part && !Known(part.ConstructionId ?? part.ArticleId))))
                : null,
            site is null ? Some(Figure(entries.Where(e => e.Money.Part is null))) : null,
            site is null ? [] : [.. entries.GroupBy(e => e.Invoice.SupplierId).Select(g => (g.Key, Figure(g)))],
            // Тот же набор долей, сгруппированный иначе: сумма строк обоих срезов — один итог. А число
            // счетов по разделам в итог НЕ складывается: счёт на два раздела стоит в двух строках.
            // Группа — название в реестре: одноимённые разделы и разделы, которых больше нет, — одной
            // строкой, и счёт на два таких раздела в ней — один счёт.
            // ⚠️ Чьим именем названа строка, решает ПРАВИЛО, а не порядок чтения из базы: «без раздела»
            // побеждает раздел, названный так же (строка тогда — не название, а его отсутствие), а из
            // тёзок берётся один и тот же. Возьми мы первую попавшуюся долю, вид и место строки на
            // экране менялись бы от запроса к запросу (так и упал тест на master после PR #1209).
            site is null || section is null ? [] : [.. entries
                .Select(e => (Entry: e, Section: section(e.Money.Part!)))
                .GroupBy(e => e.Section.Registry, StringComparer.Ordinal)
                .Select(g => (g.Select(e => e.Section).OrderBy(s => s.Id is not null).ThenBy(s => s.Id).First(),
                    Figure(g.Select(e => e.Entry))))],
            Figure(entries),
            Some(Figure(entries.Where(e => e.Invoice.Unmatched))),
            withVat ? null : Some(Figure(entries.Where(e => e.VatUnknown))));
    }

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
