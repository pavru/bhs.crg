using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Reconciliation;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Ядро сверки (issue #431, фаза Ф1 из #414). Вертикальный срез — «кабель: проложено ↔ реестр
/// материалов»: суммирование по марке, ключ из марки с сечением, оператор ≥, персистентное решение.
/// ИИ здесь нет и не должно быть: арифметику и сопоставление считает код.
/// </summary>
[Collection("Integration")]
public class ReconciliationRunnerTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static JsonSerializerOptions Json => ReconciliationSpecJson.Options;

    /// <summary>CSV-источник. Разделитель — запятая: парсер определяет таб/запятую.</summary>
    private static async Task<Guid> SeedCsvAsync(IServiceScope scope, string name, string csv, string[] columns)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

        var path = await blob.UploadAsync($"{Guid.NewGuid():N}.csv",
            new MemoryStream(Encoding.UTF8.GetBytes(csv)), "text/csv");

        var file = DataSetFile.Create(name, DataSetFormat.Csv, path, CatalogScope.System, null);
        var schema = JsonSerializer.Serialize(columns.Select(c => new { name = c, sampleValues = new[] { "" } }));
        var source = file.AddSource(name, "default", schema, csv.Split('\n').Length - 1);
        db.DataSetFiles.Add(file);
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    /// <summary>Журнал: одна марка идёт несколькими линиями — итог по марке и есть предмет сравнения.</summary>
    private const string JournalCsv =
        // Запятая внутри сечения обязана быть в кавычках: разделитель CSV — та же запятая.
        "Марка,Сечение,Проложено\n" +
        "ВВГнг(А)-LS,\"3х2,5\",120\n" +
        "ВВГнг(А)-LS,\"3х2,5\",80\n" +
        "ВВГнг(А)-LS,5х6,50\n" +
        "КВВГ,\"4х1,5\",30";

    private const string RegistryCsv =
        "Марка,Сечение,Количество\n" +
        "ВВГнг(А)–LS,\"3Х2.5\",200\n" + // другая запись той же марки — ключ обязан сойтись
        "ВВГнг(А)-LS,5х6,70\n" +        // проложено меньше заявленного — расхождение
        "ПВС,\"2х1,5\",15";             // в журнале нет вовсе

    private static async Task<(Guid definitionId, IReconciliationRunner runner)> SeedAsync(IServiceScope scope)
    {
        var journal = await SeedCsvAsync(scope, "Кабельный журнал", JournalCsv, ["Марка", "Сечение", "Проложено"]);
        var registry = await SeedCsvAsync(scope, "Реестр материалов", RegistryCsv, ["Марка", "Сечение", "Количество"]);

        var spec = new ReconciliationSpec(
            new ReconciliationSide(journal, ["Марка", "Сечение"], "Проложено"),
            new ReconciliationSide(registry, ["Марка", "Сечение"], "Количество"),
            new ComparisonRule(ComparisonOperator.GreaterOrEqual));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var definition = ReconciliationDefinition.Create(
            "Кабель: проложено ↔ реестр", CatalogScope.System, null,
            JsonSerializer.SerializeToDocument(spec, Json));
        db.Add(definition);
        await db.SaveChangesAsync();

        return (definition.Id, scope.ServiceProvider.GetRequiredService<IReconciliationRunner>());
    }

    private static async Task<List<ReconciliationFinding>> FindingsAsync(IServiceScope scope, Guid runId)
        => await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .ReconciliationFindings.AsNoTracking().Where(f => f.RunId == runId).ToListAsync();

    [Fact]
    public async Task Run_SumsByKey_AndAppliesOperator()
    {
        using var scope = fixture.Services.CreateScope();
        var (definitionId, runner) = await SeedAsync(scope);

        var run = await runner.RunAsync(definitionId);
        Assert.Equal(ReconciliationRunStatus.Completed, run.Status);

        var findings = await FindingsAsync(scope, run.Id);

        // 120 + 80 = 200 против 200 при операторе ≥ — совпадение. Заодно доказано, что «ВВГнг(А)–LS
        // 3Х2.5» и «ВВГнг(А)-LS 3х2,5» сошлись в один ключ.
        var cable = Assert.Single(findings, f => f.Label.Contains("ВВГнг", StringComparison.OrdinalIgnoreCase)
                                                 && f.LeftValue == 200);
        Assert.Equal(FindingStatus.Match, cable.Status);
        Assert.Equal(200, cable.RightValue);

        // 50 проложено против 70 заявленных — расхождение.
        var short6 = Assert.Single(findings, f => f.LeftValue == 50);
        Assert.Equal(FindingStatus.Mismatch, short6.Status);

        // Позиция есть только в реестре и только в журнале — обе обязаны стать находками, а не пропасть.
        Assert.Single(findings, f => f.Status == FindingStatus.MissingLeft);
        Assert.Single(findings, f => f.Status == FindingStatus.MissingRight);

        Assert.Equal(1, run.MismatchCount);
        Assert.Equal(1, run.MissingLeftCount);
        Assert.Equal(1, run.MissingRightCount);
    }

    [Fact]
    public async Task Finding_CarriesProvenance_ToSourceAndRows()
    {
        using var scope = fixture.Services.CreateScope();
        var (definitionId, runner) = await SeedAsync(scope);
        var run = await runner.RunAsync(definitionId);

        var cable = Assert.Single(await FindingsAsync(scope, run.Id), f => f.LeftValue == 200);
        var left = cable.Provenance.RootElement.GetProperty("left");

        // Две строки журнала сложились в одну находку — провенанс обязан назвать обе, иначе
        // расхождение не проверить глазами.
        Assert.Equal(2, left.GetProperty("rows").GetArrayLength());
        Assert.Equal("Проложено", left.GetProperty("column").GetString());
        Assert.True(left.TryGetProperty("sourceId", out _));
    }

    /// <summary>
    /// Центральное решение #414: решение живёт отдельно от прогона. Привяжи его к прогону — и следующий
    /// прогон потеряет память о том, что человек уже разобрал, вместе со всем смыслом журнала.
    /// </summary>
    [Fact]
    public async Task Decision_SurvivesNewRun()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (definitionId, runner) = await SeedAsync(scope);

        var first = await runner.RunAsync(definitionId);
        var mismatch = Assert.Single(await FindingsAsync(scope, first.Id), f => f.Status == FindingStatus.Mismatch);

        db.Add(ReconciliationDecision.Create(
            definitionId, mismatch.Key, DecisionKind.Accepted, "Давальческий кабель", "alex"));
        await db.SaveChangesAsync();

        var second = await runner.RunAsync(definitionId);
        var again = Assert.Single(await FindingsAsync(scope, second.Id), f => f.Status == FindingStatus.Mismatch);

        Assert.Equal(mismatch.Key, again.Key);
        var decision = await db.ReconciliationDecisions.AsNoTracking()
            .SingleAsync(d => d.DefinitionId == definitionId && d.Key == again.Key);
        Assert.Equal(DecisionKind.Accepted, decision.Kind);
        Assert.Equal("Давальческий кабель", decision.Note);
    }

    /// <summary>
    /// Прямая проверка P2 — высшего продуктового риска. Перенумерация 1..N в этих документах происходит
    /// регулярно; ключ на порядковом номере обнулял бы всю накопленную память при первой же вставке.
    /// </summary>
    [Fact]
    public async Task Renumbering_And_Reordering_DoNotChangeKeys()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (definitionId, runner) = await SeedAsync(scope);

        var before = (await FindingsAsync(scope, (await runner.RunAsync(definitionId)).Id))
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        // Тот же журнал: строки переставлены, и добавлена ведущая колонка с номерами — ровно то, что
        // происходит при перевыпуске документа.
        var renumbered =
            "№,Марка,Сечение,Проложено\n" +
            "1,КВВГ,\"4х1,5\",30\n" +
            "2,ВВГнг(А)-LS,5х6,50\n" +
            "3,ВВГнг(А)-LS,\"3х2,5\",80\n" +
            "4,ВВГнг(А)-LS,\"3х2,5\",120";
        var newJournal = await SeedCsvAsync(scope, "Кабельный журнал (перевыпуск)", renumbered,
            ["№", "Марка", "Сечение", "Проложено"]);

        var definition = await db.Set<ReconciliationDefinition>().SingleAsync(d => d.Id == definitionId);
        var spec = definition.Spec.Deserialize<ReconciliationSpec>(Json)!;
        definition.Update(definition.Name, JsonSerializer.SerializeToDocument(
            spec with { Left = spec.Left with { SourceId = newJournal } }, Json));
        await db.SaveChangesAsync();

        var after = (await FindingsAsync(scope, (await runner.RunAsync(definitionId)).Id))
            .Select(f => f.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(before, after);
    }

    /// <summary>Неудача обязана быть видимой: пустой журнал молча читался бы как «расхождений нет».</summary>
    /// <summary>
    /// Алиас сводит две по-разному названные позиции в одну и СКЛАДЫВАЕТ количества. Применяется на
    /// свёртке: сопоставление ключей после сравнения ничего бы не дало.
    /// </summary>
    [Fact]
    public async Task ConfirmedAlias_MergesPositions_AndSumsQuantities()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Слева одна и та же позиция записана двумя именами, справа — одним.
        var left = await SeedCsvAsync(scope, "Журнал",
            """
            Марка,Сечение,Кол
            Органайзер СвязьСтройДеталь,1U,4
            Hyperline CM-1U-ML,1U,6
            """,
            ["Марка", "Сечение", "Кол"]);
        var right = await SeedCsvAsync(scope, "Реестр",
            """
            Марка,Сечение,Кол
            Hyperline CM-1U-ML,1U,10
            """,
            ["Марка", "Сечение", "Кол"]);

        var spec = new ReconciliationSpec(
            new ReconciliationSide(left, ["Марка", "Сечение"], "Кол"),
            new ReconciliationSide(right, ["Марка", "Сечение"], "Кол"),
            new ComparisonRule(ComparisonOperator.Equal));
        var definition = ReconciliationDefinition.Create("Органайзеры", CatalogScope.System, null,
            JsonSerializer.SerializeToDocument(spec, Json));
        db.Add(definition);
        await db.SaveChangesAsync();

        var runner = scope.ServiceProvider.GetRequiredService<IReconciliationRunner>();

        // Без алиаса — две находки-сироты.
        var before = await FindingsAsync(scope, (await runner.RunAsync(definition.Id)).Id);
        Assert.Equal(2, before.Count);
        Assert.Contains(before, f => f.Status == FindingStatus.MissingRight);

        var variant = before.Single(f => f.Status == FindingStatus.MissingRight).Key;
        var canonical = before.Single(f => f.Status != FindingStatus.MissingRight).Key;

        // Предложенный, но НЕ подтверждённый алиас на сравнение влиять не должен: это была бы модель
        // внутри арифметики (риск P1 в #414).
        var alias = ReconciliationAlias.Propose(variant, "Органайзер", canonical, "Hyperline", null, "агент");
        db.Add(alias);
        await db.SaveChangesAsync();
        Assert.Equal(2, (await FindingsAsync(scope, (await runner.RunAsync(definition.Id)).Id)).Count);

        alias.Review(AliasStatus.Confirmed, "Одно и то же", "alex");
        db.Update(alias);
        await db.SaveChangesAsync();

        var after = await FindingsAsync(scope, (await runner.RunAsync(definition.Id)).Id);
        var merged = Assert.Single(after);
        Assert.Equal(FindingStatus.Match, merged.Status);
        Assert.Equal(10, merged.LeftValue);   // 4 + 6 сложились в одну позицию
        Assert.Equal(10, merged.RightValue);
    }

    [Fact]
    public async Task BrokenSpec_FailsRunVisibly_InsteadOfEmptyResult()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var spec = new ReconciliationSpec(
            new ReconciliationSide(Guid.NewGuid(), ["Марка"], "Проложено"),
            new ReconciliationSide(Guid.NewGuid(), ["Марка"], "Количество"),
            new ComparisonRule(ComparisonOperator.Equal));
        var definition = ReconciliationDefinition.Create("Битая", CatalogScope.System, null,
            JsonSerializer.SerializeToDocument(spec, Json));
        db.Add(definition);
        await db.SaveChangesAsync();

        var run = await scope.ServiceProvider.GetRequiredService<IReconciliationRunner>()
            .RunAsync(definition.Id);

        Assert.Equal(ReconciliationRunStatus.Failed, run.Status);
        Assert.Contains("не найден", run.Error);
        Assert.Empty(await FindingsAsync(scope, run.Id));
    }
}
