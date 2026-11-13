using System.Text.Json;
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
/// Миграция <c>TemplateVersionIntegrity</c> (id-template-version-integrity, задачи 2.2 и 4.1): база с
/// дублями из #1920 и #1924 и закреплениями за погашенными версиями после миграции приведена к
/// правилам D8, а индексы созданы.
///
/// <para>Проверяется на своей базе, доведённой до миграции ПЕРЕД этой: на базе прогона индексы уже
/// стоят, и дубли туда не положить. Тот же набор проходит и через доменную функцию
/// (<see cref="TemplateVersionRepair" /> + <see cref="DomainObject.RepinTemplates" />), которой
/// пользуется восстановление копии, — итоги обязаны совпасть: правило одно, а живёт в двух местах.</para>
/// </summary>
public class TemplateVersionIntegrityMigrationTests : IAsyncLifetime
{
    private readonly string _name = TestDatabases.Name("template_integrity");

    public async Task InitializeAsync()
    {
        await DropAsync();
        await AdminAsync($"CREATE DATABASE {TestDatabases.Quote(_name)}");
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("BHS_TEST_KEEP_DB") is not { Length: > 0 }) await DropAsync();
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int day) => T0.AddDays(day);

    // Идентификаторы заданы, чтобы проверить и разбор равенства по Id (порядок uuid — по тексту).
    private static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private sealed record Row(Guid Id, string Name, int Version, bool Active, bool Default, DateTimeOffset Created);

    /// <param name="Files">Шаблоны, которыми помечены выпущенные файлы документа.</param>
    private sealed record Doc(Guid Id, Guid? TemplateId, string? TemplateIds, string? TemplateParams, Guid[] Files);

    private static Row[] Rows(Guid type) =>
    [
        // #1924: два создания под одним названием — две действующие v1. Остаётся поздняя, получает v2
        // и «по умолчанию» ранней.
        new(G(11), "Основной", 1, true, true, At(1)),
        new(G(12), "Основной", 1, true, false, At(2)),
        // #1920: новая версия от исторической — второй v3 и вторая действующая.
        new(G(21), "Краткий", 1, false, false, At(1)),
        new(G(22), "Краткий", 2, false, false, At(2)),
        new(G(23), "Краткий", 3, true, false, At(3)),
        new(G(24), "Краткий", 3, true, false, At(4)),
        // Чистый шаблон — не трогается.
        new(G(31), "Акт", 1, false, false, At(1)),
        new(G(32), "Акт", 2, true, false, At(2)),
        // Повторный номер у погашенных при одной действующей.
        new(G(41), "Протокол", 1, false, false, At(1)),
        new(G(42), "Протокол", 1, false, false, At(2)),
        new(G(43), "Протокол", 2, true, false, At(3)),
        // Полное равенство номера и времени — решает идентификатор: остаётся больший по тексту.
        new(G(51), "Ведомость", 1, true, false, At(1)),
        new(G(52), "Ведомость", 1, true, false, At(1)),
    ];

    private static Doc[] Docs() =>
    [
        // Выбор погашаемой «Краткий» v3 и чистого «Акт» — порядок и переопределения сохраняются.
        new(G(101), null, $"[\"{G(23)}\",\"{G(32)}\"]", $"{{\"{G(23)}\":{{\"a\":1}},\"{G(32)}\":{{\"b\":2}}}}",
            [G(23), G(32)]),
        // Выбраны обе действующие «Краткого»: повтор убирается, переопределения оставшейся сильнее.
        new(G(102), null, $"[\"{G(23)}\",\"{G(24)}\"]", $"{{\"{G(23)}\":{{\"a\":1}},\"{G(24)}\":{{\"a\":2}}}}",
            [G(23), G(24)]),
        // Одиночный пин на погашенной — не трогается (D8, п. 3), с ним и его переопределения.
        new(G(103), G(23), null, $"{{\"{G(23)}\":{{\"x\":1}}}}", [G(23)]),
        // Выбор давно погашенной версии чистого шаблона — тоже к действующей.
        new(G(104), null, $"[\"{G(31)}\"]", null, []),
    ];

    [Fact]
    public async Task Дубли_починены_по_правилам_и_так_же_как_доменной_функцией()
    {
        await using var db = Context();
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_TemplateVersionIntegrity", StringComparison.Ordinal));
        Assert.True(index > 0, "Миграции TemplateVersionIntegrity в сборке нет — тест проверяет не то, что назвал.");
        await db.GetService<IMigrator>().MigrateAsync(all[index - 1]);

        var type = DocumentType.Create("АОСР", "AOSR", DocumentTypeKind.Document, null, JsonDocument.Parse("""{"fields":[]}"""),
            TypeOwner.Core, TypeVisibility.Shared);
        db.DocumentTypes.Add(type);
        await db.SaveChangesAsync();
        db.Templates.AddRange(Templates(type.Id));
        db.DomainObjects.AddRange(Objects(type.Id, withFiles: false));
        await db.SaveChangesAsync();
        foreach (var d in Docs())
            foreach (var t in d.Files)
                db.GeneratedFiles.Add(GeneratedFile.Restore(Guid.NewGuid(), d.Id, OutputFormat.Pdf, $"{d.Id}-{t}.pdf", t, T0, T0));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await db.Database.MigrateAsync();

        // Индексы на месте.
        var indexes = await db.Database
            .SqlQuery<string>($"""SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'templates'""")
            .ToListAsync();
        Assert.Contains("UX_templates_one_active", indexes);
        Assert.Contains("UX_templates_version", indexes);

        var stored = await db.Templates.AsNoTracking().ToDictionaryAsync(t => t.Id);
        // Правила D8 — явно, а не только через сравнение двух реализаций.
        Assert.Equal((false, false, 1), State(stored[G(11)]));
        Assert.Equal((true, true, 2), State(stored[G(12)]));
        Assert.Equal((false, false, 3), State(stored[G(23)]));
        Assert.Equal((true, false, 4), State(stored[G(24)]));
        Assert.Equal((true, false, 2), State(stored[G(32)]));
        Assert.Equal((false, false, 3), State(stored[G(42)]));
        Assert.Equal((false, false, 1), State(stored[G(51)]));
        Assert.Equal((true, false, 2), State(stored[G(52)]));

        var docs = await db.DomainObjects.AsNoTracking().Include(o => o.Facet).ThenInclude(f => f!.GeneratedFiles)
            .Where(o => o.Facet != null).ToDictionaryAsync(o => o.Id);
        Assert.Equal([G(24), G(32)], Ids(docs[G(101)].TemplateIds));
        Assert.Equal([G(24)], Ids(docs[G(102)].TemplateIds));
        Assert.Equal("""{"a":2}""", Params(docs[G(102)].TemplateParams)[G(24).ToString()]);
        Assert.Equal(G(23), docs[G(103)].TemplateId);
        Assert.Equal([G(23).ToString()], Params(docs[G(103)].TemplateParams).Keys);
        Assert.Equal([G(32)], Ids(docs[G(104)].TemplateIds));
        // Выпущенный файл выбранной погашенной версии — к действующей; файл действующей уже есть — прежний
        // остаётся; одиночный пин не тронут вместе с его файлом.
        Assert.Equal([G(24), G(32)], FileTemplates(docs[G(101)]));
        Assert.Equal([G(23), G(24)], FileTemplates(docs[G(102)]));
        Assert.Equal([G(23)], FileTemplates(docs[G(103)]));

        // Та же починка доменной функцией — итог тот же.
        var memory = Templates(type.Id);
        TemplateVersionRepair.Apply(memory);
        foreach (var t in memory) Assert.Equal(State(t), State(stored[t.Id]));
        var map = TemplateVersionRepair.ActiveByRetired(memory.Select(t => (t.Id, t.DocumentTypeId, t.Name, t.IsActive)));
        foreach (var o in Objects(type.Id, withFiles: true))
        {
            o.RepinTemplates(map, selectionOnly: true);
            var d = docs[o.Id];
            Assert.Equal(o.TemplateId, d.TemplateId);
            Assert.Equal(Ids(o.TemplateIds), Ids(d.TemplateIds));
            Assert.Equal(Params(o.TemplateParams), Params(d.TemplateParams));
            Assert.Equal(FileTemplates(o), FileTemplates(d));
        }
    }

    private static List<Template> Templates(Guid type) => Rows(type)
        .Select(r => Template.Restore(r.Id, type, r.Name, $"= {r.Name} v{r.Version}", r.Version,
            r.Active, r.Default, r.Created, r.Created))
        .ToList();

    private static List<DomainObject> Objects(Guid type, bool withFiles) => Docs()
        .Select(d =>
        {
            var o = DomainObject.RestoreDocument(d.Id, type, $"Документ {d.Id}", JsonDocument.Parse("{}"),
                Guid.NewGuid(), T0, T0, null, DocumentStatus.Generated, 0, d.TemplateId, d.TemplateIds,
                d.TemplateParams, JsonDocument.Parse("{}"));
            if (withFiles)
                foreach (var t in d.Files) o.AddGeneratedFile(OutputFormat.Pdf, $"{d.Id}-{t}.pdf", t);
            return o;
        })
        .ToList();

    private static List<Guid> FileTemplates(DomainObject o)
        => o.GeneratedFiles.Select(f => f.TemplateId!.Value).Order().ToList();

    private static (bool Active, bool Default, int Version) State(Template t) => (t.IsActive, t.IsDefault, t.Version);

    private static List<Guid> Ids(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<Guid>>(json)!;

    private static SortedDictionary<string, string> Params(string? json)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;
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
