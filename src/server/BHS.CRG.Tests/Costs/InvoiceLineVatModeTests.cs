using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Строка счёта по режиму НДС (issue #1497): сумма строки С НДС — всегда, в обоих режимах; бумажная
/// сумма без НДС при НДС сверху — рядом, своим полем.
/// </summary>
public class InvoiceLineVatModeTests
{
    private static InvoiceLineValues Paper(
        decimal? quantity = null, decimal? price = null, decimal? rate = null, decimal? vat = null,
        decimal? amount = null, decimal? net = null) =>
        new(null, "Кабель", null, "м", quantity, price, rate, vat, amount, null, NetAmount: net);

    [Fact]
    public void В_том_числе_считается_как_прежде()
    {
        var line = Paper(amount: 1220m, rate: 22m).Completed(VatMode.Included).Values;

        Assert.Equal(1220m, line.Amount);
        Assert.Equal(220m, line.VatAmount);
        Assert.Null(line.NetAmount);
    }

    [Fact]
    public void Сверху_НДС_прибавляется_к_бумажной_сумме()
    {
        var line = Paper(net: 1000m, rate: 22m).Completed(VatMode.OnTop).Values;

        Assert.Equal(1000m, line.NetAmount);
        Assert.Equal(220m, line.VatAmount);
        Assert.Equal(1220m, line.Amount);
    }

    /// <summary>Бумажной суммы нет — сумма без НДС есть количество × цена, как у «в том числе».</summary>
    [Fact]
    public void Сверху_без_суммы_считается_от_количества_и_цены()
    {
        var line = Paper(quantity: 2m, price: 500m, rate: 22m).Completed(VatMode.OnTop).Values;

        Assert.Equal(1000m, line.NetAmount);
        Assert.Equal(1220m, line.Amount);
    }

    /// <summary>
    /// НДС неизвестен — суммы с НДС нет, а не бумажная без НДС: её прочли бы как сумму с НДС все, кто
    /// читает <c>amount</c>, и ошиблись бы на весь налог молча.
    /// </summary>
    [Fact]
    public void Сверху_без_ставки_суммы_с_НДС_нет()
    {
        var line = Paper(net: 1000m).Completed(VatMode.OnTop).Values;

        Assert.Equal(1000m, line.NetAmount);
        Assert.Null(line.VatAmount);
        Assert.Null(line.Amount);
    }

    /// <summary>Присланная сумма НДС не пересчитывается — и без ставки складывается с бумажной.</summary>
    [Fact]
    public void Сверху_присланный_НДС_ложится_как_есть()
    {
        var line = Paper(net: 1000m, rate: 22m, vat: 219.99m).Completed(VatMode.OnTop).Values;

        Assert.Equal(219.99m, line.VatAmount);
        Assert.Equal(1219.99m, line.Amount);
    }

    /// <summary>НДС — построчно, копейки «от нуля»: 333,33 × 22 % = 73,3326 → 73,33.</summary>
    [Fact]
    public void Сверху_НДС_округляется_у_строки()
    {
        var line = Paper(net: 333.33m, rate: 22m).Completed(VatMode.OnTop).Values;

        Assert.Equal(73.33m, line.VatAmount);
        Assert.Equal(406.66m, line.Amount);
    }

    /// <summary>Последняя дверь против удвоения НДС: бумажная сумма не в своём поле — ошибка кода.</summary>
    [Fact]
    public void Бумажная_сумма_не_в_своём_поле_не_досчитывается()
    {
        Assert.Throws<ArgumentException>(() => Paper(amount: 1220m).Completed(VatMode.OnTop));
        Assert.Throws<ArgumentException>(() => Paper(net: 1000m).Completed(VatMode.Included));
    }

    /// <summary>Тот же запрет человеку — отказом разбора, с полем своего режима.</summary>
    [Fact]
    public void Чужое_поле_суммы_отказ_с_полем_своего_режима()
    {
        var onTop = Assert.Throws<InvalidRequestException>(() =>
            InvoiceLineRequests.Complete(Paper(amount: 1220m), 3, VatMode.OnTop));
        Assert.Contains("Строка 3", onTop.Message);
        Assert.Contains("«netAmount»", onTop.Message);

        var included = Assert.Throws<InvalidRequestException>(() =>
            InvoiceLineRequests.Complete(Paper(net: 1000m), 1, VatMode.Included));
        Assert.Contains("«amount»", included.Message);
        Assert.Contains("vatMode", included.Message);
    }

    [Fact]
    public void Итоги_сверху_называют_бумажную_сумму_и_источник_НДС()
    {
        var invoice = Invoice.Create(Guid.NewGuid(), null);
        invoice.ChangeVatMode(VatMode.OnTop);
        InvoiceLine Line(decimal net, decimal? rate)
        {
            var line = InvoiceLine.Create(invoice.Id);
            line.Apply(1, Paper(net: net, rate: rate).Completed(VatMode.OnTop));
            return line;
        }

        var all = InvoiceLineTotals.Of(invoice, [Line(1000m, 22m), Line(500m, 22m)]);
        Assert.Equal(VatMode.OnTop, all.Mode);
        Assert.Equal(1500m, all.Net);
        Assert.Equal(330m, all.Vat);
        Assert.Equal(1830m, all.Amount);
        Assert.Equal("lines", all.VatFrom);

        // Строка без ставки: суммы с НДС у неё нет — в сумму строк она не входит, а бумажная — входит.
        var blind = InvoiceLineTotals.Of(invoice, [Line(1000m, 22m), Line(500m, null)]);
        Assert.Equal(1500m, blind.Net);
        Assert.Equal(1220m, blind.Amount);
        Assert.Equal(1, blind.VatMissing);
        Assert.Null(blind.VatFrom);
    }

    /// <summary>
    /// Версия счёта хранит бумагу счёта сверху — режим и сумму без НДС: без них история версий теряла бы
    /// число, с которым человек сверял счёт, — сумма с НДС у такого счёта производная.
    /// </summary>
    [Fact]
    public void Версия_хранит_режим_и_бумажную_сумму()
    {
        var invoice = Invoice.Create(Guid.NewGuid(), null);
        invoice.ChangeVatMode(VatMode.OnTop);
        var line = InvoiceLine.Create(invoice.Id);
        line.Apply(1, Paper(net: 1000m, rate: 22m).Completed(VatMode.OnTop));

        using var content = System.Text.Json.JsonDocument.Parse(InvoiceVersion.Snapshot(invoice, [line], [], []));
        var root = content.RootElement;

        Assert.Equal("OnTop", root.GetProperty("vatMode").GetString());
        Assert.Equal(1000m, root.GetProperty("lines")[0].GetProperty("netAmount").GetDecimal());
        Assert.Equal(1220m, root.GetProperty("lines")[0].GetProperty("amount").GetDecimal());
    }

    /// <summary>У «в том числе» бумажной суммы без НДС нет — и итоги её не выдумывают.</summary>
    [Fact]
    public void Итоги_в_том_числе_без_суммы_без_НДС()
    {
        var invoice = Invoice.Create(Guid.NewGuid(), null);
        var line = InvoiceLine.Create(invoice.Id);
        line.Apply(1, Paper(amount: 1220m, rate: 22m).Completed(VatMode.Included));

        var totals = InvoiceLineTotals.Of(invoice, [line]);

        Assert.Equal(VatMode.Included, totals.Mode);
        Assert.Null(totals.Net);
    }
}
