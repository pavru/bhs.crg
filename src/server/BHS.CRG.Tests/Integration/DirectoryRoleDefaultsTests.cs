using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Роли справочников действующим установкам (issue #1101): правило исполняется ДВАЖДЫ — миграцией
/// <c>DirectoryRoles</c> в SQL и функцией <see cref="DirectoryRoleDefaults" /> при восстановлении
/// копии — и обязано давать одно и то же.
///
/// <para>Каждый случай прогоняется обоими исполнениями на одних и тех же схемах. Разойдись они —
/// установка, обновлённая миграцией, и установка, восстановленная из вчерашней копии, читали бы
/// справочники по-разному, и ни одна проверка этого не показала бы.</para>
///
/// <para>SQL идёт по ВРЕМЕННОЙ таблице <c>document_types</c> внутри откатываемой транзакции:
/// временная схема стоит в пути поиска первой, поэтому текст миграции находит её, а не настоящую.
/// Типы общей тестовой базы при этом не видны и не тронуты.</para>
/// </summary>
[Collection("Integration")]
public class DirectoryRoleDefaultsTests(IntegrationTestFixture host)
{
    private sealed record Seed(string Code, string Schema, string Kind = "Composite");

    private const string Live = """
        [{"Code":"Организация","Schema":"{\"fields\":[{\"key\":\"Наименование\",\"type\":\"complex\"},{\"key\":\"ИНН\",\"type\":\"primitive\"},{\"key\":\"КПП\",\"type\":\"string\"}]}"},
         {"Code":"ОрганизацияСРО","Schema":"{\"fields\":[{\"key\":\"ИНН\",\"type\":\"string\"}]}"},
         {"Code":"Номенклатура","Schema":"{\"fields\":[{\"key\":\"Наименование\",\"type\":\"string\",\"tags\":[\"identity:1\"]}]}"}]
        """;

    public static TheoryData<string, string> Cases => new()
    {
        // Как лежит в живой базе: «ИНН» — примитив, «Наименование» — составное, у номенклатуры —
        // строка с тэгом идентичности. Подтип роль не получает: он её наследует.
        { "как в живой базе", Live },
        { "типов нет вовсе", """[{"Code":"Материал","Schema":"{\"fields\":[]}"}]""" },
        // Роль уже несёт другой тип — прежний не получает ни роли, ни тэгов полей.
        { "роль занята другим типом", """
            [{"Code":"Контрагент","Schema":"{\"tags\":[\"role.organization\"],\"fields\":[]}"},
             {"Code":"Организация","Schema":"{\"fields\":[{\"key\":\"ИНН\",\"type\":\"string\"}]}"}]
            """ },
        { "роль уже стоит, тэгов полей нет", """
            [{"Code":"Организация","Schema":"{\"tags\":[\"type.x\",\"role.organization\"],\"fields\":[{\"key\":\"ИНН\",\"type\":\"string\",\"tags\":[\"identity\"]},{\"key\":\"Наименование\",\"type\":\"text\"}]}"}]
            """ },
        { "ИНН составной — тэга не получает", """
            [{"Code":"Организация","Schema":"{\"fields\":[{\"key\":\"ИНН\",\"type\":\"complex\"},{\"key\":\"Наименование\",\"type\":\"string\"}]}"}]
            """ },
        // Человек уже пометил другое поле — его решение не трогаем.
        { "тэг ИНН уже на другом поле", """
            [{"Code":"Организация","Schema":"{\"fields\":[{\"key\":\"ИНН\",\"type\":\"string\"},{\"key\":\"НалоговыйНомер\",\"type\":\"string\",\"tags\":[\"org.inn\"]}]}"}]
            """ },
        { "схема без полей и без тэгов", """[{"Code":"Организация","Schema":"{}"},{"Code":"Номенклатура","Schema":"{\"tags\":[]}"}]""" },
        // Справочник — составной тип; документ с тем же кодом им не становится.
        { "код занят документом", """[{"Code":"Организация","Schema":"{\"fields\":[{\"key\":\"ИНН\",\"type\":\"string\"}]}","Kind":"Document"}]""" },
        { "полей с прежними ключами нет", """[{"Code":"Организация","Schema":"{\"fields\":[{\"key\":\"Название\",\"type\":\"string\"}]}"}]""" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Миграция_и_правило_восстановления_дают_одно_и_то_же(string name, string seedsJson)
    {
        var seeds = JsonSerializer.Deserialize<Seed[]>(seedsJson)!;

        var bySql = await MigratedAsync(seeds);
        var byCode = Applied(seeds);

        foreach (var seed in seeds)
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(bySql[seed.Code]), JsonNode.Parse(byCode[seed.Code])),
                $"«{name}», тип «{seed.Code}»: миграция дала {bySql[seed.Code]}, правило — {byCode[seed.Code]}");
    }

    /// <summary>
    /// Сам результат — на живой форме: без этого два исполнения могли бы сходиться в том, что обе
    /// ничего не делают.
    /// </summary>
    [Fact]
    public void На_живой_форме_роли_и_тэги_встают_на_свои_места()
    {
        var after = Applied(JsonSerializer.Deserialize<Seed[]>(Live)!);

        var organization = JsonNode.Parse(after["Организация"])!;
        Assert.Equal("role.organization", organization["tags"]![0]!.GetValue<string>());
        Assert.Equal("org.name", organization["fields"]![0]!["tags"]![0]!.GetValue<string>());
        Assert.Equal("org.inn", organization["fields"]![1]!["tags"]![0]!.GetValue<string>());
        Assert.Null(organization["fields"]![2]!["tags"]);

        // Подтип не тронут: роль он наследует, а тэг на его «ИНН» дал бы два поля с одним тэгом.
        Assert.Null(JsonNode.Parse(after["ОрганизацияСРО"])!["tags"]);

        var nomenclature = JsonNode.Parse(after["Номенклатура"])!;
        Assert.Equal("role.nomenclature", nomenclature["tags"]![0]!.GetValue<string>());
        Assert.Equal("identity:1", nomenclature["fields"]![0]!["tags"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Повторное_применение_ничего_не_меняет()
    {
        var seeds = JsonSerializer.Deserialize<Seed[]>(Live)!;
        var once = Applied(seeds);

        var twice = Applied([.. seeds.Select(s => s with { Schema = once[s.Code] })]);

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// Правило не вправе ставить тэг туда, где его не допускает сам тэг: такое поле порт не прочитал
    /// бы, а редактор показал бы тэг на недопустимом месте.
    /// </summary>
    [Fact]
    public void Виды_полей_в_правиле_те_же_что_допускает_тэг_в_реестре()
    {
        var tags = TagCatalog.Build(TagRegistry.Core, []);

        foreach (var rule in DirectoryRoleDefaults.Rules)
        {
            Assert.True(tags.IsRole(rule.Role), rule.Role);
            foreach (var field in rule.Fields)
            {
                var tag = tags.Find(field.Tag)!;
                Assert.Equal(rule.Role, tag.Role);
                Assert.Equal(tag.AppliesTo.Order(), field.Kinds.Order());
            }
        }
    }

    private static Dictionary<string, string> Applied(Seed[] seeds)
    {
        var types = seeds.Select(s => DocumentType.Create(
            s.Code, s.Code, Enum.Parse<DocumentTypeKind>(s.Kind), null, JsonDocument.Parse(s.Schema),
            TypeOwner.Core, TypeVisibility.Shared)).ToList();

        var result = types.ToDictionary(t => t.Code, t => t.Schema.RootElement.GetRawText());
        foreach (var (type, schema) in DirectoryRoleDefaults.Apply(types))
            result[type.Code] = schema.RootElement.GetRawText();
        return result;
    }

    private async Task<Dictionary<string, string>> MigratedAsync(Seed[] seeds)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        await using var tx = await conn.BeginTransactionAsync();

        // Мимо EF: ExecuteSqlRaw разбирает текст на подстановки и спотыкается о $$ в теле миграции.
        await RunAsync(conn, tx, """
            CREATE TEMP TABLE document_types
                (LIKE public.document_types INCLUDING DEFAULTS) ON COMMIT DROP
            """);
        foreach (var seed in seeds)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO document_types ("Id", "Name", "Code", "Schema", "PluginBindings", "CreatedAt", "UpdatedAt", "Kind")
                VALUES (gen_random_uuid(), @code, @code, @schema::jsonb, '[]'::jsonb, now(), now(), @kind)
                """, conn, tx);
            insert.Parameters.AddWithValue("code", seed.Code);
            insert.Parameters.AddWithValue("schema", seed.Schema);
            insert.Parameters.AddWithValue("kind", seed.Kind);
            await insert.ExecuteNonQueryAsync();
        }

        await RunAsync(conn, tx, MigrationSql());

        var result = new Dictionary<string, string>();
        await using (var read = new NpgsqlCommand("""SELECT "Code", "Schema"::text FROM document_types""", conn, tx))
        await using (var reader = await read.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                result[reader.GetString(0)] = reader.GetString(1);

        await tx.RollbackAsync();
        return result;
    }

    private static async Task RunAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string MigrationSql()
    {
        var type = typeof(AppDbContext).Assembly.GetTypes()
            .Single(t => typeof(Migration).IsAssignableFrom(t) && t.Name == "DirectoryRoles");
        var migration = (Migration)Activator.CreateInstance(type)!;
        return string.Join("\n;\n", migration.UpOperations.OfType<SqlOperation>().Select(o => o.Sql));
    }
}
