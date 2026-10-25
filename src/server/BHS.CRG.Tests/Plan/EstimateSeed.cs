using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Integration;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Посев для тестов сметы на хосте «только планирование» (change <c>plan-06-estimate-model</c>).
///
/// <para>⚠️ Классификатор заводится ОДИН РАЗ за прогон: типы единственны по коду, а хост на все классы
/// один (<see cref="PlanOnlyCollection" />). Базу классы не сбрасывают — каждый заводит свою стройку и
/// читает по её идентификатору, как классы хоста счетов. Хост чистит базу сам раз за прогон
/// (<c>IntegrationTestFixture.InitializeAsync</c>) — и сносит при этом типы, спроецированные при старте;
/// поэтому проекция типов модулей повторяется здесь.</para>
/// </summary>
internal static class EstimateSeed
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Classifier? classifier;

    /// <summary>
    /// Вид работы и единица классификатора — ссылки сопоставленной строки; тип вида работы — для новых
    /// записей; позиция номенклатуры — ссылка материала.
    /// </summary>
    /// <param name="Piece">Вторая единица — «шт», не единица вида работы (<c>plan-08</c>).</param>
    internal sealed record Classifier(Guid WorkType, Guid Unit, Guid WorkTypeClass, Guid Nomenclature, Guid Piece);

    /// <summary>Стройка с разделом — у каждого теста своя.</summary>
    internal sealed record Site(Guid Construction, Guid Section);

    public static async Task<Classifier> ClassifierAsync(PlanOnlyHost host)
    {
        await Gate.WaitAsync();
        try
        {
            return classifier ??= await SeedClassifierAsync(host);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<Classifier> SeedClassifierAsync(PlanOnlyHost host)
    {
        _ = host.CreateClient();
        var seed = new WorkTypeSeed(host);

        using (var scope = host.Services.CreateScope())
        {
            var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            var unitType = (await types.FindAsync(t => t.Code == CoreRecordTypes.UnitCode)).FirstOrDefault()
                ?? await seed.TypeAsync(CoreRecordTypes.UnitCode, "Единица измерения");
            await scope.ServiceProvider.ProjectModuleTypesAsync();
            var workType = (await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkType)))!;

            // Номенклатура — справочник по роли, как на базе заказчика (issue #1101).
            var nomenclatureType = (await types.FindAsync(t => t.Code == TestDirectories.NomenclatureCode)).FirstOrDefault()
                ?? await seed.TypeAsync(TestDirectories.NomenclatureCode, "Номенклатура");
            await TestDirectories.AssignRolesAsync(scope.ServiceProvider);

            var unit = await seed.RecordAsync(unitType.Id, "м");
            var work = await seed.RecordAsync(workType.Id, "Прокладка кабеля",
                WorkTypeSeed.WorkTypeData("01.02", "Прокладка кабеля", unit));
            var cable = await seed.RecordAsync(nomenclatureType.Id, "Кабель ВВГнг-LS 3х2,5");
            var piece = await seed.RecordAsync(unitType.Id, "шт");
            return new Classifier(work.Id, unit.Id, workType.Id, cable.Id, piece.Id);
        }
    }

    public static async Task<Site> SiteAsync(PlanOnlyHost host, string name = "Стройка")
    {
        await ClassifierAsync(host);
        var seed = new WorkTypeSeed(host);
        var construction = await seed.SendAsync(new CreateConstructionCommand($"{name} {Guid.NewGuid():N}", Guid.NewGuid()));
        var section = await seed.SendAsync(new CreateSectionCommand(construction.Id, "ЭОМ-1"));
        return new Site(construction.Id, section.Id);
    }

    public static async Task<Guid> EstimateAsync(
        PlanOnlyHost host, Guid constructionId, string name = "Основная", EstimateKind kind = EstimateKind.Main)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var estimate = Estimate.Create(constructionId, null, name, kind, DateTimeOffset.UtcNow);
        db.Estimates.Add(estimate);
        await db.SaveChangesAsync();
        return estimate.Id;
    }

    /// <summary>Редакция — единственным местом записи строк, как её запишет импорт.</summary>
    public static async Task<EstimateRevision> RevisionAsync(PlanOnlyHost host, Guid estimateId, params EstimateRowDraft[] rows)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EstimateRevisionWriter>()
            .AddAsync(estimateId, new EstimateRevisionSource(FileName: "смета.gsfx", SchemaVersion: "2026.1.1"), rows);
    }

    /// <summary>Позиция перечня стройки — портом ядра, как её заведёт сопоставление.</summary>
    public static async Task<Guid> PlanItemAsync(PlanOnlyHost host, Site site, Guid? workType = null)
    {
        var c = await ClassifierAsync(host);
        using var scope = host.Services.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IModulePlanItems>()
            .FindOrCreateAsync(new PlanItemKey(workType ?? c.WorkType, site.Construction, site.Section, c.Unit));
        return outcome.Item?.Id ?? throw new InvalidOperationException(outcome.Refusal?.Reason);
    }

    /// <summary>Сопоставленная строка: вид работы, единица, объём и позиция перечня.</summary>
    public static async Task<EstimateRowDraft> MatchedRowAsync(PlanOnlyHost host, Site site, decimal volume, string name = "Кабель ВВГнг 3х2,5")
    {
        var c = await ClassifierAsync(host);
        return new EstimateRowDraft(name, WorkTypeId: c.WorkType, UnitId: c.Unit, Factor: 100m, Volume: volume,
            SourceVolume: volume / 100m, SourceUnit: "100 м", PlanItemId: await PlanItemAsync(host, site),
            RateCode: "ГЭСНм08-02-399-01", UnitCost: 1000m, Total: volume * 10m);
    }

    /// <summary>Действующая редакция сметы, как она лежит.</summary>
    public static async Task<Guid?> CurrentRevisionAsync(PlanOnlyHost host, Guid estimateId)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlanDbContext>().Estimates
            .Where(e => e.Id == estimateId).Select(e => e.CurrentRevisionId).SingleAsync();
    }

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}
