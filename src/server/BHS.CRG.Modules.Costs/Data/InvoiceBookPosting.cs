using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Ячейка книги — цель доли и поставщик счёта (spec v3.3 п. 0.3). Без цели — остаток счёта.
/// </summary>
/// <param name="Target">Стройка с разделом или статья вне строек; <c>null</c> — остаток.</param>
public readonly record struct BookCell(AllocationTarget? Target, Guid? SupplierId)
{
    /// <summary>Чей период у ячейки: доли — как у неё самой, остатка — компании.</summary>
    public PeriodContour Contour => Target is { } target ? PaymentPosting.ContourOf(target) : new PeriodContour.Company();
}

/// <summary>
/// Зерно денег: ячейка, учётный день и деньги — с НДС, без НДС, известен ли НДС. Им же записана строка
/// книги: форма у них одна, и сравнивают их друг с другом — два типа одной формы разошлись бы молча на
/// первом новом поле (ревью PR #1357).
/// </summary>
public sealed record Grain(BookCell Cell, DateOnly Day, decimal Gross, decimal Net, bool VatUnknown);

/// <summary>Запись книги до сохранения: вид, день D, допуск и строки.</summary>
public sealed record BookRecord(InvoiceBookKind Kind, DateOnly Day, decimal Tolerance, IReadOnlyList<Grain> Lines);

/// <summary>Что сдвигает запись: деньги счёта до и после и допуски, которыми они посчитаны.</summary>
/// <param name="AtLast">Деньги счёта при допуске ПОСЛЕДНЕЙ записи книги — для страховки (spec п. 1.3
/// шаг 2). Нужны, только если книга есть и тот допуск отличается от <paramref name="ToleranceBefore" />;
/// иначе <c>null</c>.</param>
public sealed record BookChange(
    InvoiceBookKind Kind,
    IReadOnlyList<Grain> Before, decimal ToleranceBefore,
    IReadOnlyList<Grain> After, decimal ToleranceAfter,
    IReadOnlyList<Grain>? AtLast = null);

/// <summary>
/// Проводка по книге счёта (<c>PostToBook</c>, E2, issue #1100, spec v3.3 п. 1.2–1.3) — чистая
/// арифметика, без базы: одна функция на предпросмотр и на запись, как у <see cref="PaymentPosting" />.
///
/// <para><b>Правило (б′).</b> Разница считается по ПАРАМ (ячейка, день): деньги, которые правка
/// сдвинула в паре, ложатся строкой её же дня, если день открыт и книга на эту пару до записи равна
/// документу; иначе — днём записи D. Так закрытый месяц во взгляде «на момент закрытия» не меняется, а
/// открытый остаётся равным документу: исправление закрытого дня лежит днём исправления, а не в нём.</para>
///
/// <para><b>Что сравнивается</b> — только суммы с НДС и без. «НДС неизвестен» ни в одном равенстве не
/// участвует (решение владельца 15.10.2026): он исторический, и участвуй он, пара, в которой НДС стал
/// известен, навсегда разошлась бы с книгой, и следующие её разницы уходили бы днём D даже в контуре,
/// который ни разу не закрывался (S33).</para>
///
/// <para>⚠️ «Открыт ли день» здесь спрашивается, а учётные дни не назначаются: их назначает
/// <see cref="PaymentPosting" />. Книга кладёт деньги по дням, которые уже назначены.</para>
/// </summary>
public static class InvoiceBookPosting
{
    /// <summary>
    /// Деньги счёта зёрнами — ТЕМ ЖЕ «без НДС», что у отчётов о затратах (<see cref="SiteCosts.Net" />):
    /// посчитай книга его своим правилом, «на момент закрытия» и «с исправлениями» разошлись бы на
    /// копейках там, где правка не трогала ничего. Зёрна только с деньгами и днём: у неоплаченного
    /// счёта дней нет — и зёрен нет.
    /// </summary>
    public static IReadOnlyList<Grain> Grains(CostInvoice invoice, IReadOnlyDictionary<Guid, LineVat> lines) =>
    [
        .. invoice.Money
            .Where(m => m is { Amount: not null, AccountingOn: not null })
            .Select(m =>
            {
                var (net, unknown) = SiteCosts.Net(m, invoice, lines);
                return new Grain(new BookCell(m.Part?.Target, invoice.SupplierId), m.AccountingOn!.Value,
                    m.Amount!.Value, net, unknown);
            }),
    ];

