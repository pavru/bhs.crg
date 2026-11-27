using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Plan;

/// <summary>Независимые основы цены и суммы после сохранения и повторного чтения (P2 плана PLAN-32).</summary>
[Collection(PlanOnlyCollection.Name)]
public class RevisionMaterialsResourceCostTests(PlanOnlyHost host)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Групповая_цена_и_сумма_независимо_проверяют_подтверждение_основы(bool secondPriceKnown, bool firstAmountKnown)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var code = Guid.NewGuid().ToString("N");
        var priceBasis = secondPriceKnown ? ResourceTaxBasis.ExcludingVat : ResourceTaxBasis.Unknown;
        var amountBasis = firstAmountKnown ? ResourceTaxBasis.ExcludingVat : ResourceTaxBasis.Unknown;
        var revision = await EstimateSeed.RevisionAsync(host, estimate,
            new EstimateRowDraft("Работа 1", Number: "1", Materials: [new("Материал", 2m, code, "шт", UnitPrice: 100m, Amount: 200m,
                UnitPriceBasis: ResourceTaxBasis.ExcludingVat, AmountBasis: amountBasis)]),
            new EstimateRowDraft("Работа 2", Number: "2", Materials: [new("Материал", 3m, code, "шт", UnitPrice: 100m, Amount: 300m,
                UnitPriceBasis: priceBasis, AmountBasis: ResourceTaxBasis.ExcludingVat)]));
        var first = Assert.Single(await ResourceCostFlow.StoredAsync(host, revision.Id, "1"));
        var second = Assert.Single(await ResourceCostFlow.StoredAsync(host, revision.Id, "2"));
        Assert.Equal((100m, 200m, ResourceTaxBasis.ExcludingVat, amountBasis), (first.UnitPrice, first.Amount, first.UnitPriceBasis, first.AmountBasis));
        Assert.Equal((100m, 300m, priceBasis, ResourceTaxBasis.ExcludingVat), (second.UnitPrice, second.Amount, second.UnitPriceBasis, second.AmountBasis));
        var client = await EstimateImportFlow.CostEditorAsync(host);
        var groups = await client.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{revision.Id}/materials");
        var cost = Assert.Single(groups.GetProperty("items").EnumerateArray()).GetProperty("cost");
        Assert.Equal(secondPriceKnown ? JsonValueKind.Number : JsonValueKind.Null, cost.GetProperty("unitPrice").ValueKind);
        if (secondPriceKnown) Assert.Equal(100m, cost.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(priceBasis.ToString(), cost.GetProperty("unitPriceBasis").GetString());
        Assert.Equal(500m, cost.GetProperty("amount").GetDecimal());
        Assert.Equal(amountBasis.ToString(), cost.GetProperty("amountBasis").GetString());
    }

    [Theory]
    [InlineData(null, false, null, false)]
    [InlineData(0, true, 100, true)]
    [InlineData(20, false, 120, false)]
    public async Task Неизвестная_сумма_не_равна_нулю_а_неизвестная_основа_не_удаляет_число(
        int? second, bool secondNet, int? expected, bool expectedNet)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var c = await EstimateSeed.ClassifierAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var draft = await EstimateSeed.MatchedRowAsync(host, site, 100m);
        var revision = await EstimateSeed.RevisionAsync(host, estimate, draft with
        {
            Materials = [new("Материал 1", 1m, NomenclatureId: c.Nomenclature, Amount: 100m, AmountBasis: ResourceTaxBasis.ExcludingVat),
                new("Материал 2", 1m, NomenclatureId: c.Nomenclature, Amount: second,
                    AmountBasis: secondNet ? ResourceTaxBasis.ExcludingVat : ResourceTaxBasis.Unknown)],
        });
        var cost = await ResourceCostFlow.CostAsync(host, new(site.Construction), c.Nomenclature);
        Assert.Equal(expected is { } n ? (decimal?)n : null, cost!.Amount);
        Assert.Equal(expectedNet ? ResourceTaxBasis.ExcludingVat : ResourceTaxBasis.Unknown, cost.AmountBasis);
        Assert.Equal(expected is null, cost.NotInEstimate);
        var client = await EstimateImportFlow.EditorAsync(host);
        ResourceCostFlow.NoMoney(await client.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{revision.Id}/materials"));
    }

    [Fact]
    public async Task Доля_раздела_округляет_каждый_вклад_по_договору_Plan23()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var c = await EstimateSeed.ClassifierAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var draft = await EstimateSeed.MatchedRowAsync(host, site, 200m);
        var revision = await EstimateSeed.RevisionAsync(host, estimate, draft with
        {
            Materials = [new("Первый", 1m, NomenclatureId: c.Nomenclature, Amount: 0.01m, AmountBasis: ResourceTaxBasis.ExcludingVat),
                new("Второй", 1m, NomenclatureId: c.Nomenclature, Amount: 0.01m, AmountBasis: ResourceTaxBasis.ExcludingVat)],
        });
        var client = await EstimateImportFlow.EditorAsync(host);
        var row = Assert.Single(await RateMatchFlow.RowsAsync(client, revision.Id));
        await DistributionFlow.LinesAsync(client, revision.Id, DistributionFlow.Line(row.GetProperty("id").GetGuid(), "row", (site.Section, 1m)));
        Assert.Equal(new PlannedMaterialCost(c.Nomenclature, 0m, ResourceTaxBasis.ExcludingVat),
            await ResourceCostFlow.CostAsync(host, new(site.Construction, site.Section), c.Nomenclature));
        Assert.Equal(0.02m, (await ResourceCostFlow.CostAsync(host, new(site.Construction), c.Nomenclature))!.Amount);
    }
}
