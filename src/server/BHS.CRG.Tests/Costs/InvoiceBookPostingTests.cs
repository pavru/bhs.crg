using System.Globalization;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Проводка по книге счёта (E2, issue #1100) — числа пятого прогона устройства (spec v3.3): два
/// независимых счётчика посчитали каждый сценарий, и здесь проверяется, что функция кода даёт ТЕ ЖЕ
/// строки. Номера сценариев — по материалам прогона (S05, S24, …); S33 добавила пятая ревизия.
///
/// <para>После каждого шага — инварианты, которые видны без базы: I1 (книга по ячейкам равна документу
/// — его держит и сама функция), I4′ (а) и (в) — строки не в закрытых днях, дни записей не убывают, —
/// и I8 там, где контур не закрывался ни разу. Заморозку закрытого месяца (I2) сценарии проверяют
/// числом «на момент закрытия» до и после.</para>
///
/// <para>Зёрна «до» и «после» здесь заданы руками, как в материалах прогона: дни долей назначает
/// <see cref="PaymentPosting" /> (его правила проверяют свои тесты), а книга кладёт деньги по дням,
/// которые уже назначены.</para>
/// </summary>
public partial class InvoiceBookPostingTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid R1 = Guid.NewGuid();
    private static readonly Guid R2 = Guid.NewGuid();
    private static readonly Guid R3 = Guid.NewGuid();
    private static readonly Guid Supplier = Guid.NewGuid();

    private static readonly BookCell CellA = Cell(A);
    private static readonly BookCell CellB = Cell(B);
    private static readonly BookCell Rest = new(null, Supplier);

    private static BookCell Cell(Guid site, Guid? section = null) => new(AllocationTarget.Site(site, section), Supplier);

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static Grain G(BookCell cell, DateOnly day, decimal gross, decimal? net = null, bool vatUnknown = false) =>
        new(cell, day, gross, net ?? gross, vatUnknown);

    private static PeriodBoundaries Closed(Guid site, DateOnly through) => new(null, new Dictionary<Guid, DateOnly> { [site] = through });

    private static readonly PeriodBoundaries ClosedA = Closed(A, D(9, 30));

    /// <summary>Книга одного счёта по шагам — с инвариантами после каждого.</summary>
    private sealed class Run
    {
        public List<BookRecord> Book { get; } = [];

        /// <summary>Контуры, которые не закрывались ни разу: в них «на момент закрытия» = документ по парам (I8).</summary>
        public HashSet<PeriodContour> NeverClosed { get; } = [];

        public IReadOnlyList<BookRecord> Post(
            InvoiceBookKind kind, DateOnly today, PeriodBoundaries boundaries, Grain[] before, Grain[] after,
            decimal toleranceBefore = 1.00m, decimal? toleranceAfter = null, Grain[]? atLast = null)
        {
            var added = InvoiceBookPosting.Post(Book,
                new BookChange(kind, before, toleranceBefore, after, toleranceAfter ?? toleranceBefore, atLast),
                boundaries, today);

            // I4′(а): строка своим днём или днём D — день открыт в контуре её ячейки (открытие не в счёт).
            foreach (var line in added.Where(r => r.Kind != InvoiceBookKind.Opening).SelectMany(r => r.Lines))
                Assert.False(boundaries.IsClosed(line.Day, line.Cell.Contour), $"строка в закрытом дне: {Show(line)}");

            Book.AddRange(added);

            // I4′(в): дни записей не убывают.
            Assert.Equal(Book.Select(r => r.Day).OrderBy(d => d), Book.Select(r => r.Day));

            // I8: в контуре, который ни разу не закрывался, книга по парам равна документу.
            var pairs = Book.SelectMany(r => r.Lines).Where(l => NeverClosed.Contains(l.Cell.Contour))
                .GroupBy(l => (l.Cell, l.Day)).ToDictionary(g => g.Key, g => (g.Sum(l => l.Gross), g.Sum(l => l.Net)));
            var document = after.Where(g => NeverClosed.Contains(g.Cell.Contour))
                .GroupBy(g => (g.Cell, g.Day)).ToDictionary(g => g.Key, g => (g.Sum(x => x.Gross), g.Sum(x => x.Net)));
            foreach (var key in pairs.Keys.Union(document.Keys))
                Assert.True(pairs.GetValueOrDefault(key) == document.GetValueOrDefault(key),
                    $"I8: {key} книга {pairs.GetValueOrDefault(key)}, документ {document.GetValueOrDefault(key)}");

            return added;
        }

        /// <summary>«На момент закрытия» по ячейкам стройки за месяц — с НДС.</summary>
        public decimal Close(Func<BookCell, bool> cells, int month) =>
            Book.SelectMany(r => r.Lines).Where(l => cells(l.Cell) && l.Day.Month == month).Sum(l => l.Gross);
    }

    private static bool OnA(BookCell cell) => cell.Target?.ConstructionId == A;

    private static string Name(BookCell cell) =>
        cell.Target is not { } target ? "остаток"
        : (target.ConstructionId == A ? "A" : target.ConstructionId == B ? "B" : "?")
          + (target.SectionId == R1 ? "/р1" : target.SectionId == R2 ? "/р2" : target.SectionId == R3 ? "/р3" : "");

    private static string Show(Grain line) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Name(line.Cell)} {line.Day:dd.MM} {line.Gross:+0.00;-0.00;0.00}/{line.Net:+0.00;-0.00;0.00}{(line.VatUnknown ? " vU" : "")}");

    private static string[] Lines(BookRecord record) => [.. record.Lines.Select(Show).Order(StringComparer.Ordinal)];

    private static string[] Sorted(params string[] lines) => [.. lines.Order(StringComparer.Ordinal)];

    // ── S05: смена допуска пишет книгу (решение владельца 15.10.2026, вариант (б)) ─────────────────

    /// <summary>
    /// Счёт 1000,00 (НДС 0): B 400,30, A 600,50 → расхождение −0,80 в последней доле, A 599,70. Оплата
    /// 15.09, A закрыта по 30.09. Допуск 1,00 → 0,50 → 1,00: сентябрь A «на момент закрытия» остаётся
    /// 599,70 — числом записи закрытия.
    /// </summary>
    private static readonly Grain[] S05At100 = [G(CellB, D(9, 15), 400.30m), G(CellA, D(9, 15), 599.70m)];
    private static readonly Grain[] S05At050 =
        [G(CellB, D(9, 15), 400.30m), G(CellA, D(9, 15), 600.50m), G(Rest, D(9, 15), -0.80m)];

    [Fact]
    public void S05_смена_допуска_туда_и_обратно_не_сдвигает_закрытый_месяц()
    {
        var run = new Run { NeverClosed = { new PeriodContour.Company() } };

        var lowered = run.Post(InvoiceBookKind.Alignment, D(10, 2), ClosedA, S05At100, S05At050, 1.00m, 0.50m);
        Assert.Equal([InvoiceBookKind.Opening, InvoiceBookKind.Alignment], lowered.Select(r => r.Kind));
        Assert.Equal(Sorted("A 15.09 +599.70/+599.70", "B 15.09 +400.30/+400.30"), Lines(lowered[0]));
        Assert.Equal((D(10, 2), 1.00m), (lowered[0].Day, lowered[0].Tolerance));
        Assert.Equal(Sorted("A 02.10 +0.80/+0.80", "остаток 15.09 -0.80/-0.80"), Lines(lowered[1]));
        Assert.Equal(0.50m, lowered[1].Tolerance);

        // 05.10 исправление только текста: заголовок без строк.
        var text = run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA, S05At050, S05At050, 0.50m);
        Assert.Empty(Assert.Single(text).Lines);

        var raised = run.Post(InvoiceBookKind.Alignment, D(10, 6), ClosedA, S05At050, S05At100, 0.50m, 1.00m);
        Assert.Equal(Sorted("A 06.10 -0.80/-0.80", "остаток 15.09 +0.80/+0.80"), Lines(Assert.Single(raised)));

        Assert.Empty(Assert.Single(run.Post(InvoiceBookKind.Correction, D(10, 7), ClosedA, S05At100, S05At100)).Lines);
        Assert.Equal(599.70m, run.Close(OnA, 9));
        Assert.Equal(0m, run.Close(c => c == Rest, 9));
    }

    /// <summary>S24: после понижения допуска — исправление открытой доли; строка своим днём.</summary>
    [Fact]
    public void S24_исправление_открытой_доли_после_смены_допуска_ложится_своим_днём()
    {
        var run = new Run();
        run.Post(InvoiceBookKind.Alignment, D(10, 2), ClosedA, S05At100, S05At050, 1.00m, 0.50m);

        Grain[] after = [G(CellB, D(9, 15), 410.30m), G(CellA, D(9, 15), 600.50m), G(Rest, D(9, 15), -0.80m)];
        var fixedB = run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA, S05At050, after, 0.50m);

        Assert.Equal(["B 15.09 +10.00/+10.00"], Lines(Assert.Single(fixedB)));
        Assert.Equal(599.70m, run.Close(OnA, 9));
        Assert.Equal(0.80m, run.Close(OnA, 10));
    }

    // ── S29: положительный остаток от допуска при закрытой компании ──────────────────────────────

    /// <summary>
    /// Счёт 1000,80 (НДС 0): B 400,30, A 600,00 + 0,50. Компания закрыта по 30.09. Допуск 0,40 — остаток
    /// +0,50 днём чтения 15.09; в книге — днём D (02.10). Исправление только текста — строк нет: снимок
    /// «до правки» берёт день остатка тем же правилом, что чтение (#1345).
    /// </summary>
    [Fact]
    public void S29_остаток_от_допуска_в_закрытой_компании_ложится_днём_записи()
    {
        var company = new PeriodBoundaries(D(9, 30), new Dictionary<Guid, DateOnly>());
        Grain[] before = [G(CellB, D(9, 15), 400.30m), G(CellA, D(9, 15), 600.50m)];
        Grain[] after = [G(CellB, D(9, 15), 400.30m), G(CellA, D(9, 15), 600.00m), G(Rest, D(9, 15), 0.50m)];
        var run = new Run();

        var lowered = run.Post(InvoiceBookKind.Alignment, D(10, 2), company, before, after, 1.00m, 0.40m);
        Assert.Equal(Sorted("A 15.09 +600.50/+600.50", "B 15.09 +400.30/+400.30"), Lines(lowered[0]));
        Assert.Equal(Sorted("A 02.10 -0.50/-0.50", "остаток 02.10 +0.50/+0.50"), Lines(lowered[1]));

        Assert.Empty(Assert.Single(run.Post(InvoiceBookKind.Correction, D(10, 5), company, after, after, 0.40m)).Lines);
        Assert.Equal(1000.80m, run.Close(_ => true, 9));
    }

    // ── S31: смена допуска у запертых счетов без книги, оба знака ────────────────────────────────

    /// <summary>
    /// С1 (расхождение −0,80) и С2 (+0,50), оба на A, A закрыта по 30.09. Допуск 0,40 и обратно 1,00:
    /// сентябрь A «на момент закрытия» по двум счетам — 1200,20 до, между и после; повторное закрытие
    /// (после отмены) запишет то же число.
    /// </summary>
    [Fact]
    public void S31_смена_допуска_держит_закрытый_сентябрь_у_обоих_знаков()
    {
        Grain[] c1At100 = [G(CellB, D(9, 15), 400.30m), G(CellA, D(9, 15), 599.70m)];
        Grain[] c1At040 = [G(CellB, D(9, 15), 400.30m), G(CellA, D(9, 15), 600.50m), G(Rest, D(9, 15), -0.80m)];
        Grain[] c2At100 = [G(CellB, D(9, 15), 400.30m), G(CellA, D(9, 15), 600.50m)];
        Grain[] c2At040 = [G(CellB, D(9, 15), 400.30m), G(CellA, D(9, 15), 600.00m), G(Rest, D(9, 15), 0.50m)];
        var c1 = new Run { NeverClosed = { new PeriodContour.Company() } };
        var c2 = new Run { NeverClosed = { new PeriodContour.Company() } };
        decimal September() => c1.Close(OnA, 9) + c2.Close(OnA, 9);

        Assert.Equal("A 02.10 +0.80/+0.80",
            Lines(c1.Post(InvoiceBookKind.Alignment, D(10, 2), ClosedA, c1At100, c1At040, 1.00m, 0.40m)[1]).First());
        var second = c2.Post(InvoiceBookKind.Alignment, D(10, 2), ClosedA, c2At100, c2At040, 1.00m, 0.40m);
        Assert.Equal(Sorted("A 02.10 -0.50/-0.50", "остаток 15.09 +0.50/+0.50"), Lines(second[1]));
        Assert.Equal(1200.20m, September());

        c1.Post(InvoiceBookKind.Alignment, D(10, 3), ClosedA, c1At040, c1At100, 0.40m, 1.00m);
        var back = c2.Post(InvoiceBookKind.Alignment, D(10, 3), ClosedA, c2At040, c2At100, 0.40m, 1.00m);
        Assert.Equal(Sorted("A 03.10 +0.50/+0.50", "остаток 15.09 -0.50/-0.50"), Lines(Assert.Single(back)));
        Assert.Equal(1200.20m, September());
    }

    // ── Страховка: по деньгам, а не по числу допуска ─────────────────────────────────────────────

    /// <summary>
    /// Смена допуска, не тронувшая денег счёта, ему ничего не пишет — и допуск последней записи
    /// остаётся прежним. Следующая запись не пишет пустого «выравнивания» (находка A пятого прогона).
    /// </summary>
    [Fact]
    public void Страховка_молчит_когда_смена_допуска_не_тронула_денег()
    {
        Grain[] money = [G(CellA, D(9, 15), 120m, 100m)];
        var run = new Run();
        run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA, money, money, 1.00m);

        var next = run.Post(InvoiceBookKind.Correction, D(10, 6), ClosedA, money, money, 0.50m, atLast: money);

        Assert.Equal([InvoiceBookKind.Correction], next.Select(r => r.Kind));
    }

    /// <summary>
    /// Книга посчитана при 0,50, а запись — при 1,00, и деньги разные (гонка мимо смены допуска):
    /// страховка пишет выравнивание тем же правилом (б′), что смена допуска в S05 06.10.
    /// </summary>
    [Fact]
    public void Страховка_выравнивает_книгу_посчитанную_при_другом_допуске()
    {
        var run = new Run();
        run.Post(InvoiceBookKind.Alignment, D(10, 2), ClosedA, S05At100, S05At050, 1.00m, 0.50m);

        var next = run.Post(InvoiceBookKind.Correction, D(10, 6), ClosedA, S05At100, S05At100, 1.00m, atLast: S05At050);

        Assert.Equal([InvoiceBookKind.Alignment, InvoiceBookKind.Correction], next.Select(r => r.Kind));
        Assert.Equal(Sorted("A 06.10 -0.80/-0.80", "остаток 15.09 +0.80/+0.80"), Lines(next[0]));
        Assert.Equal(1.00m, next[0].Tolerance);
        Assert.Empty(next[1].Lines);
    }

    /// <summary>Без денег при прежнем допуске страховке не с чем сравнить — отказ, а не молчание.</summary>
    [Fact]
    public void Запись_при_другом_допуске_без_прежних_денег_отказывает()
    {
        var run = new Run();
        run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA, S05At100, S05At100, 1.00m);

        Assert.Throws<ArgumentException>(() =>
            InvoiceBookPosting.Post(run.Book, new BookChange(InvoiceBookKind.Correction, S05At050, 0.50m, S05At050, 0.50m),
                ClosedA, D(10, 6)));
    }

    /// <summary>
    /// Оплата неоплаченного счёта без книги книгу не заводит (spec п. 1.5): иначе в книге лёг бы пустой
    /// заголовок «открытие» — след, которого не было.
    /// </summary>
    [Fact]
    public void Оплата_счёта_без_книги_в_книгу_не_пишется() =>
        Assert.Throws<ArgumentException>(() => InvoiceBookPosting.Post([],
            new BookChange(InvoiceBookKind.Payment, [], 1.00m, [G(CellA, D(10, 7), 120m)], 1.00m), ClosedA, D(10, 7)));

    /// <summary>Книга, разошедшаяся с документом при том же допуске, — ошибка, а не повод выровнять.</summary>
    [Fact]
    public void Книга_разошедшаяся_с_документом_не_выравнивается()
    {
        var run = new Run();
        run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA, [G(CellA, D(9, 15), 120m)], [G(CellA, D(9, 15), 120m)]);

        Assert.Throws<InvalidOperationException>(() =>
            InvoiceBookPosting.Post(run.Book, new BookChange(InvoiceBookKind.Correction,
                [G(CellA, D(9, 15), 132m)], 1.00m, [G(CellA, D(9, 15), 144m)], 1.00m), ClosedA, D(10, 6)));
    }

    // ── «НДС неизвестен»: исторический признак вне равенств (S30, S33) ───────────────────────────

    /// <summary>S30 (а): НДС стал известен — строка 0 / −20 несёт «неизвестен»; отмена оплаты — уже нет.</summary>
    [Fact]
    public void S30_НДС_стал_известен_строка_несёт_признак_а_отмена_уже_нет()
    {
        var run = new Run();
        Grain[] unknown = [G(CellA, D(9, 15), 120m, vatUnknown: true)];
        Grain[] known = [G(CellA, D(9, 15), 120m, 100m)];

        var vat = run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA, unknown, known);
        Assert.Equal(["A 15.09 +120.00/+120.00 vU"], Lines(vat[0]));
        Assert.Equal(["A 05.10 0.00/-20.00 vU"], Lines(vat[1]));

        var unpaid = run.Post(InvoiceBookKind.Unpayment, D(10, 6), ClosedA, known, []);
        Assert.Equal(["A 06.10 -120.00/-100.00"], Lines(Assert.Single(unpaid)));
    }

    /// <summary>S30 (б): с суммой — +12 по «с НДС» и −10 по «без»: net правит прежнее число-заглушку.</summary>
    [Fact]
    public void S30_НДС_стал_известен_вместе_с_суммой()
    {
        var run = new Run();
        var vat = run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA,
            [G(CellA, D(9, 15), 120m, vatUnknown: true)], [G(CellA, D(9, 15), 132m, 110m)]);

        Assert.Equal(["A 05.10 +12.00/-10.00 vU"], Lines(vat[1]));
    }

    /// <summary>
    /// S33 (пятая ревизия): B закрыта, A — никогда. После «НДС стал известен» пара (A, 15.09) в книге
    /// несёт признак, а документ — нет. Следующая разница ложится своим днём: признак в равенство не
    /// входит, иначе она ушла бы днём D и «на момент закрытия» A разошёлся бы с документом (I8).
    /// </summary>
    [Fact]
    public void S33_признак_НДС_не_уводит_разницу_открытого_контура_в_день_записи()
    {
        var closedB = Closed(B, D(9, 30));
        var run = new Run { NeverClosed = { new PeriodContour.Construction(A) } };
        Grain[] unknown = [G(CellB, D(9, 15), 120m, vatUnknown: true), G(CellA, D(9, 15), 120m, vatUnknown: true)];
        Grain[] known = [G(CellB, D(9, 15), 120m, 100m), G(CellA, D(9, 15), 120m, 100m)];

        var vat = run.Post(InvoiceBookKind.Correction, D(10, 5), closedB, unknown, known);
        Assert.Equal(Sorted("A 15.09 0.00/-20.00 vU", "B 05.10 0.00/-20.00 vU"), Lines(vat[1]));

        var more = run.Post(InvoiceBookKind.Correction, D(10, 6), closedB, known,
            [G(CellB, D(9, 15), 120m, 100m), G(CellA, D(9, 15), 132m, 110m)]);
        Assert.Equal(["A 15.09 +12.00/+10.00"], Lines(Assert.Single(more)));
    }
}
