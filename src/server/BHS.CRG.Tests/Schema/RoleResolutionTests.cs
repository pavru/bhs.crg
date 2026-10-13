using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Справочник по роли и его поле по тэгу (issue #1101) — расчёт, которым пользуются и порт модулей,
/// и проверка покрытия. Каждый тест — про ответ, который иначе был бы молчаливым выбором.
/// </summary>
public class RoleResolutionTests
{
    private const string Role = FunctionalTag.RoleOrganization;
    private static readonly TagCatalog Tags = TagCatalog.Build(TagRegistry.Core, []);
    private static readonly TagDefinition TaxId = Tags.Find(FunctionalTag.OrgTaxId)!;
    private static readonly TagDefinition Name = Tags.Find(FunctionalTag.OrgName)!;

    [Fact]
    public void Роль_не_назначена_никому_справочника_нет()
    {
        var resolved = RoleResolution.Role(Role, [Type("Организация", """{"fields":[]}""")]);

        Assert.Null(resolved.Root);
        Assert.Empty(resolved.Bearers);
        Assert.Empty(resolved.Family);
    }

    [Fact]
    public void Справочник_это_носитель_и_его_подтипы_как_бы_он_ни_назывался()
    {
        // Код типа — не «Организация»: имя справочника — дело администратора.
        var root = Type("Контрагент", Bearer());
        var child = Type("КонтрагентСРО", """{"fields":[]}""", root.Id);
        var grandchild = Type("КонтрагентСРОМосква", """{"fields":[]}""", child.Id);
        var stranger = Type("Номенклатура", """{"fields":[]}""");

        var resolved = RoleResolution.Role(Role, [stranger, grandchild, child, root]);

        Assert.Same(root, resolved.Root);
        Assert.Equal(
            new[] { "Контрагент", "КонтрагентСРО", "КонтрагентСРОМосква" },
            resolved.Family.Values.Order());
    }

    /// <summary>
    /// Двух носителей обычное сохранение не допустит, а восстановление копии и включение модуля —
    /// допустят. Выбрать одного из двух значило бы читать то один справочник, то другой.
    /// </summary>
    [Fact]
    public void Два_носителя_роль_не_обеспечена_а_не_выбран_первый()
    {
        var first = Type("Организация", Bearer());
        var second = Type("Контрагент", Bearer());

        var resolved = RoleResolution.Role(Role, [first, second]);

        Assert.Null(resolved.Root);
        Assert.Equal(2, resolved.Bearers.Count);
        Assert.Empty(resolved.Family);
    }

    /// <summary>Подтип роль наследует, но носителем не становится — иначе потолок считал бы семью.</summary>
    [Fact]
    public void Подтип_носителя_вторым_носителем_не_считается()
    {
        var root = Type("Организация", Bearer());
        var child = Type("ОрганизацияСРО", """{"fields":[]}""", root.Id);

        Assert.Same(root, RoleResolution.Role(Role, [root, child]).Root);
    }

    [Fact]
    public void Поле_находится_по_тэгу_а_не_по_ключу()
    {
        var root = Type("Организация", Bearer("""{"key":"НалоговыйНомер","type":"primitive","tags":["org.inn"]}"""));
        var all = new[] { root };

        var field = RoleResolution.Field(RoleResolution.Role(Role, all), TaxId, all);

        Assert.Equal(RoleFieldFault.None, field.Fault);
        Assert.Equal("НалоговыйНомер", field.Key);
    }

    [Fact]
    public void Тэга_нет_ни_на_одном_поле_не_назначено()
    {
        var root = Type("Организация", Bearer("""{"key":"ИНН","type":"string"}"""));
        var all = new[] { root };

        var field = RoleResolution.Field(RoleResolution.Role(Role, all), TaxId, all);

        Assert.Equal(RoleFieldFault.Absent, field.Fault);
        Assert.Null(field.Key);
    }

    [Fact]
    public void Тэг_на_двух_полях_не_читается_ни_одно()
    {
        var root = Type("Организация", Bearer(
            """{"key":"ИНН","type":"string","tags":["org.inn"]}""",
            """{"key":"ИННСтарый","type":"string","tags":["org.inn"]}"""));
        var all = new[] { root };

        var field = RoleResolution.Field(RoleResolution.Role(Role, all), TaxId, all);

        Assert.Equal(RoleFieldFault.Several, field.Fault);
        Assert.Null(field.Key);
        Assert.Equal(new[] { "ИНН", "ИННСтарый" }, field.Keys);
    }

    /// <summary>Сервер вид поля при сохранении не сверяет — поле можно сделать составным, оставив тэг.</summary>
    [Theory]
    [InlineData("""{"key":"ИНН","type":"complex","tags":["org.inn"]}""")]
    [InlineData("""{"key":"ИНН","type":"string","computed":true,"expression":"1","tags":["org.inn"]}""")]
    public void Поле_не_того_вида_или_расчётное_нарушение(string declared)
    {
        var root = Type("Организация", Bearer(declared));
        var all = new[] { root };

        var field = RoleResolution.Field(RoleResolution.Role(Role, all), TaxId, all);

        Assert.Equal(RoleFieldFault.WrongKind, field.Fault);
        Assert.Null(field.Key);
    }

    /// <summary>Название организации в живой базе — составное, и тэг его допускает.</summary>
    [Fact]
    public void Составное_поле_годится_тэгу_который_его_допускает()
    {
        var root = Type("Организация", Bearer("""{"key":"Наименование","type":"complex","tags":["org.name"]}"""));
        var all = new[] { root };

        Assert.Equal("Наименование", RoleResolution.Field(RoleResolution.Role(Role, all), Name, all).Key);
    }

    /// <summary>
    /// Потомок вправе исключить помеченное поле предка и пометить своё — для схемы это законно. А
    /// справочник читают одним ключом на всю семью: у записей подтипа значение лежало бы в другом поле.
    /// </summary>
    [Fact]
    public void У_подтипа_тэг_на_другом_поле_нарушение_с_названием_подтипа()
    {
        var root = Type("Организация", Bearer("""{"key":"ИНН","type":"string","tags":["org.inn"]}"""));
        var child = Type("ОрганизацияСРО",
            """{"excludedFields":["ИНН"],"fields":[{"key":"ИННСРО","type":"string","tags":["org.inn"]}]}""", root.Id);
        var all = new[] { root, child };

        var field = RoleResolution.Field(RoleResolution.Role(Role, all), TaxId, all);

        Assert.Equal(RoleFieldFault.DiffersInSubtype, field.Fault);
        Assert.Null(field.Key);
        Assert.Same(child, field.Subtype);
    }

    [Fact]
    public void Роль_не_обеспечена_поле_не_ищется()
    {
        var all = new[] { Type("Организация", """{"fields":[{"key":"ИНН","type":"string","tags":["org.inn"]}]}""") };

        Assert.Equal(RoleFieldFault.Absent, RoleResolution.Field(RoleResolution.Role(Role, all), TaxId, all).Fault);
    }

    private static string Bearer(params string[] fields) =>
        $$"""{"tags":["{{Role}}"],"fields":[{{string.Join(",", fields)}}]}""";

    private static DocumentType Type(string code, string schema, Guid? parent = null) =>
        DocumentType.Create(code, code, DocumentTypeKind.Composite, parent, JsonDocument.Parse(schema),
            TypeOwner.Core, TypeVisibility.Shared);
}
