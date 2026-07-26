using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Reconciliation;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Какие сверки относятся к уровню иерархии (issue #452). Ошибка здесь либо прячет проблему от
/// человека, либо зажигает счётчик там, где проблемы нет, — второе обесценивает бейджи быстрее.
/// </summary>
[Collection("Integration")]
public class ProblemAttributionTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IProblemAttribution Attribution(IServiceScope s) =>
        s.ServiceProvider.GetRequiredService<IProblemAttribution>();

    private static async Task<Guid> SeedSourceAsync(
        IServiceScope scope, string name, CatalogScope fileScope, Guid? scopeId)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var path = await blob.UploadAsync($"{Guid.NewGuid():N}.csv",
            new MemoryStream(Encoding.UTF8.GetBytes("Марка,Кол\nА,1")), "text/csv");

        var file = DataSetFile.Create(name, DataSetFormat.Csv, path, fileScope, scopeId);
        var schema = JsonSerializer.Serialize(new[] { new { name = "Марка", sampleValues = new[] { "" } } });
        var source = file.AddSource(name, "default", schema, 1);
        db.DataSetFiles.Add(file);
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private static async Task<Guid> SeedDefinitionAsync(IServiceScope scope, string name, params Guid[] sourceIds)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var spec = new ReconciliationSpec(
            new ReconciliationSide(sourceIds[0], ["Марка"], "Кол"),
            new ReconciliationSide(sourceIds[^1], ["Марка"], "Кол"),
            new ComparisonRule(ComparisonOperator.Equal));
        var d = ReconciliationDefinition.Create(name, CatalogScope.System, null,
            JsonSerializer.SerializeToDocument(spec, ReconciliationSpecJson.Options));
        db.Add(d);
        await db.SaveChangesAsync();
        return d.Id;
    }

    private static async Task<(Guid constructionId, Guid sectionId, Guid setId)> SeedHierarchyAsync(IMediator m)
    {
        var c = await m.Send(new CreateConstructionCommand("ДНС Сити", Guid.NewGuid()));
        var s = await m.Send(new CreateSectionCommand(c.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(s.Id, "ЭОМ-1"));
        return (c.Id, s.Id, set.Id);
    }

    /// <summary>Проблема комплекта видна и на его разделе, и на стройке: иначе, стоя на стройке,
    /// человек не узнает, что где-то внизу расхождение.</summary>
    [Fact]
    public async Task SetLevelSource_RollsUpToSectionAndConstruction()
    {
        using var scope = fixture.Services.CreateScope();
        var (c, section, set) = await SeedHierarchyAsync(scope.ServiceProvider.GetRequiredService<IMediator>());

        var source = await SeedSourceAsync(scope, "Журнал", CatalogScope.Set, set);
        var definition = await SeedDefinitionAsync(scope, "Кабель", source);

        var a = Attribution(scope);
        Assert.Contains(definition, await a.ReconciliationIdsForAsync(CatalogScope.Set, set));
        Assert.Contains(definition, await a.ReconciliationIdsForAsync(CatalogScope.Section, section));
        Assert.Contains(definition, await a.ReconciliationIdsForAsync(CatalogScope.Construction, c));
    }

    /// <summary>
    /// Объединение осей, а НЕ «самый узкий scope»: сверка над источниками разных уровней касается
    /// обоих, и исчезнув с раздела, она обманула бы человека.
    /// </summary>
    [Fact]
    public async Task SourcesOnDifferentLevels_AttachToBoth()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (_, section, set) = await SeedHierarchyAsync(m);
        var (_, otherSection, _) = await SeedHierarchyAsync(m);

        var onSet = await SeedSourceAsync(scope, "Комплектный", CatalogScope.Set, set);
        var onOtherSection = await SeedSourceAsync(scope, "Разделный", CatalogScope.Section, otherSection);
        var definition = await SeedDefinitionAsync(scope, "Смешанная", onSet, onOtherSection);

        var a = Attribution(scope);
        Assert.Contains(definition, await a.ReconciliationIdsForAsync(CatalogScope.Set, set));
        Assert.Contains(definition, await a.ReconciliationIdsForAsync(CatalogScope.Section, section));
        Assert.Contains(definition, await a.ReconciliationIdsForAsync(CatalogScope.Section, otherSection));
    }

    /// <summary>
    /// System-источник связи НЕ даёт: иначе сверка над общесистемным файлом загорелась бы на КАЖДОМ
    /// комплекте и обесценила бы счётчики за вечер. Такая сверка остаётся «общей», а не пропадает.
    /// </summary>
    [Fact]
    public async Task SystemLevelSource_DoesNotLightUpEverySet()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var (c, section, set) = await SeedHierarchyAsync(m);

        var source = await SeedSourceAsync(scope, "Общий справочник", CatalogScope.System, null);
        var definition = await SeedDefinitionAsync(scope, "Общая", source);

        var a = Attribution(scope);
        Assert.DoesNotContain(definition, await a.ReconciliationIdsForAsync(CatalogScope.Set, set));
        Assert.DoesNotContain(definition, await a.ReconciliationIdsForAsync(CatalogScope.Section, section));
        Assert.DoesNotContain(definition, await a.ReconciliationIdsForAsync(CatalogScope.Construction, c));

        // Тихо исчезнуть со всех экранов она не должна.
        Assert.Contains(definition, await a.GlobalReconciliationsAsync());
    }

    /// <summary>
    /// Вторая ось: документ, привязанный к источнику, реально потребляет эти строки — значит сверка
    /// над источником относится к его комплекту, даже если файл лежит выше.
    /// </summary>
    [Fact]
    public async Task BoundDocument_AttachesReconciliationToItsSet()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (_, _, set) = await SeedHierarchyAsync(m);

        var type = await m.Send(new CreateDocumentTypeCommand(
            "Акт", $"ACT_{Guid.NewGuid():N}"[..12], DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}""")));
        var doc = await m.Send(new AddDocumentToSetCommand(set, type.Id));

        // Файл лежит на уровне System — по одной лишь географии сверка не досталась бы комплекту.
        var source = await SeedSourceAsync(scope, "Общий", CatalogScope.System, null);
        db.DataSetBindings.Add(DataSetBinding.For(doc.Id, source, "Материалы", "{}"));
        await db.SaveChangesAsync();

        var definition = await SeedDefinitionAsync(scope, "По привязке", source);

        Assert.Contains(definition, await Attribution(scope).ReconciliationIdsForAsync(CatalogScope.Set, set));
    }

    [Fact]
    public async Task MultiSourceSpec_CountsAllItsSources()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (_, _, set) = await SeedHierarchyAsync(m);

        var a1 = await SeedSourceAsync(scope, "Шкаф 1", CatalogScope.System, null);
        var a2 = await SeedSourceAsync(scope, "Шкаф 2", CatalogScope.Set, set);
        var right = await SeedSourceAsync(scope, "Сводная", CatalogScope.System, null);

        // Свод (#450): второй источник лежит на комплекте — сверка обязана к нему привязаться.
        var spec = new ReconciliationSpec(
            new ReconciliationSide(a1, ["Марка"], "Кол", null,
                [new SideSource(a1, ["Марка"], "Кол"), new SideSource(a2, ["Марка"], "Кол")]),
            new ReconciliationSide(right, ["Марка"], "Кол"),
            new ComparisonRule(ComparisonOperator.Equal));
        var d = ReconciliationDefinition.Create("Свод", CatalogScope.System, null,
            JsonSerializer.SerializeToDocument(spec, ReconciliationSpecJson.Options));
        db.Add(d);
        await db.SaveChangesAsync();

        Assert.Contains(d.Id, await Attribution(scope).ReconciliationIdsForAsync(CatalogScope.Set, set));
    }

    /// <summary>
    /// Замечания адресованы комплекту, поэтому выше их надо СВОДИТЬ: иначе, стоя на стройке, человек
    /// видел бы ноль при тринадцати неразобранных этажом ниже. Найдено живой проверкой, не рассуждением.
    /// </summary>
    [Fact]
    public async Task Observations_RollUpToSectionAndConstruction()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (c, section, set) = await SeedHierarchyAsync(m);

        db.Add(AgentObservation.Create(CatalogScope.Set, set, "k", "Замечание", null,
            ObservationSeverity.Warning, JsonDocument.Parse("""{"note":"x"}"""), "агент"));
        await db.SaveChangesAsync();

        Assert.Equal(1, (await m.Send(new GetRelatedProblemsQuery(CatalogScope.Set, set))).NeedsAttention);
        Assert.Equal(1, (await m.Send(new GetRelatedProblemsQuery(CatalogScope.Section, section))).NeedsAttention);
        Assert.Equal(1, (await m.Send(new GetRelatedProblemsQuery(CatalogScope.Construction, c))).NeedsAttention);

        // Соседняя стройка чужие замечания не показывает.
        var (other, _, _) = await SeedHierarchyAsync(m);
        Assert.Equal(0, (await m.Send(new GetRelatedProblemsQuery(CatalogScope.Construction, other))).NeedsAttention);
    }

    /// <summary>Счётчик обязан обнуляться действиями человека — иначе он не сигнал, а украшение.</summary>
    [Fact]
    public async Task NeedsAttention_CountsOnlyUnreviewed()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (_, _, set) = await SeedHierarchyAsync(m);

        var observation = AgentObservation.Create(CatalogScope.Set, set, "k", "Замечание", null,
            ObservationSeverity.Warning, JsonDocument.Parse("""{"note":"x"}"""), "агент");
        db.Add(observation);
        await db.SaveChangesAsync();

        var before = await m.Send(new GetRelatedProblemsQuery(CatalogScope.Set, set));
        Assert.Equal(1, before.NeedsAttention);
        // Красный — только за арифметику системы; утверждение агента её не заменяет.
        Assert.False(before.HasArithmeticProblems);

        await m.Send(new ReviewObservationCommand(observation.Id, ObservationStatus.Rejected, "не ошибка", "alex"));

        Assert.Equal(0, (await m.Send(new GetRelatedProblemsQuery(CatalogScope.Set, set))).NeedsAttention);
    }
}
