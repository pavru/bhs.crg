using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Costs;

[Collection(CostsOnlyCollection.Name)]
public sealed class PurchaseRequestWithoutPlanTests(CostsOnlyHost host)
{
    [Fact]
    public async Task Без_Plan_новая_заявка_отказывает_а_существующая_читается()
    {
        using var client = await AccessProbe.SignInAsync(host, "Supplier", "plan26");
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var stored = PurchaseRequest.Create(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        db.PurchaseRequests.Add(stored);
        await db.SaveChangesAsync();

        const string path = PurchaseRequestTests.Requests;
        var body = new { constructionId = stored.ConstructionId, planItemIds = new[] { Guid.NewGuid() }, lines = Array.Empty<object>() };
        foreach (var route in new[] { path, path + "/draft-from-estimate" })
        {
            var refusal = await PurchaseRequestTests.ReadAsync(await client.PostAsJsonAsync(route, body), HttpStatusCode.Conflict);
            Assert.Contains("нужен модуль «Планирование»", refusal.GetProperty("error").GetString());
        }
        var read = await client.GetFromJsonAsync<JsonElement>($"{path}/{stored.Id}");
        Assert.Equal(stored.Id, read.GetProperty("id").GetGuid());
    }
}

[Collection(PlanOnlyCollection.Name)]
public sealed class PurchaseRequestWithoutCostsTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Без_Costs_адрес_заявок_закрыт_воротами_модуля()
    {
        using var client = await AccessProbe.SignInAsync(host, "Admin", "plan26");
        var refusal = await PurchaseRequestTests.ReadAsync(await client.GetAsync(PurchaseRequestTests.Requests), HttpStatusCode.NotImplemented);
        Assert.Equal("costs", refusal.GetProperty("module").GetString());
    }
}