    /// <summary>
    /// Записи, которые ляжут в книгу: открытие (если книги нет), выравнивание (страховка) и сама
    /// запись — всегда, даже без строк: заголовок и есть след того, что счёт трогали.
    /// </summary>
    /// <param name="book">Книга, как она лежит, — по порядку записей.</param>
    /// <param name="boundaries">Границы из замка периода, под которым идёт запись.</param>
    /// <param name="today">Сегодня по часам компании.</param>
    /// <exception cref="InvalidOperationException">Книга разошлась с документом до записи или после неё:
    /// это ошибка кода, а не повод выровнять.</exception>
    public static IReadOnlyList<BookRecord> Post(
        IReadOnlyList<BookRecord> book, BookChange change, PeriodBoundaries boundaries, DateOnly today)
    {
        if (change.Kind == InvoiceBookKind.Opening)
            throw new ArgumentException("Открытие пишет сама проводка, перед первой записью книги.", nameof(change));
        // Книгу заводят исправление, отмена оплаты запертого и смена допуска запертому (spec п. 1.5);
        // оплата — только продолжает её. Оплата неоплаченного счёта без книги в книгу не пишется вовсе:
        // пустое «открытие» перед ней было бы ложным следом (ревью PR #1357).
        if (book.Count == 0 && change.Kind == InvoiceBookKind.Payment)
            throw new ArgumentException("Оплата счёта без книги в книгу не пишется: книгу она не заводит.", nameof(change));
        if (book.Count > 0 && book[^1].Tolerance != change.ToleranceBefore && change.AtLast is null)
            throw new ArgumentException(
                $"Последняя запись книги посчитана при допуске {book[^1].Tolerance}, а «до» — при " +
                $"{change.ToleranceBefore}: без денег при прежнем допуске страховке не с чем сравнить.", nameof(change));

        var before = Pairs(change.Before);
        var after = Pairs(change.After);
        var atLast = change.AtLast is null ? null : Pairs(change.AtLast);

        // День записи D (п. 1.2): не раньше сегодня, не в закрытом дне ни одной ячейки записи и не
        // раньше прежних записей — дни записей одного счёта не убывают (I4′(в)).
        var cells = before.Keys.Concat(after.Keys).Concat(atLast?.Keys ?? Enumerable.Empty<(BookCell Cell, DateOnly Day)>())
            .Select(k => k.Cell).Distinct();
        var day = cells
            .Select(c => boundaries.ClosedThrough(c.Contour) is { } through ? through.AddDays(1) : today)
            .Concat(book.Select(r => r.Day))
            .Append(today)
            .Max();

        var lines = book.SelectMany(r => r.Lines).ToList();
        var written = new List<BookRecord>();
        void Write(BookRecord record)
        {
            written.Add(record);
            lines.AddRange(record.Lines);
        }

        if (book.Count == 0)
            Write(new BookRecord(InvoiceBookKind.Opening, day, change.ToleranceBefore,
                [.. Ordered(before).Select(p => new Grain(p.Key.Cell, p.Key.Day, p.Value.Gross, p.Value.Net, p.Value.VatUnknown))]));
        else if (atLast is not null && !Same(atLast, before))
            // Страховка — по деньгам, а не по числу допуска (п. 1.3 шаг 2): смена, не тронувшая денег
            // счёта, ему ничего не пишет, и пустой заголовок «выравнивание» был бы ложным следом.
            Write(new BookRecord(InvoiceBookKind.Alignment, day, change.ToleranceBefore,
                Diff(atLast, before, lines, boundaries, day)));

        Ensure(lines, before, "до записи");
        Write(new BookRecord(change.Kind, day, change.ToleranceAfter, Diff(before, after, lines, boundaries, day)));
        Ensure(lines, after, "после записи");
        return written;
    }

