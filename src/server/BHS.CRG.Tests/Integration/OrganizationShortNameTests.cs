using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Тэг сокращённого названия организации действующим установкам (issue #1473): правило исполняется
/// ДВАЖДЫ — миграцией <c>OrgShortNameTag</c> в SQL и <see cref="DirectoryRoleDefaults.ApplyParts" /> при
/// восстановлении копии — и обязано давать одно и то же. Приём тот же, что у <c>DirectoryRoleDefaultsTests</c>:
/// SQL идёт по ВРЕМЕННОЙ таблице внутри откатываемой транзакции, типы общей базы не тронуты.
///
/// <para>Ссылка поля на составной тип в схемах записана как <c>@Код</c>: идентификаторы у двух
/// исполнений одни и те же, подставляются перед прогоном.</para>
/// </summary>
[Collection("Integration")]
public class OrganizationShortNameTests(IntegrationTestFixture host)
{
    private sealed record Seed(string Code, string Schema, string Kind = "Composite", string? Parent = null);

    private const string NameType = """{"Code":"НаименованиеОрганизации","Schema":"{\"fields\":[{\"key\":\"Полное\",\"type\":\"string\",\"required\":true},{\"key\":\"Сокращённое\",\"type\":\"string\",\"tags\":[\"identity\"]}]}"}""";

    /// <summary>Как на копии базы заказчика (сверка 11.11.2026): «Наименование» — составное, часть «Сокращённое» — строка.</summary>
    private const string Customer = $$"""
        [{{NameType}},
         {"Code":"Организация","Schema":"{\"tags\":[\"role.organization\"],\"fields\":[{\"key\":\"Наименование\",\"type\":\"complex\",\"typeId\":\"@НаименованиеОрганизации\",\"tags\":[\"org.name\"]},{\"key\":\"ИНН\",\"type\":\"primitive\",\"tags\":[\"org.inn\"]}]}"},
         {"Code":"ОрганизацияСРО","Parent":"Организация","Schema":"{\"fields\":[]}"}]
        """;

    public static TheoryData<string, string> Cases => new()
    {
        { "как у заказчика", Customer },
        { "роль не назначена", $$"""
            [{{NameType}},
             {"Code":"Организация","Schema":"{\"fields\":[{\"key\":\"Наименование\",\"type\":\"complex\",\"typeId\":\"@НаименованиеОрганизации\",\"tags\":[\"org.name\"]}]}"}]
            """ },
        { "роль несут двое", $$"""
            [{{NameType}},
             {"Code":"Организация","Schema":"{\"tags\":[\"role.organization\"],\"fields\":[{\"key\":\"Наименование\",\"type\":\"complex\",\"typeId\":\"@НаименованиеОрганизации\",\"tags\":[\"org.name\"]}]}"},
             {"Code":"Контрагент","Schema":"{\"tags\":[\"role.organization\"],\"fields\":[]}"}]
            """ },
        // Тэг уже стоит — у поля организации или у другой части: решение человека, не трогаем.
        { "отмечено полем организации", $$"""
            [{{NameType}},
             {"Code":"Организация","Schema":"{\"tags\":[\"role.organization\"],\"fields\":[{\"key\":\"Наименование\",\"type\":\"complex\",\"typeId\":\"@НаименованиеОрганизации\",\"tags\":[\"org.name\"]},{\"key\":\"Краткое\",\"type\":\"string\",\"tags\":[\"org.shortName\"]}]}"}]
            """ },
        { "отмечена другая часть", """
            [{"Code":"НаименованиеОрганизации","Schema":"{\"fields\":[{\"key\":\"Полное\",\"type\":\"string\"},{\"key\":\"Сокращённое\",\"type\":\"string\"},{\"key\":\"Краткое\",\"type\":\"string\",\"tags\":[\"org.shortName\"]}]}"},
             {"Code":"Организация","Schema":"{\"tags\":[\"role.organization\"],\"fields\":[{\"key\":\"Наименование\",\"type\":\"complex\",\"typeId\":\"@НаименованиеОрганизации\",\"tags\":[\"org.name\"]}]}"}]
            """ },
        { "часть не строка", """
            [{"Code":"НаименованиеОрганизации","Schema":"{\"fields\":[{\"key\":\"Полное\",\"type\":\"string\"},{\"key\":\"Сокращённое\",\"type\":\"number\"}]}"},
             {"Code":"Организация","Schema":"{\"tags\":[\"role.organization\"],\"fields\":[{\"key\":\"Наименование\",\"type\":\"complex\",\"typeId\":\"@НаименованиеОрганизации\",\"tags\":[\"org.name\"]}]}"}]
            """ },
        { "название строкой", $$"""
            [{{NameType}},
             {"Code":"Организация","Schema":"{\"tags\":[\"role.organization\"],\"fields\":[{\"key\":\"Наименование\",\"type\":\"string\",\"tags\":[\"org.name\"]}]}"}]
            """ },
        { "ссылка поля в никуда", $$"""
            [{{NameType}},
             {"Code":"Организация","Schema":"{\"tags\":[\"role.organization\"],\"fields\":[{\"key\":\"Наименование\",\"type\":\"complex\",\"typeId\":\"00000000-0000-0000-0000-000000000001\",\"tags\":[\"org.name\"]}]}"}]
            """ },
        // Поле названия объявлено у родителя носителя — находится, как ИНН у DirectoryRoles.
        { "название у родителя", $$"""
            [{{NameType}},
             {"Code":"Лицо","Schema":"{\"fields\":[{\"key\":\"Наименование\",\"type\":\"complex\",\"typeId\":\"@НаименованиеОрганизации\",\"tags\":[\"org.name\"]}]}"},
             {"Code":"Организация","Parent":"Лицо","Schema":"{\"tags\":[\"role.organization\"],\"fields\":[]}"}]
            """ },
        { "часть без тэгов и схема без полей", """
            [{"Code":"НаименованиеОрганизации","Schema":"{\"fields\":[{\"key\":\"Сокращённое\",\"type\":\"text\"}]}"},
             {"Code":"Организация","Schema":"{\"tags\":[\"role.organization\"],\"fields\":[{\"key\":\"Наименование\",\"type\":\"complex\",\"typeId\":\"@НаименованиеОрганизации\",\"tags\":[\"org.name\"]}]}"},
             {"Code":"Пустой","Schema":"{}"}]
            """ },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Миграция_и_правило_восстановления_дают_одно_и_то_же(string name, string seedsJson)
    {
        var (types, _) = Types(JsonSerializer.Deserialize<Seed[]>(seedsJson)!);

        var bySql = await MigratedAsync(types);
        var byCode = Applied(types);

        foreach (var type in types)
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(bySql[type.Code]), JsonNode.Parse(byCode[type.Code])),
                $"«{name}», тип «{type.Code}»: миграция дала {bySql[type.Code]}, правило — {byCode[type.Code]}");
    }

    /// <summary>Результат по существу: сойтись в бездействии два исполнения могли бы и тут.</summary>
    [Fact]
    public void На_форме_заказчика_тэг_встаёт_на_часть_Сокращённое_и_второй_прогон_ничего_не_меняет()
    {
        var (types, _) = Types(JsonSerializer.Deserialize<Seed[]>(Customer)!);

        var changed = DirectoryRoleDefaults.ApplyParts(types);

        var (type, schema) = Assert.Single(changed);
        Assert.Equal("НаименованиеОрганизации", type.Code);
        var fields = JsonNode.Parse(schema.RootElement.GetRawText())!["fields"]!.AsArray();
        Assert.Null(fields[0]!["tags"]);
        Assert.Equal(["identity", FunctionalTag.OrgShortName], fields[1]!["tags"]!.AsArray().Select(t => t!.GetValue<string>()));

        type.UpdateSchema(schema);
        Assert.Empty(DirectoryRoleDefaults.ApplyParts(types));
    }

    [Theory]
    [InlineData("отмечено полем организации")]
    [InlineData("отмечена другая часть")]
    [InlineData("часть не строка")]
    [InlineData("название строкой")]
    [InlineData("роль несут двое")]
    public void Правило_не_трогает_занятое_и_неподходящее(string name)
    {
        var seeds = (string)Cases.Single(c => (string)c[0] == name)[1];
        Assert.Empty(DirectoryRoleDefaults.ApplyParts(Types(JsonSerializer.Deserialize<Seed[]>(seeds)!).Types));
    }

    /// <summary>Правило ставит тэг туда, где его допускает сам тэг: иначе порт такую часть не прочитал бы.</summary>
    [Fact]
    public void Части_в_правиле_совпадают_с_объявлением_тэга()
    {
        var tags = TagCatalog.Build(TagRegistry.Core, []);
        foreach (var rule in DirectoryRoleDefaults.Parts)
        {
            var tag = tags.Find(rule.Tag)!;
            Assert.Equal(rule.Role, tag.Role);
            Assert.Equal(rule.Whole, tag.PartOf);
            Assert.True(tag.Names);
            Assert.Equal(tag.AppliesTo.Order(), rule.Kinds.Order());
        }
    }

    /// <summary>Порт и экран сопоставления находят часть одним расчётом: поле — «Наименование», часть — «Сокращённое».</summary>
    [Fact]
    public void Расчёт_поля_роли_находит_тэг_на_части_составного()
    {
        var (types, _) = Types(JsonSerializer.Deserialize<Seed[]>(Customer)!);
        foreach (var (type, schema) in DirectoryRoleDefaults.ApplyParts(types)) type.UpdateSchema(schema);
        var tags = TagCatalog.Build(TagRegistry.Core, []);
        var role = RoleResolution.Role(FunctionalTag.RoleOrganization, types);

        var field = RoleResolution.Field(role, tags.Find(FunctionalTag.OrgShortName)!, types);

        Assert.Equal(RoleFieldFault.None, field.Fault);
        Assert.Equal("Наименование", field.Key);
        Assert.Equal("Сокращённое", field.Part);

        // Без тэга — «не назначено», ничего не угадывается по ключу «Сокращённое».
        var (bare, _) = Types(JsonSerializer.Deserialize<Seed[]>(Customer)!);
        Assert.Equal(RoleFieldFault.Absent,
            RoleResolution.Field(RoleResolution.Role(FunctionalTag.RoleOrganization, bare), tags.Find(FunctionalTag.OrgShortName)!, bare).Fault);
    }

    /// <summary>Сохранение схемы: тэг на части принимается, на двух полях одного типа — отказ (одиночный).</summary>
    [Fact]
    public async Task Сохранение_схемы_принимает_часть_и_отказывает_двум_полям()
    {
        using var scope = host.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var mark = Guid.NewGuid().ToString("N")[..8];

        var one = await mediator.Send(new CreateDocumentTypeCommand(
            $"Название {mark}", $"Название{mark}", DocumentTypeKind.Composite, null, JsonDocument.Parse("""
                {"fields":[{"key":"Полное","type":"string"},{"key":"Сокращённое","type":"string","tags":["org.shortName"]}]}
                """)));
        Assert.Contains(FunctionalTag.OrgShortName, one.Schema.RootElement.GetRawText());

        var twice = await Assert.ThrowsAsync<ConflictException>(() => mediator.Send(new CreateDocumentTypeCommand(
            $"Дважды {mark}", $"Дважды{mark}", DocumentTypeKind.Composite, null, JsonDocument.Parse("""
                {"fields":[{"key":"Сокращённое","type":"string","tags":["org.shortName"]},{"key":"Краткое","type":"string","tags":["org.shortName"]}]}
                """))));
        Assert.Contains("Сокращённое название организации", twice.Message);
    }

    // ── Оснастка ────────────────────────────────────────────────────────────

    /// <summary>Типы в памяти с подставленными ссылками <c>@Код</c> → идентификатор.</summary>
    private static (List<DocumentType> Types, Dictionary<string, Guid> Ids) Types(Seed[] seeds)
    {
        var types = new List<DocumentType>();
        foreach (var s in seeds)
            types.Add(DocumentType.Create(
                s.Code, s.Code, Enum.Parse<DocumentTypeKind>(s.Kind),
                s.Parent is null ? null : types.Single(t => t.Code == s.Parent).Id, JsonDocument.Parse("{}"),
                TypeOwner.Core, TypeVisibility.Shared));
        var ids = types.ToDictionary(t => t.Code, t => t.Id);

        foreach (var (type, seed) in types.Zip(seeds))
        {
            var schema = seed.Schema;
            foreach (var (code, id) in ids) schema = schema.Replace($"@{code}", id.ToString());
            type.UpdateSchema(JsonDocument.Parse(schema));
        }
        return (types, ids);
    }

    private static Dictionary<string, string> Applied(IReadOnlyList<DocumentType> types)
    {
        var result = types.ToDictionary(t => t.Code, t => t.Schema.RootElement.GetRawText());
        foreach (var (type, schema) in DirectoryRoleDefaults.ApplyParts(types))
            result[type.Code] = schema.RootElement.GetRawText();
        return result;
    }

    private async Task<Dictionary<string, string>> MigratedAsync(IReadOnlyList<DocumentType> types)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        await using var tx = await conn.BeginTransactionAsync();
        await RunAsync(conn, tx, """
            CREATE TEMP TABLE document_types
                (LIKE public.document_types INCLUDING DEFAULTS) ON COMMIT DROP
            """);
        foreach (var type in types)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO document_types ("Id", "ParentId", "Name", "Code", "Schema", "PluginBindings", "CreatedAt", "UpdatedAt", "Kind")
                VALUES (@id, @parent, @code, @code, @schema::jsonb, '[]'::jsonb, now(), now(), @kind)
                """, conn, tx);
            insert.Parameters.AddWithValue("id", type.Id);
            insert.Parameters.Add(new NpgsqlParameter("parent", NpgsqlTypes.NpgsqlDbType.Uuid)
                { Value = type.ParentId is { } p ? p : DBNull.Value });
            insert.Parameters.AddWithValue("code", type.Code);
            insert.Parameters.AddWithValue("schema", type.Schema.RootElement.GetRawText());
            insert.Parameters.AddWithValue("kind", type.Kind.ToString());
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
            .Single(t => typeof(Migration).IsAssignableFrom(t) && t.Name == "OrgShortNameTag");
        var migration = (Migration)Activator.CreateInstance(type)!;
        return string.Join("\n;\n", migration.UpOperations.OfType<SqlOperation>().Select(o => o.Sql));
    }
}
