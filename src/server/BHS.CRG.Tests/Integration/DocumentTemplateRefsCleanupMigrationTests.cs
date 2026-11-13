using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Миграция <c>DocumentTemplateRefsCleanup</c> (issue #1960): ссылки документов на несуществующие
/// шаблоны убраны, живые не тронуты. Тот же набор проходит через
/// <see cref="DomainObject.ForgetUnknownTemplates" />, которой пользуется восстановление копии, — итоги
/// обязаны совпасть: правило одно, а живёт в двух местах.
/// </summary>
public class DocumentTemplateRefsCleanupMigrationTests : IAsyncLifetime
{
    private readonly string _name = TestDatabases.Name("template_refs_cleanup");

    public async Task InitializeAsync()
    {
        await DropAsync();
        await AdminAsync($"CREATE DATABASE {TestDatabases.Quote(_name)}");
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("BHS_TEST_KEEP_DB") is not { Length: > 0 }) await DropAsync();
    }

    private static readonly DateTimeOffset T0 = new(2026, 7, 19, 9, 0, 0, TimeSpan.Zero);
    private static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    // Живые шаблоны — 1 и 2; 91 и 92 удалены до #364.
    private sealed record Doc(Guid Id, Guid? TemplateId, string? TemplateIds, string? TemplateParams);

    private static string P(params int[] ids) =>
        new JsonObject(ids.Select(n => KeyValuePair.Create<string, JsonNode?>(G(n).ToString(), new JsonObject { ["v"] = n })))
            .ToJsonString();

    private static Doc[] Docs() =>
    [
        // Как в копии рабочей базы: висячие вперемешку с живыми, с их параметрами. Висячий — первым.
        new(G(101), null, $"[\"{G(91)}\",\"{G(2)}\",\"{G(92)}\",\"{G(1)}\"]", P(91, 2, 92)),
        // Только висячие — выбор и параметры становятся null; одиночный пин на удалённом — тоже.
        new(G(102), G(91), $"[\"{G(91)}\"]", P(91)),
        // Чистый документ, даже с повтором в выборе, — не трогается.
        new(G(103), G(1), $"[\"{G(1)}\",\"{G(1)}\"]", P(1)),
        // Старый плоский вид параметров — ключи не шаблоны.
        new(G(104), null, null, """{"x":1}"""),
    ];

    [Fact]
    public async Task Висячие_ссылки_убраны_так_же_как_доменной_функцией()
    {
        await using var db = Context();
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_DocumentTemplateRefsCleanup", StringComparison.Ordinal));
        Assert.True(index > 0, "Миграции DocumentTemplateRefsCleanup в сборке нет — тест проверяет не то, что назвал.");
        await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

        var type = DocumentType.Create("АОСР", "AOSR", DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared);
        db.DocumentTypes.Add(type);
        await db.SaveChangesAsync();
        db.Templates.AddRange(
            Template.Restore(G(1), type.Id, "Основной", "= 1", 1, true, true, T0, T0),
            Template.Restore(G(2), type.Id, "Краткий", "= 2", 1, true, false, T0, T0));
        db.DomainObjects.AddRange(Objects(type.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await db.Database.MigrateAsync();

        var docs = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)
            .Where(o => o.Facet != null).ToDictionaryAsync(o => o.Id);
        // Правило — явно, а не только через сравнение двух реализаций.
        Assert.Equal([G(2), G(1)], Ids(docs[G(101)].TemplateIds));
        Assert.Equal(Params(P(2)), Params(docs[G(101)].TemplateParams));
        Assert.Null(docs[G(102)].TemplateId);
        Assert.Null(docs[G(102)].TemplateIds);
        Assert.Null(docs[G(102)].TemplateParams);
        Assert.Equal(G(1), docs[G(103)].TemplateId);
        Assert.Equal([G(1), G(1)], Ids(docs[G(103)].TemplateIds));
        Assert.Equal(Params(P(1)), Params(docs[G(103)].TemplateParams));
        Assert.Null(docs[G(104)].TemplateParams);

        // Та же чистка доменной функцией — итог тот же.
        var known = new HashSet<Guid> { G(1), G(2) };
        foreach (var o in Objects(type.Id))
        {
            o.ForgetUnknownTemplates(known);
            var d = docs[o.Id];
            Assert.Equal(o.TemplateId, d.TemplateId);
            Assert.Equal(Ids(o.TemplateIds), Ids(d.TemplateIds));
            Assert.Equal(Params(o.TemplateParams), Params(d.TemplateParams));
        }
    }

    private static List<DomainObject> Objects(Guid type) => Docs()
        .Select(d => DomainObject.RestoreDocument(d.Id, type, $"Документ {d.Id}", JsonDocument.Parse("{}"),
            Guid.NewGuid(), T0, T0, null, DocumentStatus.Draft, 0, d.TemplateId, d.TemplateIds,
            d.TemplateParams, JsonDocument.Parse("{}")))
        .ToList();

    private static List<Guid>? Ids(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<Guid>>(json)!;

    private static SortedDictionary<string, string>? Params(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in JsonDocument.Parse(json).RootElement.EnumerateObject())
            result[p.Name] = JsonSerializer.Serialize(p.Value);
        return result;
    }

    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString)
        {
            Database = _name,
            CommandTimeout = 300,
        }.ConnectionString)
        .Options);

    private Task DropAsync() => AdminAsync($"DROP DATABASE IF EXISTS {TestDatabases.Quote(_name)} WITH (FORCE)");

    private static async Task AdminAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString)
        {
            Database = "postgres",
            CommandTimeout = 300,
            Timeout = 60,
        }.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
