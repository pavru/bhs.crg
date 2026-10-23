using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Ссылки строк новой редакции (change <c>plan-06-estimate-model</c>, ТЗ CORE-34.4, PLAN-4, PLAN-9.1):
/// позиция перечня — этой стройки, того же вида работы и единицы; номенклатура — запись справочника
/// номенклатуры. Иначе объём ушёл бы в чужой план, а «материал» оказался бы записью другого вида.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateRowReferencesTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Позиция_перечня_другой_стройки_отвергается_и_редакции_нет()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var other = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var foreign = await EstimateSeed.MatchedRowAsync(host, other, 10m);

        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() => EstimateSeed.RevisionAsync(host, estimate, foreign));

        Assert.Contains("Строка 1, позиция перечня работ: позиция другой стройки", refusal.Message);
        Assert.False(await AnyRevisionAsync(estimate));
    }

    [Fact]
    public async Task Позиция_перечня_другого_вида_работы_отвергается()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var site = await EstimateSeed.SiteAsync(host);
        var otherWork = await new WorkTypeSeed(host).RecordAsync(c.WorkTypeClass, $"Другой вид {Guid.NewGuid():N}");
        var item = await EstimateSeed.PlanItemAsync(host, site, otherWork.Id);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var row = await EstimateSeed.MatchedRowAsync(host, site, 10m) with { PlanItemId = item };

        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() => EstimateSeed.RevisionAsync(host, estimate, row));

        Assert.Contains("у позиции другой вид работы", refusal.Message);
    }

    [Fact]
    public async Task Номенклатура_материала_только_запись_справочника_номенклатуры()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);

        // Единица существует, но это не номенклатура — ссылка материала на неё не записывается.
        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() => EstimateSeed.RevisionAsync(host, estimate,
            new EstimateRowDraft("Строка", Materials: [new EstimateMaterialDraft("Кабель", 1m, NomenclatureId: c.Unit)])));
        Assert.Contains("Строка 1, позиция номенклатуры материала: записи нет", refusal.Message);

        var revision = await EstimateSeed.RevisionAsync(host, estimate,
            new EstimateRowDraft("Строка", Materials: [new EstimateMaterialDraft("Кабель", 1m, NomenclatureId: c.Nomenclature)]));
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var row = await db.EstimateRows.Where(r => r.RevisionId == revision.Id).Select(r => r.Id).SingleAsync();
        Assert.Equal(c.Nomenclature, (await db.EstimateRowMaterials.SingleAsync(m => m.RowId == row)).NomenclatureId);
    }

    /// <summary>Архивная позиция номенклатуры новым материалом не ставится (ТЗ CORE-34.4).</summary>
    [Fact]
    public async Task Архивная_номенклатура_материала_отвергается()
    {
        var seed = new WorkTypeSeed(host);
        await EstimateSeed.ClassifierAsync(host);
        Guid typeId;
        using (var scope = host.Services.CreateScope())
            typeId = (await scope.ServiceProvider.GetRequiredService<BHS.CRG.Application.Common.IRepository<BHS.CRG.Domain.Documents.DocumentType>>()
                .FindAsync(t => t.Code == TestDirectories.NomenclatureCode)).Single().Id;
        var archived = await seed.RecordAsync(typeId, $"Снятая позиция {Guid.NewGuid():N}");
        await seed.ArchiveAsync(archived.Id);
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);

        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() => EstimateSeed.RevisionAsync(host, estimate,
            new EstimateRowDraft("Строка", Materials: [new EstimateMaterialDraft("Кабель", 1m, NomenclatureId: archived.Id)])));

        Assert.Contains("Строка 1, позиция номенклатуры материала: запись в архиве", refusal.Message);
        Assert.False(await AnyRevisionAsync(estimate));
    }

    private async Task<bool> AnyRevisionAsync(Guid estimate)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateRevisions.AnyAsync(r => r.EstimateId == estimate);
    }
}
