using System.Globalization;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using static BHS.CRG.Modules.Costs.Data.InvoiceRequisites;

namespace BHS.CRG.Tests.Costs;

public sealed class InvoicePaymentRequisitesTests
{
    [Theory]
    [InlineData(SupplierKey)]
    [InlineData(NumberKey)]
    [InlineData(DateKey)]
    [InlineData(TotalKey)]
    public void Каждый_обязательный_реквизит(string field)
    {
        var invoice = Create(field != SupplierKey, field == NumberKey ? null : "СЧ-1",
            field != DateKey, field == TotalKey ? null : 100m);

        var gap = Assert.Single(InvoicePaymentRequisites.Find(invoice, ReferenceState.Present));
        Assert.Equal(field, gap.Field);
        Assert.Equal("missing", gap.Kind);
        Assert.Contains(TitleOf(field), gap.Label);
    }

    [Fact]
    public void Потерянный_поставщик_и_остальные_пробелы_возвращаются_вместе()
    {
        var invoice = Create(true, " \t ", false, 0m);

        var gaps = InvoicePaymentRequisites.Find(invoice, ReferenceState.Lost);
        Assert.Equal([SupplierKey, NumberKey, DateKey, TotalKey], gaps.Select(g => g.Field));
        Assert.Equal(["lost", "missing", "missing", "missing"], gaps.Select(g => g.Kind));
        Assert.Contains(TitleOf(SupplierKey), gaps[0].Label);
    }

    [Theory]
    [InlineData(ReferenceState.Present, "100")]
    [InlineData(ReferenceState.Archived, "100")]
    [InlineData(ReferenceState.Archived, "-100")]
    public void Плательщик_и_строки_не_обязательны_для_оплаты(ReferenceState supplierState, string amount)
    {
        var invoice = Create(true, "СЧ-1", true, decimal.Parse(amount, CultureInfo.InvariantCulture));

        Assert.Null(invoice.PayerId);
        Assert.Equal([PayerKey], MissingFields(invoice).Select(field => field.Key));
        Assert.Empty(InvoicePaymentRequisites.Find(invoice, supplierState));
    }

    private static Invoice Create(bool supplier, string? number, bool date, decimal? total)
    {
        var invoice = Invoice.Create(Guid.NewGuid(), null, null);
        invoice.ApplyRequisites(
            new InvoiceColumns(number, date ? new DateOnly(2026, 11, 20) : null, supplier ? Guid.NewGuid() : null,
                null, null, total, null, null, null, null),
            JsonDocument.Parse("{}"), dueDateByHand: true, new InvoiceDueRule(true));
        return invoice;
    }
}
