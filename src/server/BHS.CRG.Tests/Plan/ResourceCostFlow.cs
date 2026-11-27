using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>Штатные границы проверки PLAN-32: файл → фоновый разбор → фиксация → новое чтение → контракт.
/// Денежные значения здесь не подставляются.</summary>
internal static class ResourceCostFlow
{
    public static async Task<(Guid Estimate, Guid Revision, JsonElement Report)> ImportAsync(
        HttpClient client, Guid construction, string sample, Guid? estimate = null, HttpClient? reader = null)
    {
        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(GrandSmetaSamples.Gsfx(sample), sample + ".gsfx", construction,
                estimateId: estimate, newName: estimate is null ? "Стоимость ресурсов" : null)));
        Assert.Equal("parsed", report.GetProperty("state").GetString());
        if (reader is not null) NoMoney(await EstimateImportFlow.GetAsync(reader, EstimateImportFlow.Id(report)));
        var response = await client.SendAsync(EstimateImportFlow.Commit(EstimateImportFlow.Id(report), report.GetProperty("version").GetString()));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var committed = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (reader == client) NoMoney(committed);
        return (committed.GetProperty("estimate").GetProperty("id").GetGuid(), committed.GetProperty("revisionId").GetGuid(), report);
    }

    public static async Task<List<EstimateRowMaterial>> StoredAsync(PlanOnlyHost host, Guid revision, string number)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        return await (from m in db.EstimateRowMaterials.AsNoTracking()
                      join r in db.EstimateRows.AsNoTracking() on m.RowId equals r.Id
                      where r.RevisionId == revision && r.Number == number
                      orderby m.Order
                      select m).ToListAsync();
    }

    public static async Task<JsonElement> MatchAsync(HttpClient client, Guid estimate, Guid revision, Guid material, Guid? nomenclature, bool remember = false)
    {
        var response = await client.SendAsync(ResourceMatchingTests.Match(revision, material,
            await RateMatchFlow.VersionAsync(client, estimate, revision), new { nomenclatureId = nomenclature, clear = nomenclature is null, remember }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<Guid> MatchWorkAsync(PlanOnlyHost host, HttpClient client, Guid estimate, Guid revision, JsonElement row)
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var response = await client.SendAsync(RateMatchFlow.MatchRow(revision, row.GetProperty("id").GetGuid(),
            await RateMatchFlow.VersionAsync(client, estimate, revision),
            new { workTypeId = c.WorkType, unitId = c.Unit, factor = 1m, remember = false }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("row").GetProperty("planItemId").GetGuid();
    }

    public static async Task<ContractAnswer<PlannedWorksAnswer>> ReadAsync(PlanOnlyHost host, PlannedWorksQuery query, params string[] keys)
    {
        using var scope = host.Services.CreateScope();
        var contract = Assert.IsType<ModuleContractAnswer<IPlannedWorks>.Connected>(scope.ServiceProvider
            .GetRequiredService<IModuleContracts>().Get<IPlannedWorks>()).Contract;
        return await contract.ReadAsync(ModuleReader.Of(Guid.NewGuid(), "Читатель стоимости", keys), query);
    }

    public static async Task<PlannedMaterialCost?> CostAsync(PlanOnlyHost host, PlannedWorksQuery query, Guid nomenclature)
    {
        var answer = Shown(await ReadAsync(host, query, PlanModule.EstimateCost));
        return Shown(answer.Cost).SelectMany(c => c.Materials).SingleOrDefault(c => c.NomenclatureId == nomenclature);
    }

    public static T Shown<T>(ContractAnswer<T> answer) => Assert.IsType<ContractAnswer<T>.Shown>(answer).Value;

    public static void NoMoneyParts(PlannedWorksAnswer answer) => NoMoney(JsonSerializer.SerializeToElement(new
    {
        volumes = Value(answer.Volumes), materials = Value(answer.Materials), labor = Value(answer.Labor), hint = Value(answer.Hint),
    }, EstimateSeed.Json));

    private static object? Value<T>(ContractAnswer<T> part) => part is ContractAnswer<T>.Shown shown ? shown.Value : null;

    public static async Task MakeCurrentAsync(HttpClient client, Guid estimate, Guid revision)
    {
        var current = await client.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}");
        var response = await client.SendAsync(RateMatchFlow.With(HttpMethod.Post, $"/api/plan/estimates/{estimate}/current-revision",
            current.GetProperty("version").GetString(), new { revisionId = revision }));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    public static void NoMoney(JsonElement json)
    {
        string[] forbidden = ["cost", "total", "unitPrice", "amount", "unitPriceBasis", "amountBasis"];
        if (json.ValueKind == JsonValueKind.Object)
            foreach (var property in json.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, forbidden, StringComparer.OrdinalIgnoreCase);
                NoMoney(property.Value);
            }
        else if (json.ValueKind == JsonValueKind.Array)
            foreach (var item in json.EnumerateArray()) NoMoney(item);
    }
}