    /// <summary>
    /// Отметка предпросмотра исправления (spec п. 1.4): день D и ВСЕ строки, которые запишет запрос, с
    /// их днями, — открытие и выравнивание тоже. Запись сверяет её со своей: полночь, закрытие периода и
    /// смена допуска между предпросмотром и записью версию формы не двигают, а строки меняют, — и человек
    /// подтвердил бы не то, что ляжет в книгу.
    ///
    /// <para>Отпечаток, а не сами строки: форма возвращает его как есть и разбирать не обязана. Числа —
    /// в неизменном виде (без культуры): «0,5» и «0.50» дали бы разные отметки одним деньгам.</para>
    /// </summary>
    public static string Stamp(IReadOnlyList<BookRecord> records)
    {
        var text = new System.Text.StringBuilder();
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var record in records)
        {
            text.Append(invariant, $"{record.Kind}|{record.Day:yyyy-MM-dd}|{record.Tolerance:0.00####}\n");
            foreach (var line in record.Lines)
                text.Append(invariant,
                    $"  {line.Cell.Target?.ConstructionId}|{line.Cell.Target?.SectionId}|{line.Cell.Target?.ArticleId}|" +
                    $"{line.Cell.SupplierId}|{line.Day:yyyy-MM-dd}|{line.Gross:0.00####}|{line.Net:0.00####}|{line.VatUnknown}\n");
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    /// <summary>
    /// Проводка от одних денег к другим по правилу (б′). Книга <paramref name="lines" /> — какой она
    /// лежит ДО этой проводки (с уже записанными открытием и выравниванием).
    /// </summary>
    private static List<Grain> Diff(
        IReadOnlyDictionary<(BookCell Cell, DateOnly Day), Money> from,
        IReadOnlyDictionary<(BookCell Cell, DateOnly Day), Money> to,
        IReadOnlyList<Grain> lines, PeriodBoundaries boundaries, DateOnly day)
    {
        var book = Pairs(lines);
        var result = new List<Grain>();

        foreach (var key in from.Keys.Union(to.Keys))
        {
            var was = from.GetValueOrDefault(key);
            var now = to.GetValueOrDefault(key);
            var (gross, net) = (now.Gross - was.Gross, now.Net - was.Net);
            if (gross == 0 && net == 0) continue;

            // Своим днём — только пока книга на эту пару равна документу: тогда строка оставляет её
            // равной и после. Иначе в паре лежит замороженное число закрытия или прежняя поправка, и
            // своим днём строка сдвинула бы закрытое — а «закрыт ли день сейчас» этого уже не видит
            // (закрытие отменили).
            var held = book.GetValueOrDefault(key);
            var own = !boundaries.IsClosed(key.Day, key.Cell.Contour) && held.Gross == was.Gross && held.Net == was.Net;
            result.Add(new Grain(key.Cell, own ? key.Day : day, gross, net, was.VatUnknown || now.VatUnknown));
        }

        // Строки одного дня одной ячейки складываются — у дня D их бывает несколько из разных пар.
        return [.. Ordered(Pairs(result))
            .Select(p => new Grain(p.Key.Cell, p.Key.Day, p.Value.Gross, p.Value.Net, p.Value.VatUnknown))];
    }

    /// <summary>Книга по ячейкам равна документу — по суммам с НДС и без.</summary>
    private static void Ensure(
        IReadOnlyList<Grain> lines, IReadOnlyDictionary<(BookCell Cell, DateOnly Day), Money> document, string when)
    {
        var book = ByCell(lines.Select(l => (l.Cell, l.Gross, l.Net)));
        var expected = ByCell(document.Select(p => (p.Key.Cell, p.Value.Gross, p.Value.Net)));
        var off = book.Keys.Union(expected.Keys)
            .Where(c => book.GetValueOrDefault(c) != expected.GetValueOrDefault(c))
            .Select(c => $"{c}: книга {book.GetValueOrDefault(c)}, документ {expected.GetValueOrDefault(c)}")
            .ToList();

        if (off.Count > 0)
            throw new InvalidOperationException(
                $"Книга счёта разошлась с документом {when}: {string.Join("; ", off)}. Выравнивать это " +
                "нельзя — расхождение значит, что деньги счёта менялись мимо книги, а запись поверх " +
                "спрятала бы это навсегда.");
    }

    private static Dictionary<BookCell, (decimal Gross, decimal Net)> ByCell(
        IEnumerable<(BookCell Cell, decimal Gross, decimal Net)> money) =>
        money.GroupBy(m => m.Cell)
            .Select(g => (g.Key, Gross: g.Sum(m => m.Gross), Net: g.Sum(m => m.Net)))
            .Where(c => c.Gross != 0 || c.Net != 0)
            .ToDictionary(c => c.Key, c => (c.Gross, c.Net));

    private static bool Same(
        IReadOnlyDictionary<(BookCell Cell, DateOnly Day), Money> a, IReadOnlyDictionary<(BookCell Cell, DateOnly Day), Money> b) =>
        a.Keys.Union(b.Keys).All(k => a.GetValueOrDefault(k) is var x && b.GetValueOrDefault(k) is var y
                                      && x.Gross == y.Gross && x.Net == y.Net);

    /// <summary>Деньги пары (ячейка, день); нулевые пары отбрасываются — их нет ни в книге, ни в документе.</summary>
    private static Dictionary<(BookCell Cell, DateOnly Day), Money> Pairs(IEnumerable<Grain> grains) =>
        grains.GroupBy(g => (g.Cell, g.Day))
            .Select(g => (g.Key, Money: new Money(g.Sum(x => x.Gross), g.Sum(x => x.Net), g.Any(x => x.VatUnknown))))
            .Where(p => p.Money.Gross != 0 || p.Money.Net != 0)
            .ToDictionary(p => p.Key, p => p.Money);

    /// <summary>Порядок строк — по ячейке и дню: одна и та же правка пишет книгу одинаково.</summary>
    private static IEnumerable<KeyValuePair<(BookCell Cell, DateOnly Day), Money>> Ordered(
        IReadOnlyDictionary<(BookCell Cell, DateOnly Day), Money> pairs) =>
        pairs.OrderBy(p => p.Key.Cell.Target is null)
            .ThenBy(p => p.Key.Cell.Target?.ConstructionId)
            .ThenBy(p => p.Key.Cell.Target?.SectionId)
            .ThenBy(p => p.Key.Cell.Target?.ArticleId)
            .ThenBy(p => p.Key.Cell.SupplierId)
            .ThenBy(p => p.Key.Day);

    private readonly record struct Money(decimal Gross, decimal Net, bool VatUnknown);
}
