using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Итоги по строкам счёта: НДС, не указанный в строках, — не ноль (issue #1409).
///
/// <para>Счёт, где НДС стоит лишь в итогах бумаги, — а так выглядит каждый счёт из файла, — показывал
/// под строками «в том числе НДС: 0,00» рядом с шапкой, где НДС распознан верно. Сумма НДС здесь
/// по-прежнему складывается из указанных, но рядом стоит число строк, о которых она молчит: без него
/// «не указан ни у одной», «у части» и «у всех» в ответе неотличимы.</para>
/// </summary>
public class InvoiceLineTotalsTests
{
    private static readonly Invoice Invoice = Invoice.Create(Guid.NewGuid(), null);

    private static InvoiceLine Line(decimal amount, decimal? rate)
    {
        var line = InvoiceLine.Create(Invoice.Id);
        // Сумма НДС не прислана: её досчитывает Completed по ставке — как при записи строки.
        line.Apply(1, new InvoiceLineValues(
            null, "Кабель", null, "м", 1m, amount, rate, null, amount, null).Completed(VatMode.Included));
        return line;
    }

    [Fact]
    public void НДС_не_указан_ни_у_одной_строки()
    {
        var totals = InvoiceLineTotals.Of(Invoice, [Line(1200m, null), Line(600m, null)]);

        Assert.Equal(0m, totals.Vat);
        Assert.Equal(2, totals.VatMissing);
    }

    [Fact]
    public void НДС_указан_у_части_строк()
    {
        var totals = InvoiceLineTotals.Of(Invoice, [Line(1220m, 22m), Line(600m, null)]);

        Assert.Equal(220m, totals.Vat);
        Assert.Equal(1, totals.VatMissing);
    }

    [Fact]
    public void НДС_указан_у_всех_строк()
    {
        var totals = InvoiceLineTotals.Of(Invoice, [Line(1220m, 22m), Line(1100m, 10m)]);

        Assert.Equal(320m, totals.Vat);
        Assert.Equal(0, totals.VatMissing);
    }

    /// <summary>«Без НДС» — ставка 0 — это ноль, а не пропуск: строка такая НДС назвала.</summary>
    [Fact]
    public void Ставка_ноль_считается_указанной()
    {
        var totals = InvoiceLineTotals.Of(Invoice, [Line(1200m, 0m)]);

        Assert.Equal(0m, totals.Vat);
        Assert.Equal(0, totals.VatMissing);
    }
}
