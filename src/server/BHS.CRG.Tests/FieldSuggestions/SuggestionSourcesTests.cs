using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Documents;
using BHS.CRG.Application.Objects.FieldSuggestions;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Tests.FieldSuggestions;

/// <summary>
/// План источников подсказки (issue #1530): семья типа, перекрытие ключа, права — без базы.
/// </summary>
public class SuggestionSourcesTests
{
    private static DocumentType Type(string code, string schema, DocumentType? parent = null,
        DocumentTypeKind kind = DocumentTypeKind.Composite, TypeStorage storage = TypeStorage.SharedObject) =>
        DocumentType.Create(code, code, kind, parent?.Id, JsonDocument.Parse(schema), TypeOwner.Core,
            TypeVisibility.Shared, storage: storage);

    private static Dictionary<Guid, DocumentType> Types(params DocumentType[] types) => types.ToDictionary(t => t.Id);

    private const string Maker = """{"fields":[{"key":"Производитель","type":"string"}]}""";

    private static readonly DocumentType Base = Type("Номенклатура",
        """{"tags":["role.nomenclature"],"fields":[{"key":"Производитель","type":"string"},{"key":"ИНН","type":"string","tags":["org.inn"]}]}""");

    private static SuggestionPlan.Read Read(SuggestionPlan plan) => Assert.IsType<SuggestionPlan.Read>(plan);

    [Fact]
    public void Тип_потомка_ведёт_к_объявившему_и_всей_его_семье()
    {
        var cable = Type("Кабель", """{"fields":[]}""", Base);
        var copper = Type("Медный", """{"fields":[]}""", cable);
        var types = Types(Base, cable, copper);

        var plan = Read(SuggestionSources.For(copper.Id, "Производитель", types, TestAccess.All));

        Assert.Equal(Base.Id, plan.Declarer);
        Assert.Equal(new[] { Base.Id, cable.Id, copper.Id }.Order(), plan.Types.Order());
    }

    [Fact]
    public void Потомок_объявивший_ключ_своим_выпадает_вместе_со_своими_потомками()
    {
        var own = Type("СвоёПоле", Maker, Base);
        var under = Type("ПодСвоим", """{"fields":[]}""", own);
        var types = Types(Base, own, under);

        Assert.Equal([Base.Id], Read(SuggestionSources.For(Base.Id, "Производитель", types, TestAccess.All)).Types);
        // Спрошенный по своему ключу — своя семья, без родителя.
        var plan = Read(SuggestionSources.For(under.Id, "Производитель", types, TestAccess.All));
        Assert.Equal(own.Id, plan.Declarer);
        Assert.Equal(new[] { own.Id, under.Id }.Order(), plan.Types.Order());
    }

    [Fact]
    public void Потомок_исключивший_ключ_выпадает()
    {
        var without = Type("БезПроизводителя", """{"fields":[],"excludedFields":["Производитель"]}""", Base);

        Assert.Equal([Base.Id],
            Read(SuggestionSources.For(Base.Id, "Производитель", Types(Base, without), TestAccess.All)).Types);
        // А у самого исключившего поля нет — и это ошибка адреса, а не пусто.
        Assert.IsType<SuggestionPlan.Unknown>(
            SuggestionSources.For(without.Id, "Производитель", Types(Base, without), TestAccess.All));
    }

    [Fact]
    public void Неизвестные_тип_и_поле_это_ошибка_адреса()
    {
        Assert.IsType<SuggestionPlan.Unknown>(SuggestionSources.For(Guid.NewGuid(), "Производитель", Types(Base), TestAccess.All));
        Assert.IsType<SuggestionPlan.Unknown>(SuggestionSources.For(Base.Id, "Цвет", Types(Base), TestAccess.All));
    }

    [Fact]
    public void Поле_которое_не_подсказывается_молчит_с_кодом_правила()
    {
        var plan = Assert.IsType<SuggestionPlan.Silent>(SuggestionSources.For(Base.Id, "ИНН", Types(Base), TestAccess.All));
        Assert.Equal("inn", plan.Reason);
    }

    [Fact]
    public void Документы_только_с_правом_и_включённым_модулем()
    {
        var parent = Type("Основа", Maker);
        var doc = Type("Акт", """{"fields":[]}""", parent, DocumentTypeKind.Document);
        var types = Types(parent, doc);

        Assert.Equal(new[] { parent.Id, doc.Id }.Order(),
            Read(SuggestionSources.For(parent.Id, "Производитель", types, TestAccess.All)).Types.Order());
        Assert.Equal([parent.Id],
            Read(SuggestionSources.For(parent.Id, "Производитель", types,
                TestAccess.With(SuggestionSources.CatalogRead))).Types);
        Assert.Equal([parent.Id],
            Read(SuggestionSources.For(parent.Id, "Производитель", types,
                TestAccess.WithoutModules(SuggestionSources.CatalogRead, SuggestionSources.DocumentRead))).Types);
    }

    [Fact]
    public void Без_прав_на_все_источники_отказ_а_выключенный_модуль_это_пусто()
    {
        var doc = Type("Акт", Maker, kind: DocumentTypeKind.Document);
        var types = Types(Base, doc);

        Assert.IsType<SuggestionPlan.Forbidden>(
            SuggestionSources.For(Base.Id, "Производитель", types, TestAccess.With(SuggestionSources.DocumentRead)));
        Assert.IsType<SuggestionPlan.Forbidden>(
            SuggestionSources.For(doc.Id, "Производитель", types, TestAccess.With(SuggestionSources.CatalogRead)));
        var off = Assert.IsType<SuggestionPlan.Silent>(SuggestionSources.For(doc.Id, "Производитель", types,
            TestAccess.WithoutModules(SuggestionSources.DocumentRead)));
        Assert.Equal(SuggestionSources.ModuleOff, off.Reason);
    }

    [Fact]
    public void Справочник_номенклатуры_открыт_праву_шага_новая_позиция()
    {
        var kind = Type("Кабель", """{"fields":[]}""", Base);
        var other = Type("Организация", Maker);
        var nomenclature = TestAccess.With(SuggestionSources.NomenclatureEdit);

        Assert.Equal(new[] { Base.Id, kind.Id }.Order(),
            Read(SuggestionSources.For(kind.Id, "Производитель", Types(Base, kind, other), nomenclature)).Types.Order());
        Assert.IsType<SuggestionPlan.Forbidden>(
            SuggestionSources.For(other.Id, "Производитель", Types(Base, kind, other), nomenclature));
    }

    [Fact]
    public void Тип_в_таблице_модуля_не_читается()
    {
        var moduleOwned = Type("Счёт", """{"fields":[]}""", Base, storage: TypeStorage.ModuleTable);

        Assert.Equal([Base.Id],
            Read(SuggestionSources.For(Base.Id, "Производитель", Types(Base, moduleOwned), TestAccess.All)).Types);
    }

    /// <summary>Коды прав записаны в слое приложения строками: им не дать разойтись с объявленными.</summary>
    [Fact]
    public void Коды_прав_совпадают_с_объявленными()
    {
        Assert.Equal(CorePermissions.CatalogRead, SuggestionSources.CatalogRead);
        Assert.Equal(NomenclatureIntakeEndpoints.Permission, SuggestionSources.NomenclatureEdit);
        Assert.Contains(SystemRoles.All.SelectMany(r => r.Permissions), p => p == SuggestionSources.DocumentRead);
        Assert.Equal("role.nomenclature", FunctionalTag.RoleNomenclature);
    }
}
