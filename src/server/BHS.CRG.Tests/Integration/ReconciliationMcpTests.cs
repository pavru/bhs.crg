using System.Text;
using System.Text.Json;
using BHS.CRG.Api.Mcp;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Reconciliation;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сверка глазами внешнего агента (issue #448). Он читает находки и предлагает пары; подтверждает
/// ТОЛЬКО человек — иначе неподтверждённый алиас попал бы в путь сравнения (риск P1 в #414).
/// </summary>
[Collection("Integration")]
public class ReconciliationMcpTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static ReconciliationTools Tools(IServiceScope s) => new(
        s.ServiceProvider.GetRequiredService<IMediator>(),
        s.ServiceProvider.GetRequiredService<IHttpContextAccessor>());

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

    /// <summary>Позиции названы по-разному — обе остаются без пары, это и есть кандидаты.</summary>
    private static async Task<Guid> SeedUnmatchedAsync(IServiceScope scope)
    {
        var left = await SeedCsvAsync(scope, "Журнал", "Марка,Кол\nОрганайзер СвязьСтройДеталь,10");
        var right = await SeedCsvAsync(scope, "Реестр", "Марка,Кол\nHyperline CM-1U-ML,10");

        var spec = new ReconciliationSpec(
            new ReconciliationSide(left, ["Марка"], "Кол"),
            new ReconciliationSide(right, ["Марка"], "Кол"),
            new ComparisonRule(ComparisonOperator.Equal));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var definition = ReconciliationDefinition.Create("Органайзеры", CatalogScope.System, null,
            JsonSerializer.SerializeToDocument(spec, ReconciliationSpecJson.Options));
        db.Add(definition);
        await db.SaveChangesAsync();

        await scope.ServiceProvider.GetRequiredService<IReconciliationRunner>().RunAsync(definition.Id);
        return definition.Id;
    }

    [Fact]
    public async Task Agent_SeesUnmatchedPositions_WithKeysToProposeWith()
    {
        using var scope = fixture.Services.CreateScope();
        var definitionId = await SeedUnmatchedAsync(scope);
        var tools = Tools(scope);

        Assert.Single((await tools.ListReconciliationsAsync(CancellationToken.None)).Items,
            r => r.Id == definitionId);

        var findings = await tools.GetFindingsAsync(definitionId, CancellationToken.None);
        var unmatched = findings.Items.Where(f => f.Status.StartsWith("Missing")).ToList();
        Assert.Equal(2, unmatched.Count);
        // Ключ обязан приходить: написанный от руки не совпал бы ни с чем.
        Assert.All(unmatched, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
        Assert.All(unmatched, f => Assert.False(string.IsNullOrWhiteSpace(f.Label)));
    }

    /// <summary>
    /// Предложение агента — только предложение: на прогон оно не влияет, пока человек не подтвердит.
    /// </summary>
    [Fact]
    public async Task ProposedAlias_IsNotApplied_UntilHumanConfirms()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var definitionId = await SeedUnmatchedAsync(scope);
        var tools = Tools(scope);
        var runner = scope.ServiceProvider.GetRequiredService<IReconciliationRunner>();

        var unmatched = (await tools.GetFindingsAsync(definitionId, CancellationToken.None))
            .Items.Where(f => f.Status.StartsWith("Missing")).ToList();

        var proposed = await tools.ProposeAliasAsync(
            unmatched[0].Key, unmatched[0].Label, unmatched[1].Key, unmatched[1].Label,
            CancellationToken.None, note: "Одна и та же деталь у разных поставщиков");

        Assert.Equal("Proposed", proposed.Status);

        // Прогон не изменился — модель в путь сравнения не попала.
        await runner.RunAsync(definitionId);
        Assert.Equal(2, (await tools.GetFindingsAsync(definitionId, CancellationToken.None))
            .Items.Count(f => f.Status.StartsWith("Missing")));

        // Человек подтвердил — позиции слились.
        var alias = await db.Set<ReconciliationAlias>().SingleAsync();
        alias.Review(AliasStatus.Confirmed, null, "alex");
        db.Update(alias);
        await db.SaveChangesAsync();

        await runner.RunAsync(definitionId);
        var merged = Assert.Single((await tools.GetFindingsAsync(definitionId, CancellationToken.None)).Items);
        Assert.Equal("Match", merged.Status);
        Assert.Equal(10, merged.LeftValue);
    }

    [Fact]
    public async Task Agent_SeesRejectedProposals_SoItDoesNotRepeatThem()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var definitionId = await SeedUnmatchedAsync(scope);
        var tools = Tools(scope);

        var unmatched = (await tools.GetFindingsAsync(definitionId, CancellationToken.None))
            .Items.Where(f => f.Status.StartsWith("Missing")).ToList();
        await tools.ProposeAliasAsync(unmatched[0].Key, "А", unmatched[1].Key, "Б", CancellationToken.None);

        var alias = await db.Set<ReconciliationAlias>().SingleAsync();
        alias.Review(AliasStatus.Rejected, "Разные детали", "alex");
        db.Update(alias);
        await db.SaveChangesAsync();

        // Отказ человека — ответ, а не повод повторить: агент обязан его видеть.
        var listed = Assert.Single((await tools.ListAliasesAsync(CancellationToken.None)).Items);
        Assert.Equal("Rejected", listed.Status);
        Assert.Equal("Разные детали", listed.Note);
    }

    [Fact]
    public async Task SelfAlias_AndEmptyKeys_AreRejected()
    {
        using var scope = fixture.Services.CreateScope();
        var tools = Tools(scope);

        await Assert.ThrowsAsync<McpException>(() =>
            tools.ProposeAliasAsync("к", "К", "к", "К", CancellationToken.None));
        await Assert.ThrowsAsync<McpException>(() =>
            tools.ProposeAliasAsync(" ", "А", "б", "Б", CancellationToken.None));
    }
}
