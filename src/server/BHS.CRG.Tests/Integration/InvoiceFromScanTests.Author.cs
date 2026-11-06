using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Integration;

public sealed partial class InvoiceFromScanTests
{
    /// <summary>
    /// Поля в черновик положило распознавание — автор последней записи оно, а не тот, кто приложил скан
    /// (COST-6.4, Э4): «Дописало распознавание · 12:04». Имени у такого автора нет.
    /// </summary>
    [Fact]
    public async Task Распознавание_названо_автором_своей_записи()
    {
        var (client, _) = await SignInAsync("Supplier");
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromResult(Read(
            Header(number: "СЧ-418", date: "13 марта 2026 г.", total: "1 000,00 руб.", vat: "166,67",
                basis: null, supplier: "ООО «Кабель-Торг»", supplierTaxId: "7701234567"),
            Row("Кабель ВВГнг-LS 3х2,5", "м", "10", "100,00", "1 000,00"))));

        var created = await FromScanAsync(client, scan, "Счёт 418.pdf");
        var id = created.GetProperty("invoice").GetProperty("id").GetGuid();
        // Черновик завёл человек — до распознавания автор он.
        Assert.Equal(InvoiceAuthor.User,
            created.GetProperty("invoice").GetProperty("updatedBy").GetProperty("via").GetString());

        Assert.Equal("done", (await OutcomeAsync(client, id)).GetProperty("state").GetString());

        var author = (await ReadAsync(client, id)).GetProperty("updatedBy");
        Assert.Equal(InvoiceAuthor.Recognition, author.GetProperty("via").GetString());
        Assert.Equal(JsonValueKind.Null, author.GetProperty("name").ValueKind);
        Assert.False(author.GetProperty("mine").GetBoolean());
    }
}
