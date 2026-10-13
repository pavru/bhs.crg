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

    /// <summary>
    /// Подтип, который помеченное поле просто ИСКЛЮЧИЛ, справочник не останавливает (ревью PR #1317):
    /// у его записей значения нет, и только. До ролей было так же — «ИНН» читался у всех остальных.
    /// </summary>
    [Fact]
    public void Подтип_исключивший_поле_справочник_не_останавливает()
    {
        var root = Type("Организация", Bearer("""{"key":"ИНН","type":"string","tags":["org.inn"]}"""));
        var child = Type("ОрганизацияСРО", """{"excludedFields":["ИНН"],"fields":[]}""", root.Id);
        var all = new[] { root, child };

        var field = RoleResolution.Field(RoleResolution.Role(Role, all), TaxId, all);

        Assert.Equal(RoleFieldFault.None, field.Fault);
        Assert.Equal("ИНН", field.Key);
    }

    /// <summary>
    /// Роль на документе: сохранение схемы её не даст, а восстановление копии и правка базы — дадут.
    /// Справочника тогда нет ни для кого — а не «упал запуск» у одного читателя и 500 у другого.
    /// </summary>
    [Fact]
    public void Роль_на_документе_не_обеспечена_и_причина_называет_тип()
    {
        var document = DocumentType.Create("Счёт", "Счёт", DocumentTypeKind.Document, null,
            JsonDocument.Parse(Bearer()), TypeOwner.Core, TypeVisibility.Shared);

        var resolved = RoleResolution.Role(Role, [document]);

        Assert.Null(resolved.Root);
        Assert.Empty(resolved.Family);
        Assert.Same(document, resolved.Unfit);
        Assert.Contains("«Счёт»", resolved.Describe(Tags.Find(Role)!));
        Assert.Contains("поставить нельзя", Assert.Single(RoleResolution.UnfitBearerProblems(Tags, document)));
    }

    [Fact]
    public void Составному_типу_роль_ставится_без_возражений()
    {
        Assert.Empty(RoleResolution.UnfitBearerProblems(Tags, Type("Организация", Bearer())));
        Assert.Null(RoleResolution.Role(Role, [Type("Организация", Bearer())]).Describe(Tags.Find(Role)!));
    }

    /// <summary>Каждая причина называется своими словами — и подтип по имени.</summary>
    [Fact]
    public void Причина_несопоставленного_поля_называет_подтип_и_ключи()
    {
        var root = Type("Организация", Bearer("""{"key":"ИНН","type":"string","tags":["org.inn"]}"""));
        var child = Type("ОрганизацияСРО",
            """{"excludedFields":["ИНН"],"fields":[{"key":"ИННСРО","type":"string","tags":["org.inn"]}]}""", root.Id);
        var all = new[] { root, child };
        Assert.Contains("«ОрганизацияСРО»", RoleResolution.Field(RoleResolution.Role(Role, all), TaxId, all).Describe(TaxId));

        var twice = Type("Организация", Bearer(
            """{"key":"ИНН","type":"string","tags":["org.inn"]}""", """{"key":"ИНН2","type":"string","tags":["org.inn"]}"""));
        var words = RoleResolution.Field(RoleResolution.Role(Role, [twice]), TaxId, [twice]).Describe(TaxId)!;
        Assert.Contains("«ИНН»", words);
        Assert.Contains("«ИНН2»", words);
    }

    private static string Bearer(params string[] fields) =>
        $$"""{"tags":["{{Role}}"],"fields":[{{string.Join(",", fields)}}]}""";

    private static DocumentType Type(string code, string schema, Guid? parent = null) =>
        DocumentType.Create(code, code, DocumentTypeKind.Composite, parent, JsonDocument.Parse(schema),
            TypeOwner.Core, TypeVisibility.Shared);
}
