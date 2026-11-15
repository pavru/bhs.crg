using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Контракт «Плановые работы» на живом хосте «только планирование» (change
/// <c>plan-23-planned-works-contract</c>, ТЗ CORE-34, PLAN-9.1, PLAN-23, PLAN-5.1, PLAN-5.2, PLAN-4).
///
/// <para>Контракт берётся тем же путём, что у потребителя, — портом <see cref="IModuleContracts" />, а
/// параметр доступа собирается из прав, как его соберёт ядро для роли. Каждый тест заводит свою стройку:
/// хост на все классы один.</para>
///
/// <para>Ломается так: поставщик читает строки мимо <c>PlanRows</c> (всех редакций) — первый тест видит
/// 100 + 300 + 50 вместо 350; узкое право открывает лишнюю часть — тесты прав видят <c>Shown</c> там, где
/// ждут «скрыто».</para>
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class PlannedWorksContractTests(PlanOnlyHost host)
{
    private static readonly string[] Everything =
        [PlanModule.EstimateRead, PlanModule.EstimateCost, PlanModule.ModuleCode];

    [Fact]
    public async Task План_только_действующих_редакций_а_несопоставленное_названо_числом()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var main = await EstimateSeed.EstimateAsync(host, site.Construction);
        await EstimateSeed.RevisionAsync(host, main, await EstimateSeed.MatchedRowAsync(host, site, 100m));
        var second = await EstimateSeed.RevisionAsync(host, main,
            await EstimateSeed.MatchedRowAsync(host, site, 300m), new EstimateRowDraft("Без вида работы", SourceVolume: 5m));
        await MakeCurrentAsync(main, second.Id);
        var extra = await EstimateSeed.EstimateAsync(host, site.Construction, "Дополнительная", EstimateKind.Additional);
        await EstimateSeed.RevisionAsync(host, extra, await EstimateSeed.MatchedRowAsync(host, site, 50m));

        var answer = await ReadAsync(new PlannedWorksQuery(site.Construction), Everything);

        var volumes = Shown(answer.Volumes);
        var item = Assert.Single(volumes.Items);
        Assert.Equal(350m, item.Volume);
        Assert.Equal((await EstimateSeed.ClassifierAsync(host)).Unit, item.UnitId);
        Assert.Equal(1, answer.Unmapped);
        Assert.Equal([(main, 2), (extra, 1)], volumes.Basis.Select(b => (b.EstimateId, b.Revision)));
        Assert.Equal(second.Id, volumes.Basis[0].RevisionId);
    }

    [Fact]
    public async Task Норма_на_единицу_классификатора_а_не_на_единицу_расценки()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var c = await EstimateSeed.ClassifierAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        await EstimateSeed.RevisionAsync(host, estimate,
            new EstimateRowDraft("Прокладка кабеля", WorkTypeId: c.WorkType, UnitId: c.Unit, Factor: 100m, Volume: 200m,
                SourceVolume: 2m, SourceUnit: "100 м", PlanItemId: await EstimateSeed.PlanItemAsync(host, site),
                Labor: [12m]),
            // Строка той же позиции без трудозатрат в норму не входит: иначе её объём разбавил бы чужие чел.-ч.
            await EstimateSeed.MatchedRowAsync(host, site, 1000m));

        var answer = await ReadAsync(new PlannedWorksQuery(site.Construction), Everything);

        Assert.Equal(1200m, Assert.Single(Shown(answer.Volumes).Items).Volume);
        Assert.Equal(0.06m, Assert.Single(Shown(answer.Labor)).HoursPerUnit);
    }

    [Fact]
    public async Task Норм_нет_значит_null_а_не_ноль()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 100m));

        var answer = await ReadAsync(new PlannedWorksQuery(site.Construction), Everything);

        Assert.Null(Assert.Single(Shown(answer.Labor)).HoursPerUnit);
    }

    [Fact]
    public async Task Части_строки_в_распределении_и_доля_раздела_при_отборе()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var c = await EstimateSeed.ClassifierAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var row = (await EstimateSeed.MatchedRowAsync(host, site, 200m)) with
        {
            Total = 2000m,
            Materials = [new EstimateMaterialDraft("Кабель ВВГнг-LS 3х2,5", 40m, SourceUnit: "м", NomenclatureId: c.Nomenclature, Amount: 400m)],
        };
        var revision = await EstimateSeed.RevisionAsync(host, estimate, row, await EstimateSeed.MatchedRowAsync(host, site, 10m));
        var rows = await DistributionFlow.RowIdsAsync(host, revision.Id);
        // 1,5 · 100 м = 150 м на раздел, остаток 50 м — стройке в целом; вторая строка решения не имеет.
        await DistributionFlow.LinesAsync(await DistributionFlow.EditorAsync(host), revision.Id,
            DistributionFlow.Line(rows[0], "row", (site.Section, 1.5m)));

        var whole = await ReadAsync(new PlannedWorksQuery(site.Construction), Everything);

        var item = Assert.Single(Shown(whole.Volumes).Items);
        Assert.Equal(210m, item.Volume);
        Assert.Equal([new PlannedShare(site.Section, 150m), new PlannedShare(null, 60m)], item.Distribution);
        Assert.Equal(10m, item.Undistributed);

        var section = await ReadAsync(new PlannedWorksQuery(site.Construction, SectionId: site.Section), Everything);

        var onSection = Assert.Single(Shown(section.Volumes).Items);
        Assert.Equal(150m, onSection.Volume);
        Assert.Equal([new PlannedShare(site.Section, 150m)], onSection.Distribution);
        Assert.Equal(0m, onSection.Undistributed);
        Assert.Equal(30m, Assert.Single(Shown(section.Materials).Lines).Quantity);
        var cost = Assert.Single(Shown(section.Cost));
        Assert.Equal(1500m, cost.Total);
        Assert.Equal(300m, Assert.Single(cost.Materials).Amount);
    }

    [Fact]
    public async Task Смета_на_разделе_целиком_в_раздел()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateOnSectionAsync(site);
        await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 80m));

        var answer = await ReadAsync(new PlannedWorksQuery(site.Construction), Everything);

        var item = Assert.Single(Shown(answer.Volumes).Items);
        Assert.Equal([new PlannedShare(site.Section, 80m)], item.Distribution);
        Assert.Equal(0m, item.Undistributed);
    }

    [Fact]
    public async Task Материал_строки_материала_без_позиции_и_несопоставленный_назван_числом()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var c = await EstimateSeed.ClassifierAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var work = (await EstimateSeed.MatchedRowAsync(host, site, 100m)) with
        {
            Materials = [new EstimateMaterialDraft("Скоба неведомая", 7m, SourceUnit: "шт")],
        };
        await EstimateSeed.RevisionAsync(host, estimate, work,
            new EstimateRowDraft("Кабель ВВГнг-LS 3х2,5 по КА", Kind: EstimateRowKind.Material, SourceVolume: 10m, SourceUnit: "м",
                Materials: [new EstimateMaterialDraft("Кабель ВВГнг-LS 3х2,5", 10m, SourceUnit: "м", NomenclatureId: c.Nomenclature)]));

        var answer = await ReadAsync(new PlannedWorksQuery(site.Construction), Everything);

        var materials = Shown(answer.Materials);
        Assert.Equal([new PlannedMaterial(null, c.Nomenclature, 10m, "м")], materials.Lines);
        Assert.Equal(1, materials.Unmatched);
        Assert.Equal(100m, Assert.Single(Shown(answer.Volumes).Items).Volume);
        Assert.Equal(0, answer.Unmapped);
        var hint = Assert.Single(Shown(answer.Hint), h => h.PlanItemId is null);
        Assert.Equal([c.Nomenclature], hint.NomenclatureIds);
        // Стоимости ресурса смета не несёт — «нет в смете», а не ноль.
        Assert.True(Assert.Single(Assert.Single(Shown(answer.Cost), x => x.PlanItemId is null).Materials).NotInEstimate);

        var byItem = await ReadAsync(new PlannedWorksQuery(site.Construction,
            PlanItemIds: [await EstimateSeed.PlanItemAsync(host, site)]), Everything);
        Assert.Empty(Shown(byItem.Materials).Lines);
    }

    [Fact]
    public async Task Монтажник_получает_номенклатуру_без_количеств_остальное_скрыто()
    {
        var site = await SiteWithMaterialAsync();

        var answer = await ReadAsync(new PlannedWorksQuery(site.Construction), [PlanModule.EstimateHint]);

        Assert.Equal([(await EstimateSeed.ClassifierAsync(host)).Nomenclature],
            Assert.Single(Shown(answer.Hint)).NomenclatureIds);
        Assert.Equal(PlanModule.EstimateRead, Hidden(answer.Volumes));
        Assert.Equal(PlanModule.EstimateCost, Hidden(answer.Cost));
        Assert.Equal(PlanModule.EstimateMaterials, Hidden(answer.Materials));
        Assert.Equal(PlanModule.LaborRead, Hidden(answer.Labor));
        NoMoneyAndNoQuantities(answer);
    }

    [Fact]
    public async Task Снабженец_получает_материалы_с_количествами_без_объёмов_и_денег()
    {
        var site = await SiteWithMaterialAsync();

        var answer = await ReadAsync(new PlannedWorksQuery(site.Construction), [PlanModule.EstimateMaterials]);

        Assert.Equal(40m, Assert.Single(Shown(answer.Materials).Lines).Quantity);
        Assert.Equal(PlanModule.EstimateRead, Hidden(answer.Volumes));
        Assert.Equal(PlanModule.EstimateCost, Hidden(answer.Cost));
        Assert.Equal(PlanModule.LaborRead, Hidden(answer.Labor));
        Assert.Equal(PlanModule.EstimateHint, Hidden(answer.Hint));
    }

    [Fact]
    public async Task Менеджер_получает_нормы_и_объёмы_без_денег()
    {
        var site = await SiteWithMaterialAsync();

        var answer = await ReadAsync(new PlannedWorksQuery(site.Construction), [PlanModule.EstimateRead, PlanModule.LaborRead]);

        Assert.Equal(PlanModule.EstimateCost, Hidden(answer.Cost));
        Assert.Single(Shown(answer.Volumes).Items);
        Assert.Single(Shown(answer.Labor));
        NoMoney(answer);

        var laborOnly = await ReadAsync(new PlannedWorksQuery(site.Construction), [PlanModule.LaborRead]);
        Assert.Single(Shown(laborOnly.Labor));
        Assert.Equal(PlanModule.EstimateRead, Hidden(laborOnly.Volumes));
        Assert.Equal(PlanModule.EstimateMaterials, Hidden(laborOnly.Materials));
    }

    [Fact]
    public async Task Без_прав_все_части_скрыты_а_число_несопоставленных_есть()
    {
        var site = await SiteWithMaterialAsync();

        var answer = await ReadAsync(new PlannedWorksQuery(site.Construction), ["core.constructions.read"]);

        Assert.Equal(PlanModule.EstimateRead, Hidden(answer.Volumes));
        Assert.Equal(PlanModule.EstimateCost, Hidden(answer.Cost));
        Assert.Equal(PlanModule.EstimateMaterials, Hidden(answer.Materials));
        Assert.Equal(PlanModule.LaborRead, Hidden(answer.Labor));
        Assert.Equal(PlanModule.EstimateHint, Hidden(answer.Hint));
        Assert.Equal(1, answer.Unmapped);
    }

    [Fact]
    public async Task Системе_отказ_с_причиной()
    {
        var site = await SiteWithMaterialAsync();

        var refused = Assert.IsType<ContractAnswer<PlannedWorksAnswer>.SystemRefused>(
            await ContractAsync(ModuleReader.OfSystem("ночной пересчёт сводки"), new PlannedWorksQuery(site.Construction)));

        Assert.Equal("ночной пересчёт сводки", refused.Reason);
    }

    [Fact]
    public void Контракт_объявлен_с_поставщиком_plan_и_порт_отвечает_реализацией()
    {
        using var scope = host.Services.CreateScope();

        var contract = host.Services.GetRequiredService<ModuleContractCatalog>().Find(typeof(IPlannedWorks));
        Assert.NotNull(contract);
        Assert.Equal(PlanModule.ModuleCode, contract.Declaration.Provider);
        Assert.IsType<ModuleContractAnswer<IPlannedWorks>.Connected>(
            scope.ServiceProvider.GetRequiredService<IModuleContracts>().Get<IPlannedWorks>());
    }

    /// <summary>Стройка: одна сопоставленная строка с трудозатратами и материалом, одна несопоставленная.</summary>
    private async Task<EstimateSeed.Site> SiteWithMaterialAsync()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var c = await EstimateSeed.ClassifierAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var row = (await EstimateSeed.MatchedRowAsync(host, site, 200m)) with
        {
            Labor = [12m],
            Materials = [new EstimateMaterialDraft("Кабель ВВГнг-LS 3х2,5", 40m, SourceUnit: "м", NomenclatureId: c.Nomenclature,
                UnitPrice: 10m, Amount: 400m)],
        };
        await EstimateSeed.RevisionAsync(host, estimate, row, new EstimateRowDraft("Без вида работы", SourceVolume: 1m));
        return site;
    }

    private async Task<Guid> EstimateOnSectionAsync(EstimateSeed.Site site)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var estimate = Estimate.Create(site.Construction, site.Section, "На раздел", EstimateKind.Main, DateTimeOffset.UtcNow);
        db.Estimates.Add(estimate);
        await db.SaveChangesAsync();
        return estimate.Id;
    }

    private async Task MakeCurrentAsync(Guid estimateId, Guid revisionId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var estimate = await db.Estimates.SingleAsync(e => e.Id == estimateId);
        estimate.MakeCurrent(await db.EstimateRevisions.SingleAsync(r => r.Id == revisionId));
        await db.SaveChangesAsync();
    }

    private async Task<PlannedWorksAnswer> ReadAsync(PlannedWorksQuery query, string[] keys) =>
        Shown(await ContractAsync(ModuleReader.Of(Guid.NewGuid(), "Пробный Читатель", keys), query));

    private async Task<ContractAnswer<PlannedWorksAnswer>> ContractAsync(ModuleReader reader, PlannedWorksQuery query)
    {
        using var scope = host.Services.CreateScope();
        var contract = Assert.IsType<ModuleContractAnswer<IPlannedWorks>.Connected>(
            scope.ServiceProvider.GetRequiredService<IModuleContracts>().Get<IPlannedWorks>()).Contract;
        return await contract.ReadAsync(reader, query);
    }

    private static T Shown<T>(ContractAnswer<T> answer) => Assert.IsType<ContractAnswer<T>.Shown>(answer).Value;

    private static string Hidden<T>(ContractAnswer<T> answer) => Assert.IsType<ContractAnswer<T>.Hidden>(answer).Permission;

    /// <summary>Ни в одной пришедшей части нет денежного поля — сверка по ключам JSON.</summary>
    private static void NoMoney(PlannedWorksAnswer answer) => AbsentKeys(answer, "total", "amount", "price", "cost", "notInEstimate");

    private static void NoMoneyAndNoQuantities(PlannedWorksAnswer answer) =>
        AbsentKeys(answer, "total", "amount", "price", "cost", "notInEstimate", "quantity", "volume", "hoursPerUnit", "unit");

    private static void AbsentKeys(PlannedWorksAnswer answer, params string[] keys)
    {
        object?[] shown =
        [
            ValueOf(answer.Volumes), ValueOf(answer.Cost), ValueOf(answer.Materials), ValueOf(answer.Labor), ValueOf(answer.Hint),
        ];
        foreach (var value in shown.OfType<object>())
        {
            var json = JsonSerializer.Serialize(value, value.GetType(), EstimateSeed.Json);
            foreach (var key in keys)
                Assert.DoesNotContain($"\"{key}\"", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static object? ValueOf<T>(ContractAnswer<T> answer) => answer is ContractAnswer<T>.Shown s ? s.Value : null;
}
