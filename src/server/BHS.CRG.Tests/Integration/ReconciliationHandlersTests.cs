using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Reconciliation;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Прикладной слой сверки (issue #433). Проверяем то, чего в самой находке нет и быть не должно:
/// наложенное решение человека и вычисленный признак устранения.
/// </summary>
[Collection("Integration")]
public class ReconciliationHandlersTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static JsonSerializerOptions Json => ReconciliationSpecJson.Options;

    private static async Task<Guid> SeedCsvAsync(IServiceScope scope, string name, string csv)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var path = await blob.UploadAsync($"{Guid.NewGuid():N}.csv",
            new MemoryStream(Encoding.UTF8.GetBytes(csv)), "text/csv");

        var file = DataSetFile.Create(name, DataSetFormat.Csv, path, CatalogScope.System, null);
        var schema = JsonSerializer.Serialize(new[]
        {
            new { name = "Марка", sampleValues = new[] { "" } },
            new { name = "Кол", sampleValues = new[] { "" } },
        });
        var source = file.AddSource(name, "default", schema, 1);
        db.DataSetFiles.Add(file);
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private static JsonDocument SpecFor(Guid left, Guid right) =>
        JsonSerializer.SerializeToDocument(new ReconciliationSpec(
            new ReconciliationSide(left, ["Марка"], "Кол"),
            new ReconciliationSide(right, ["Марка"], "Кол"),
            new ComparisonRule(ComparisonOperator.GreaterOrEqual)), Json);

    /// <summary>Слева не хватает — расхождение по ВВГ.</summary>
    private const string ShortCsv = "Марка,Кол\nВВГ,50";
    private const string FixedCsv = "Марка,Кол\nВВГ,100";
    private const string RegistryCsv = "Марка,Кол\nВВГ,100";

    [Fact]
    public async Task Findings_CarryDecision_ByKey()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var left = await SeedCsvAsync(scope, "Журнал", ShortCsv);
        var right = await SeedCsvAsync(scope, "Реестр", RegistryCsv);
        var def = await m.Send(new CreateReconciliationCommand(
            "Кабель", CatalogScope.System, null, SpecFor(left, right)));

        await m.Send(new RunReconciliationCommand(def.Id));
        var before = Assert.Single(await m.Send(new ListFindingsQuery(def.Id)));
        Assert.Equal(FindingStatus.Mismatch, before.Finding.Status);
        Assert.Null(before.Decision);

        await m.Send(new SetDecisionCommand(
            def.Id, before.Finding.Key, DecisionKind.Accepted, "Давальческий", "alex"));

        var after = Assert.Single(await m.Send(new ListFindingsQuery(def.Id)));
        Assert.Equal(DecisionKind.Accepted, after.Decision!.Kind);
        Assert.Equal("Давальческий", after.Decision.Note);
        Assert.Equal("alex", after.Decision.DecidedBy);
    }

    /// <summary>
    /// «Устранено» вычисляется из истории: в прошлый раз было расхождение, теперь совпадение. Хранимое
    /// поле пришлось бы держать в согласии с историей вручную и рано или поздно разошлось бы с ней.
    /// </summary>
    [Fact]
    public async Task Resolved_IsComputedFromHistory_NotStored()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var shortSource = await SeedCsvAsync(scope, "Журнал", ShortCsv);
        var right = await SeedCsvAsync(scope, "Реестр", RegistryCsv);
        var def = await m.Send(new CreateReconciliationCommand(
            "Кабель", CatalogScope.System, null, SpecFor(shortSource, right)));

        await m.Send(new RunReconciliationCommand(def.Id));
        var first = Assert.Single(await m.Send(new ListFindingsQuery(def.Id)));
        Assert.Equal(FindingStatus.Mismatch, first.Finding.Status);
        // Первый прогон: сравнивать не с чем, устранением это быть не может.
        Assert.False(first.Resolved);

        // Данные исправлены — журнал перевыпущен с полным количеством.
        var fixedSource = await SeedCsvAsync(scope, "Журнал (исправлен)", FixedCsv);
        await m.Send(new UpdateReconciliationCommand(def.Id, "Кабель", SpecFor(fixedSource, right)));
        await m.Send(new RunReconciliationCommand(def.Id));

        var second = Assert.Single(await m.Send(new ListFindingsQuery(def.Id)));
        Assert.Equal(FindingStatus.Match, second.Finding.Status);
        Assert.True(second.Resolved);
        Assert.Equal(first.Finding.Key, second.Finding.Key);
    }

    [Fact]
    public async Task Decision_IsSinglePerKey_AndRemovable()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var left = await SeedCsvAsync(scope, "Журнал", ShortCsv);
        var right = await SeedCsvAsync(scope, "Реестр", RegistryCsv);
        var def = await m.Send(new CreateReconciliationCommand(
            "Кабель", CatalogScope.System, null, SpecFor(left, right)));
        await m.Send(new RunReconciliationCommand(def.Id));
        var key = Assert.Single(await m.Send(new ListFindingsQuery(def.Id))).Finding.Key;

        await m.Send(new SetDecisionCommand(def.Id, key, DecisionKind.Accepted, "первая", "alex"));
        // Повторное решение по тому же ключу — правка первого: иначе «какое действует» стало бы
        // неопределённым.
        await m.Send(new SetDecisionCommand(def.Id, key, DecisionKind.Suppressed, "вторая", "alex"));

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<ReconciliationDecision>>();
        var single = Assert.Single(await repo.FindAsync(d => d.DefinitionId == def.Id));
        Assert.Equal(DecisionKind.Suppressed, single.Kind);
        Assert.Equal("вторая", single.Note);

        await m.Send(new RemoveDecisionCommand(def.Id, key));
        Assert.Null(Assert.Single(await m.Send(new ListFindingsQuery(def.Id))).Decision);

        // Снятие несуществующего решения — не ошибка: состояние уже такое, какого просят.
        await m.Send(new RemoveDecisionCommand(def.Id, key));
    }

    [Fact]
    public async Task Runs_AreListedNewestFirst_AndFindingsAddressableByRun()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var left = await SeedCsvAsync(scope, "Журнал", ShortCsv);
        var right = await SeedCsvAsync(scope, "Реестр", RegistryCsv);
        var def = await m.Send(new CreateReconciliationCommand(
            "Кабель", CatalogScope.System, null, SpecFor(left, right)));

        var first = await m.Send(new RunReconciliationCommand(def.Id));
        var second = await m.Send(new RunReconciliationCommand(def.Id));

        var history = await m.Send(new ListReconciliationRunsQuery(def.Id));
        Assert.Equal(2, history.Count);
        Assert.Equal(second.Id, history[0].Id);

        // Можно смотреть не только последний прогон — иначе история была бы бесполезна.
        var old = Assert.Single(await m.Send(new ListFindingsQuery(def.Id, first.Id)));
        Assert.Equal(FindingStatus.Mismatch, old.Finding.Status);
    }

    [Fact]
    public async Task Findings_BeforeAnyRun_AreEmpty_NotError()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var left = await SeedCsvAsync(scope, "Журнал", ShortCsv);
        var right = await SeedCsvAsync(scope, "Реестр", RegistryCsv);
        var def = await m.Send(new CreateReconciliationCommand(
            "Кабель", CatalogScope.System, null, SpecFor(left, right)));

        Assert.Empty(await m.Send(new ListFindingsQuery(def.Id)));
    }
}
