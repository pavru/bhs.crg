using System.Net.Http.Json;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

public sealed partial class InvoiceFromScanTests
{
    [Fact]
    public async Task InvoiceDuplicatePayment_Распознавание_присоединяет_счёт_к_совпадающей_паре()
    {
        var (client, _) = await SignInAsync("Admin");
        var existing = await CreateAsync(client, complete: true);
        var view = await ReadAsync(client, existing);
        var number = view.GetProperty("requisites").GetProperty("Номер").GetString()!;
        var scan = Scan();
        host.Recognition.On(scan, () => Task.FromException<ModuleRecognitionResult>(
            new RecognitionRefusedException(RecognitionRefusal.Unavailable, "Повторите чтение.")));
        var created = await FromScanAsync(client, scan);
        var other = created.GetProperty("invoice").GetProperty("id").GetGuid();
        Assert.Equal("failed", (await OutcomeAsync(client, other)).GetProperty("state").GetString());
        var requisites = await RequisitesWithAsync(client, other, "Поставщик", Reference(supplier));
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{other}", new { requisites }));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var previousRevision = await db.Invoices.Where(i => i.Id == other).Select(i => i.DuplicateRevision).SingleAsync();
        host.Recognition.On(scan, () => Task.FromResult(Read(Header(number: number, date: "2026-09-29"))));
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{other}/recognition", null));
        Assert.Equal("done", (await OutcomeAsync(client, other)).GetProperty("state").GetString());

        var match = await ReadAsync(client, existing);
        var blocker = Assert.Single(match.GetProperty("payment").GetProperty("duplicateBlockers").EnumerateArray());
        Assert.Equal(other, blocker.GetProperty("id").GetGuid());
        Assert.NotEqual(previousRevision, blocker.GetProperty("otherRevision").GetGuid());
    }
}
