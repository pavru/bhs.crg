using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Plan;

/// <summary>Настоящий сквозной импорт PLAN-32. Числа приходят исключительно из исходных файлов;
/// сопоставление и распределение выполняются штатными HTTP-командами.</summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateImportResourceCostTests(PlanOnlyHost host)
{
    [Theory]
    [InlineData("lsr-16.2.2-sks")]
    [InlineData("lsr-16.3.0-sks-no-itog")]
    public async Task Шайба_от_файла_до_плановых_работ_сохраняет_цену_и_сумму(string sample)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var costEditor = await EstimateImportFlow.CostEditorAsync(host);
        var editor = await EstimateImportFlow.EditorAsync(host);
        var (estimate, revision, report) = await ResourceCostFlow.ImportAsync(editor, site.Construction, sample, reader: editor);
        var washer = Assert.Single(await ResourceCostFlow.StoredAsync(host, revision, "85"));
        Assert.Equal(5.01m, washer.UnitPrice);
        Assert.Equal(388.38m, washer.Amount);
        Assert.Equal(ResourceTaxBasis.ExcludingVat, washer.UnitPriceBasis);
        Assert.Equal(ResourceTaxBasis.ExcludingVat, washer.AmountBasis);
        var concrete = Assert.Single(await ResourceCostFlow.StoredAsync(host, revision, "107"));
        Assert.Equal(1076.11m, concrete.Amount);
        Assert.Null(concrete.UnitPrice);
        var nomenclature = await new ResourceMatchingTests(host).NomenclatureAsync($"Шайба {Guid.NewGuid():N}");
        if (washer.NomenclatureId is not null)
            await ResourceCostFlow.MatchAsync(editor, estimate, revision, washer.Id, null);
        Assert.Null(await ResourceCostFlow.CostAsync(host, new(site.Construction), nomenclature));
        var matched = await ResourceCostFlow.MatchAsync(editor, estimate, revision, washer.Id, nomenclature, remember: true);
        ResourceCostFlow.NoMoney(matched); // ответ команды без права на себестоимость
        var cost = await ResourceCostFlow.CostAsync(host, new(site.Construction), nomenclature);
        Assert.Equal(new PlannedMaterialCost(nomenclature, 388.38m, ResourceTaxBasis.ExcludingVat), cost);
        Assert.Null(await ResourceCostFlow.CostAsync(host, new(site.Construction, PlanItemIds: [Guid.NewGuid()]), nomenclature));

        var other = await new ResourceMatchingTests(host).NomenclatureAsync($"Другая шайба {Guid.NewGuid():N}");
        await ResourceCostFlow.MatchAsync(costEditor, estimate, revision, washer.Id, other);
        Assert.Null(await ResourceCostFlow.CostAsync(host, new(site.Construction), nomenclature));
        Assert.Equal(388.38m, (await ResourceCostFlow.CostAsync(host, new(site.Construction), other))!.Amount);
        var reread = Assert.Single(await ResourceCostFlow.StoredAsync(host, revision, "85"));
        Assert.Equal((washer.Id, washer.UnitPrice, washer.Amount, washer.UnitPriceBasis, washer.AmountBasis),
            (reread.Id, reread.UnitPrice, reread.Amount, reread.UnitPriceBasis, reread.AmountBasis));

        // Запомненное сопоставление во втором импорте не теряет ресурсные деньги и не меняет действующую.
        var next = await ResourceCostFlow.ImportAsync(costEditor, site.Construction, sample, estimate);
        var automatic = Assert.Single(await ResourceCostFlow.StoredAsync(host, next.Revision, "85"));
        Assert.Equal(nomenclature, automatic.NomenclatureId);
        Assert.Equal(388.38m, automatic.Amount);
        Assert.Equal(revision, await EstimateSeed.CurrentRevisionAsync(host, estimate));

        foreach (var path in new[] { $"/api/plan/imports/{EstimateImportFlow.Id(report)}", $"/api/plan/revisions/{revision}/rows",
                     $"/api/plan/revisions/{revision}/materials" })
            ResourceCostFlow.NoMoney(await editor.GetFromJsonAsync<JsonElement>(path));
        foreach (var keys in new[] { new[] { PlanModule.EstimateMaterials }, new[] { PlanModule.EstimateHint },
                     new[] { PlanModule.LaborRead }, new[] { PlanModule.EstimateRead }, Array.Empty<string>() })
        {
            var answer = ResourceCostFlow.Shown(await ResourceCostFlow.ReadAsync(host, new(site.Construction), keys));
            Assert.IsType<ContractAnswer<IReadOnlyList<PlannedCost>>.Hidden>(answer.Cost);
            ResourceCostFlow.NoMoneyParts(answer);
        }
    }

    [Theory]
    [InlineData("lsr-16.2.3-rim1")]
    [InlineData("lsr-16.3.0-rim1-no-itog")]
    public async Task Два_ресурса_работы_имеют_свои_суммы_и_доли_раздела_без_удвоения_редакций(string sample)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.CostEditorAsync(host);
        var (estimate, revision, _) = await ResourceCostFlow.ImportAsync(client, site.Construction, sample);
        var resources = await ResourceCostFlow.StoredAsync(host, revision, "1");
        Assert.Equal(new decimal?[] { 12.64m, 214.14m }, resources.Select(m => m.Amount));
        Assert.Equal(new decimal?[] { 38.42m, 1514.46m }, resources.Select(m => m.UnitPrice));
        var water = await new ResourceMatchingTests(host).NomenclatureAsync($"Вода {Guid.NewGuid():N}");
        var drill = await new ResourceMatchingTests(host).NomenclatureAsync($"Сверло {Guid.NewGuid():N}");
        await ResourceCostFlow.MatchAsync(client, estimate, revision, resources[0].Id, water);
        await ResourceCostFlow.MatchAsync(client, estimate, revision, resources[1].Id, drill);
        var work = (await RateMatchFlow.RowsAsync(client, revision)).Single(r => r.GetProperty("number").GetString() == "1");
        Assert.Null(await ResourceCostFlow.CostAsync(host, new(site.Construction), water)); // работа пока не сопоставлена
        var item = await ResourceCostFlow.MatchWorkAsync(host, client, estimate, revision, work);
        await DistributionFlow.LinesAsync(client, revision, DistributionFlow.Line(work.GetProperty("id").GetGuid(), "row", (site.Section, 0.035m)));
        await CheckAsync();

        var next = await ResourceCostFlow.ImportAsync(client, site.Construction, sample, estimate);
        Assert.Equal(revision, await EstimateSeed.CurrentRevisionAsync(host, estimate));
        var nextResources = await ResourceCostFlow.StoredAsync(host, next.Revision, "1");
        await ResourceCostFlow.MatchAsync(client, estimate, next.Revision, nextResources[0].Id, water);
        await ResourceCostFlow.MatchAsync(client, estimate, next.Revision, nextResources[1].Id, drill);
        var nextWork = (await RateMatchFlow.RowsAsync(client, next.Revision)).Single(r => r.GetProperty("number").GetString() == "1");
        await ResourceCostFlow.MatchWorkAsync(host, client, estimate, next.Revision, nextWork);
        var carried = await DistributionFlow.GetAsync(client, next.Revision);
        Assert.Single(DistributionFlow.LineOf(carried, nextWork.GetProperty("id").GetGuid()).GetProperty("parts").EnumerateArray());
        await CheckAsync(); // не действующая редакция не удваивает деньги
        await ResourceCostFlow.MakeCurrentAsync(client, estimate, next.Revision);
        await CheckAsync(); // прежняя редакция тоже перестаёт входить в сумму

        async Task CheckAsync()
        {
            Assert.Equal(new PlannedMaterialCost(water, 12.64m, ResourceTaxBasis.ExcludingVat), await ResourceCostFlow.CostAsync(host, new(site.Construction), water));
            Assert.Equal(214.14m, (await ResourceCostFlow.CostAsync(host, new(site.Construction), drill))!.Amount);
            Assert.Equal(6.32m, (await ResourceCostFlow.CostAsync(host, new(site.Construction, site.Section), water))!.Amount);
            Assert.Equal(107.07m, (await ResourceCostFlow.CostAsync(host, new(site.Construction, site.Section), drill))!.Amount);
            Assert.Equal(12.64m, (await ResourceCostFlow.CostAsync(host, new(site.Construction, PlanItemIds: [item]), water))!.Amount);
        }
    }
}
