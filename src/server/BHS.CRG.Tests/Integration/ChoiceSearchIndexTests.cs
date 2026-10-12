using System.Data.Common;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Поиск «на выбор» идёт по триграммному индексу и отвечает тем же, что проход (issue #1247).
///
/// <para>⚠️ Обычный тест поиска до индекса не доходит: на десятках строк планировщик берёт проход, и
/// тест зелёный при любом индексе — и без него, и с негодным. Поэтому путь здесь принуждается, а план
/// читается.</para>
///
/// <para>Что ловится, кроме «индекс забыли»: локаль базы, при которой триграммы кириллицы не строятся
/// или регистр сворачивается не так, как у <c>ILIKE</c>. Тогда индекс не замедляется, а ТЕРЯЕТ строки —
/// «позиция не нашлась», и человек заводит дубль.</para>
/// </summary>
[Collection("Integration")]
public class ChoiceSearchIndexTests(IntegrationTestFixture fixture)
{
    private const string Index = "IX_domain_objects_SearchText";

    [Fact]
    public async Task Поиск_идёт_по_колонке_под_индексом_и_находит_по_названию_и_по_имени()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (type, byName, byAlias) = await SeedAsync(tag);

        var sql = new List<string>();
        await using var db = Context(sql);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off");

        var found = await new DomainObjectRepository(db).SearchForChoiceAsync([type], $"кабель {tag}", 10);

        Assert.Equal([byName, byAlias], found.Items.OrderBy(i => i.DisplayName).Select(i => i.Id));
        Assert.Equal($"Кабель {tag} ВВГ", found.Items.Single(i => i.Id == byAlias).MatchedAlias);
        // Запрос обязан называть колонку, под которой индекс: отбор по названию и именам порознь
        // даёт тот же ответ — и полный проход на каждое нажатие.
        Assert.Contains(sql, s => s.Contains("\"SearchText\" ILIKE"));
        Assert.DoesNotContain(sql, s => s.Contains("unnest"));
    }

    [Fact]
    public async Task Индекс_используется_и_отвечает_тем_же_что_проход()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (_, byName, byAlias) = await SeedAsync(tag);
        // Регистр набранного — третий, не совпадающий ни с названием, ни с именем. Без ORDER BY:
        // с ним планировщик идёт по первичному ключу, и индекс поиска остаётся в стороне.
        var query = $"""SELECT "Id" FROM domain_objects WHERE "SearchText" ILIKE '%кАбЕлЬ {tag}%'""";

        await using var db = Context([]);
        await using var tx = await db.Database.BeginTransactionAsync();

        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off");
        Assert.Contains(Index, await PlanAsync(db, query));
        var viaIndex = await IdsAsync(db, query);

        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = on; SET LOCAL enable_bitmapscan = off");
        Assert.DoesNotContain(Index, await PlanAsync(db, query));
        var viaScan = await IdsAsync(db, query);

        Assert.Equal(new[] { byName, byAlias }.Order(), viaScan);
        Assert.Equal(viaScan, viaIndex);
    }

    /// <summary>
    /// Две записи одного типа: у первой слово в НАЗВАНИИ (заглавными), у второй — только в
    /// альтернативном имени. Имя вписано сырым запросом нарочно: текст поиска обязан следовать и за
    /// правкой мимо приложения — он генерируется базой.
    /// </summary>
    private async Task<(Guid Type, Guid ByName, Guid ByAlias)> SeedAsync(string tag)
    {
        using var scope = fixture.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var type = (await mediator.Send(new CreateDocumentTypeCommand(
            $"TRGM_{tag}", $"TRGM_{tag}", DocumentTypeKind.Composite, null, JsonDocument.Parse("""{"fields":[]}""")))).Id;

        async Task<Guid> EntryAsync(string name) => (await mediator.Send(new CreateCommonDataEntryCommand(
            name, type, JsonDocument.Parse("{}"), CatalogScope.System, null))).Id;

        var byName = await EntryAsync($"КАБЕЛЬ {tag} медный");
        var byAlias = await EntryAsync($"Провод {tag}");
        var alias = $"Кабель {tag} ВВГ";
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync(
            $"""UPDATE domain_objects SET "Aliases" = ARRAY[{alias}] WHERE "Id" = {byAlias}""");
        return (type, byName, byAlias);
    }

    /// <summary>Свой контекст на базу хоста — чтобы видеть текст запросов и держать свою транзакцию.</summary>
    private AppDbContext Context(List<string> sql) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(fixture.Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres"))
        .LogTo(sql.Add, [Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuting])
        .Options);

    private static async Task<string> PlanAsync(AppDbContext db, string query) =>
        string.Join('\n', await ReadAsync(db, "EXPLAIN " + query, r => r.GetString(0)));

    private static async Task<List<Guid>> IdsAsync(AppDbContext db, string query) => 
        [.. (await ReadAsync(db, query, r => r.GetGuid(0))).Order()];

    private static async Task<List<T>> ReadAsync<T>(AppDbContext db, string text, Func<DbDataReader, T> read)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = text;
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(read(reader));
        return rows;
    }
}
