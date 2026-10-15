using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Tests.Costs;

/// <summary>Переносы, права-суммы, отмена оплаты и отмена закрытия — числа пятого прогона.</summary>
public partial class InvoiceBookPostingTests
{
    /// <summary>
    /// S01: обмен целей двух строк. Деньги A уходят из закрытого 15.09 днём исправления, а приходят
    /// новым днём доли (01.10) — своим днём; у B разницы нет вовсе.
    /// </summary>
    [Fact]
    public void S01_обмен_целей_двух_строк()
    {
        var run = new Run();
        var swap = run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA,
            [G(CellA, D(9, 15), 120m, 100m), G(CellB, D(9, 15), 120m, 100m)],
            [G(CellB, D(9, 15), 120m, 100m), G(CellA, D(10, 1), 120m, 100m)]);

        Assert.Equal(Sorted("A 05.10 -120.00/-100.00", "A 01.10 +120.00/+100.00"), Lines(swap[1]));
        Assert.Equal(120m, run.Close(OnA, 9));
    }

    /// <summary>S21 (г)–(е): сумма строки, отмена оплаты запертого, новая оплата — своим днём.</summary>
    [Fact]
    public void S21_сумма_строки_отмена_оплаты_и_новая_оплата()
    {
        var run = new Run();
        Grain[] at132 = [G(CellA, D(9, 15), 132m, 110m)];

        var fix = run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA, [G(CellA, D(9, 15), 120m, 100m)], at132);
        Assert.Equal(["A 15.09 +120.00/+100.00"], Lines(fix[0]));
        Assert.Equal(["A 05.10 +12.00/+10.00"], Lines(fix[1]));

        Assert.Equal(["A 06.10 -132.00/-110.00"],
            Lines(Assert.Single(run.Post(InvoiceBookKind.Unpayment, D(10, 6), ClosedA, at132, []))));

        Assert.Equal(["A 07.10 +132.00/+110.00"],
            Lines(Assert.Single(run.Post(InvoiceBookKind.Payment, D(10, 7), ClosedA, [], [G(CellA, D(10, 7), 132m, 110m)]))));
        Assert.Equal((120m, 12m), (run.Close(OnA, 9), run.Close(OnA, 10)));
    }

    /// <summary>
    /// S23: раздел, пришедший со стройки B, получает 01.10 (C5b); затем оба раздела сведены в «р3» —
    /// позднейший день отдавших. «На момент закрытия» сентябрь A держит 60.
    /// </summary>
    [Fact]
    public void S23_смена_раздела_и_сведение_разделов()
    {
        var run = new Run();
        Grain[] split = [G(Cell(A, R1), D(9, 15), 60m, 50m), G(Cell(A, R2), D(10, 1), 60m, 50m)];

        var first = run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA,
            [G(Cell(A, R1), D(9, 15), 60m, 50m), G(CellB, D(9, 15), 60m, 50m)], split);
        Assert.Equal(Sorted("B 15.09 -60.00/-50.00", "A/р2 01.10 +60.00/+50.00"), Lines(first[1]));

        var merged = run.Post(InvoiceBookKind.Correction, D(10, 6), ClosedA, split, [G(Cell(A, R3), D(10, 1), 120m, 100m)]);
        Assert.Equal(Sorted("A/р1 06.10 -60.00/-50.00", "A/р2 01.10 -60.00/-50.00", "A/р3 01.10 +120.00/+100.00"),
            Lines(Assert.Single(merged)));
        Assert.Equal((60m, 60m), (run.Close(OnA, 9), run.Close(OnA, 10)));
    }

    /// <summary>
    /// S27: деньги B влиты в существующую долю A/р1 — она держит 15.09 (названное следствие (α)).
    /// «С исправлениями» сентябрь A — 120, «на момент закрытия» — 60: приход лёг днём исправления.
    /// </summary>
    [Fact]
    public void S27_деньги_другой_стройки_в_существующей_доле()
    {
        var run = new Run();
        var merged = run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA,
            [G(Cell(A, R1), D(9, 15), 60m, 50m), G(CellB, D(9, 15), 60m, 50m)], [G(Cell(A, R1), D(9, 15), 120m, 100m)]);

        Assert.Equal(Sorted("B 15.09 -60.00/-50.00", "A/р1 05.10 +60.00/+50.00"), Lines(merged[1]));
        Assert.Equal((60m, 60m), (run.Close(OnA, 9), run.Close(OnA, 10)));
    }

    /// <summary>S28: смешанный перенос — A/р2 берёт 15.09 через A/р1 (60 → 30), обе пары закрыты → день D.</summary>
    [Fact]
    public void S28_смешанный_перенос()
    {
        var run = new Run();
        var mixed = run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA,
            [G(Cell(A, R1), D(9, 15), 60m, 50m), G(CellB, D(9, 15), 60m, 50m)],
            [G(Cell(A, R1), D(9, 15), 30m, 25m), G(Cell(A, R2), D(9, 15), 90m, 75m)]);

        Assert.Equal(Sorted("B 15.09 -60.00/-50.00", "A/р1 05.10 -30.00/-25.00", "A/р2 05.10 +90.00/+75.00"),
            Lines(mixed[1]));
    }

    /// <summary>S22: перенос между разделами одной стройки — оба раздела закрыты, обе строки днём D.</summary>
    [Fact]
    public void S22_перенос_между_разделами_одной_стройки()
    {
        var run = new Run();
        var moved = run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA,
            [G(Cell(A, R1), D(9, 15), 120m, 100m)], [G(Cell(A, R2), D(9, 15), 120m, 100m)]);

        Assert.Equal(Sorted("A/р1 05.10 -120.00/-100.00", "A/р2 05.10 +120.00/+100.00"), Lines(moved[1]));
        Assert.Equal((120m, 0m), (run.Close(OnA, 9), run.Close(OnA, 10)));
    }

    /// <summary>S26: в одной ячейке зёрна закрытого и открытого дня — закрытое днём D, открытое своим.</summary>
    [Fact]
    public void S26_закрытое_и_открытое_зерно_одной_ячейки()
    {
        var run = new Run();
        Grain[] both = [G(CellA, D(9, 15), 120m, 100m), G(CellA, D(10, 1), 120m, 100m)];
        run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA,
            [G(CellA, D(9, 15), 120m, 100m), G(CellB, D(9, 15), 120m, 100m)], both);

        var raised = run.Post(InvoiceBookKind.Correction, D(10, 6), ClosedA, both,
            [G(CellA, D(9, 15), 132m, 110m), G(CellA, D(10, 1), 132m, 110m)]);

        Assert.Equal(Sorted("A 01.10 +12.00/+10.00", "A 06.10 +12.00/+10.00"), Lines(Assert.Single(raised)));
        Assert.Equal(120m, run.Close(OnA, 9));
    }

    /// <summary>
    /// S17: исправление при закрытом месяце остаётся днём исправления и после отмены закрытия: пара
    /// (A, 15.09) в книге уже не равна документу, поэтому следующая разница — тоже днём D. Повторное
    /// закрытие запишет 120, как первое.
    /// </summary>
    [Fact]
    public void S17_после_отмены_закрытия_ничего_не_сбрасывается()
    {
        var run = new Run();
        Grain[] at132 = [G(CellA, D(9, 15), 132m, 110m)];
        run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA, [G(CellA, D(9, 15), 120m, 100m)], at132);

        var again = run.Post(InvoiceBookKind.Correction, D(10, 7), PeriodBoundaries.None, at132,
            [G(CellA, D(9, 15), 144m, 120m)]);

        Assert.Equal(["A 07.10 +12.00/+10.00"], Lines(Assert.Single(again)));
        Assert.Equal(120m, run.Close(OnA, 9));
    }

    /// <summary>S04: отмена оплаты после отмены закрытия — сентябрь 120, октябрь −120 (решение владельца).</summary>
    [Fact]
    public void S04_отмена_оплаты_после_отмены_закрытия()
    {
        var run = new Run();
        Grain[] at132 = [G(CellA, D(9, 15), 132m, 110m)];
        run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA, [G(CellA, D(9, 15), 120m, 100m)], at132);

        Assert.Equal(["A 07.10 -132.00/-110.00"],
            Lines(Assert.Single(run.Post(InvoiceBookKind.Unpayment, D(10, 7), PeriodBoundaries.None, at132, []))));
        Assert.Equal((120m, -120m), (run.Close(OnA, 9), run.Close(OnA, 10)));
    }

    /// <summary>S09а: A → B → A — октябрь A «на момент закрытия» в сумме 0,00 при двух строках.</summary>
    [Fact]
    public void S09а_туда_и_обратно()
    {
        var run = new Run();
        Grain[] onB = [G(CellB, D(9, 15), 120m, 100m)];
        run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA, [G(CellA, D(9, 15), 120m, 100m)], onB);
        var back = run.Post(InvoiceBookKind.Correction, D(10, 6), ClosedA, onB, [G(CellA, D(10, 1), 120m, 100m)]);

        Assert.Equal(Sorted("B 15.09 -120.00/-100.00", "A 01.10 +120.00/+100.00"), Lines(Assert.Single(back)));
        Assert.Equal((120m, 0m), (run.Close(OnA, 9), run.Close(OnA, 10)));
    }

    /// <summary>
    /// День записи D не раньше прежних записей счёта: пояс сдвинули назад, «сегодня» стало раньше
    /// прошлой записи — новая всё равно ложится не раньше её (I4′(в)).
    /// </summary>
    [Fact]
    public void День_записи_не_раньше_прежних_записей()
    {
        var run = new Run();
        Grain[] at132 = [G(CellA, D(9, 15), 132m, 110m)];
        run.Post(InvoiceBookKind.Correction, D(10, 6), ClosedA, [G(CellA, D(9, 15), 120m, 100m)], at132);

        var earlier = run.Post(InvoiceBookKind.Correction, D(10, 5), ClosedA, at132, [G(CellA, D(9, 15), 144m, 120m)]);

        Assert.Equal(D(10, 6), Assert.Single(earlier).Day);
    }

    /// <summary>
    /// Зёрна счёта — ТЕМ ЖЕ «без НДС», что у отчётов о затратах: НДС строки, иначе шапки; без НДС —
    /// полной суммой с признаком. Остаток — ячейка без цели, поставщик — у каждой ячейки.
    /// </summary>
    [Fact]
    public void Зёрна_счёта_считаются_правилом_отчётов()
    {
        var line = Guid.NewGuid();
        var part = InvoiceAllocation.Create(Guid.NewGuid(), line);
        part.Apply(1, new AllocationValues(AllocationTarget.Site(A), 1m, null));
        part.Post(D(9, 15));
        var invoice = new CostInvoice(Guid.NewGuid(), Supplier, 150m, 25m, false, false,
            [new PostedMoney(part, 120m, D(9, 15)), new PostedMoney(null, 30m, D(9, 15)), new PostedMoney(part, null, D(9, 15))]);

        var grains = InvoiceBookPosting.Grains(invoice, new Dictionary<Guid, LineVat>());

        Assert.Equal([G(CellA, D(9, 15), 120m, 100m), G(Rest, D(9, 15), 30m, 25m)], grains);
        var blind = InvoiceBookPosting.Grains(invoice with { VatTotal = null }, new Dictionary<Guid, LineVat>())[0];
        Assert.Equal((120m, true), (blind.Net, blind.VatUnknown));
    }
}
