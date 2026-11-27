using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>Решение о принятии импорта, начатого до обновления. Меняется только форма сохранённого
/// JSON, денежные числа в тесте не создаются и не заменяются.</summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateImportResourceCostCompatibilityTests(PlanOnlyHost host)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Независимые_основы_проходят_промежуточный_JSON_и_фиксацию(bool priceUnknown)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.CostEditorAsync(host);
        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            site.Construction, "lsr-16.2.2-sks"));
        var importId = EstimateImportFlow.Id(report);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            var parsed = JsonNode.Parse((await db.EstimateImports.AsNoTracking().SingleAsync(i => i.Id == importId)).Parsed!)!;
            var washer = parsed["chapters"]!.AsArray().SelectMany(c => c!["positions"]!.AsArray())
                .Single(p => p!["number"]!.GetValue<string>() == "85")!;
            // Меняется только подтверждение одной величины; все числа остаются взятыми из файла.
            washer["ownMaterialCost"]!.AsObject().Remove(priceUnknown ? "unitPriceBasis" : "amountBasis");
            var json = parsed.ToJsonString();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plan.estimate_imports SET parsed = {json}::jsonb WHERE id = {importId}");
        }
        report = await EstimateImportFlow.GetAsync(client, importId);
        var response = await client.SendAsync(EstimateImportFlow.Commit(importId, report.GetProperty("version").GetString()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var revision = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revisionId").GetGuid();
        var stored = Assert.Single(await ResourceCostFlow.StoredAsync(host, revision, "85"));
        Assert.Equal(5.01m, stored.UnitPrice);
        Assert.Equal(388.38m, stored.Amount);
        Assert.Equal(priceUnknown ? ResourceTaxBasis.Unknown : ResourceTaxBasis.ExcludingVat, stored.UnitPriceBasis);
        Assert.Equal(priceUnknown ? ResourceTaxBasis.ExcludingVat : ResourceTaxBasis.Unknown, stored.AmountBasis);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Старый_отчёт_принимается_и_двойное_нажатие_не_создаёт_вторую_редакцию(bool keepNumbers)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.CostEditorAsync(host);
        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            site.Construction, "lsr-16.2.2-sks"));
        var importId = EstimateImportFlow.Id(report);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            var parsed = JsonNode.Parse((await db.EstimateImports.AsNoTracking().SingleAsync(i => i.Id == importId)).Parsed!)!;
            foreach (var chapter in parsed["chapters"]!.AsArray())
                foreach (var position in chapter!["positions"]!.AsArray())
                {
                    Legacy(position!.AsObject(), "ownMaterialCost");
                    foreach (var material in position["materials"]!.AsArray()) Legacy(material!.AsObject(), "cost");
                }
            var json = parsed.ToJsonString();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE plan.estimate_imports SET parsed = {json}::jsonb WHERE id = {importId}");
        }
        report = await EstimateImportFlow.GetAsync(client, importId); // версия сдвинулась при имитации старого JSON
        var replies = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.SendAsync(
            EstimateImportFlow.Commit(importId, report.GetProperty("version").GetString()))));
        Assert.Contains(replies, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(replies, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Conflict));
        var answers = await Task.WhenAll(replies.Where(r => r.IsSuccessStatusCode).Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));
        var revision = Assert.Single(answers.Select(a => a.GetProperty("revisionId").GetGuid()).Distinct());
        var fresh = await EstimateImportFlow.GetAsync(client, importId);
        var repeated = await client.SendAsync(EstimateImportFlow.Commit(importId, fresh.GetProperty("version").GetString()));
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(revision, (await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revisionId").GetGuid());
        var washer = Assert.Single(await ResourceCostFlow.StoredAsync(host, revision, "85"));
        Assert.Equal(keepNumbers ? 5.01m : (decimal?)null, washer.UnitPrice);
        Assert.Equal(keepNumbers ? 388.38m : (decimal?)null, washer.Amount);
        Assert.Equal(ResourceTaxBasis.Unknown, washer.UnitPriceBasis);
        Assert.Equal(ResourceTaxBasis.Unknown, washer.AmountBasis);
        using var verifyScope = host.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var import = await verify.EstimateImports.AsNoTracking().SingleAsync(i => i.Id == importId);
        Assert.Null(import.Parsed);
        Assert.Equal(1, await verify.EstimateRevisions.CountAsync(r => r.ImportKey == importId.ToString()));

        void Legacy(JsonObject owner, string property)
        {
            if (!keepNumbers) { owner.Remove(property); return; }
            if (owner[property] is not JsonObject cost) return;
            cost.Remove("unitPriceBasis");
            cost.Remove("amountBasis");
        }
    }
}
