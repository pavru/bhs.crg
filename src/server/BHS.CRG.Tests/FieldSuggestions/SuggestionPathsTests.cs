using System.Text.Json;
using BHS.CRG.Application.Objects.FieldSuggestions;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;

namespace BHS.CRG.Tests.FieldSuggestions;

/// <summary>
/// Пути к полю внутри чужих записей (issue #1532): из схем, по ключам, без ссылок, до глубины 6 —
/// и план источников, который их отбирает по хранению и правам. Без базы.
/// </summary>
public class SuggestionPathsTests
{
    private static DocumentType Type(string code, string schema, DocumentType? parent = null,
        DocumentTypeKind kind = DocumentTypeKind.Composite, TypeStorage storage = TypeStorage.SharedObject) =>
        DocumentType.Create(code, code, kind, parent?.Id, JsonDocument.Parse(schema), TypeOwner.Core,
            TypeVisibility.Shared, storage: storage);

    private static Dictionary<Guid, DocumentType> Types(params DocumentType[] types) => types.ToDictionary(t => t.Id);

    private const string Named = """{"fields":[{"key":"Наименование","type":"string"}]}""";

    private static string Field(string key, string type, DocumentType of) =>
        $$"""{"key":"{{key}}","type":"{{type}}","typeId":"{{of.Id}}"}""";

    private static readonly DocumentType Org = Type("Организация", Named);
    private static readonly DocumentType Work = Type("Работа", Named);

    private static readonly DocumentType Registry = Type("РеестрРабот",
        $$"""{"fields":[{{Field("Работы", "array", Work)}},{{Field("Заказчик", "complex", Org)}}]}""",
        kind: DocumentTypeKind.Document);

    private static IReadOnlyList<NestedSource> Nested(Guid type, IReadOnlyDictionary<Guid, DocumentType> types, string field = "Наименование") =>
        Assert.IsType<SuggestionPlan.Read>(SuggestionSources.For(type, field, types, TestAccess.All)).Nested;

    private static string[] Paths(IReadOnlyList<NestedSource> nested, Guid owner) =>
        nested.Where(n => n.OwnerTypeId == owner).SelectMany(n => n.Paths).Select(p => p.ToString()).Order().ToArray();

    [Fact]
    public void Тот_же_ключ_у_другого_составного_типа_даёт_другой_путь()
    {
        var types = Types(Org, Work, Registry);

        Assert.Equal(["Работы[*].Наименование"], Paths(Nested(Work.Id, types), Registry.Id));
        Assert.Equal(["Заказчик.Наименование"], Paths(Nested(Org.Id, types), Registry.Id));
    }

    [Fact]
    public void Ссылка_пути_не_порождает()
    {
        var links = Type("Ссылки",
            $$"""{"fields":[{{Field("Работа", "doc-ref", Work)}},{{Field("Работы", "doc-array", Work)}}]}""");

        Assert.Empty(Nested(Work.Id, Types(Work, links)));
    }

    [Fact]
    public void Глубина_шесть_читается_семь_отсекается()
    {
        var chain = new List<DocumentType> { Work };
        for (var i = 1; i <= SuggestionPaths.MaxDepth + 1; i++)
            chain.Add(Type($"Уровень{i}", $$"""{"fields":[{{Field($"Шаг{i}", i % 2 == 0 ? "array" : "complex", chain[^1])}}]}"""));
        var nested = Nested(Work.Id, Types([.. chain]));

        var six = Assert.Single(Paths(nested, chain[SuggestionPaths.MaxDepth].Id));
        Assert.Equal("Шаг6[*].Шаг5.Шаг4[*].Шаг3.Шаг2[*].Шаг1.Наименование", six);
        Assert.Empty(Paths(nested, chain[SuggestionPaths.MaxDepth + 1].Id));
    }

    [Fact]
    public void Потомок_владельца_наследует_путь_а_перекрывший_массив_нет()
    {
        var heir = Type("РеестрНаследник", """{"fields":[]}""", Registry, DocumentTypeKind.Document);
        var other = Type("Другое", """{"fields":[{"key":"Марка","type":"string"}]}""");
        var overridden = Type("РеестрСвой", $$"""{"fields":[{{Field("Работы", "array", other)}}]}""", Registry,
            DocumentTypeKind.Document);
        var nested = Nested(Work.Id, Types(Org, Work, Registry, heir, other, overridden));

        Assert.Equal(["Работы[*].Наименование"], Paths(nested, heir.Id));
        Assert.Empty(Paths(nested, overridden.Id));
    }

    [Fact]
    public void Владелец_по_правам_своего_вида_и_только_из_общей_таблицы()
    {
        var moduleOwned = Type("Счёт", $$"""{"fields":[{{Field("Работы", "array", Work)}}]}""",
            storage: TypeStorage.ModuleTable);
        var types = Types(Org, Work, Registry, moduleOwned);

        Assert.Equal([Registry.Id], Nested(Work.Id, types).Select(n => n.OwnerTypeId));
        // Строка массива живёт в документе: без права на документы — только верхний уровень типа строки.
        var catalog = Assert.IsType<SuggestionPlan.Read>(
            SuggestionSources.For(Work.Id, "Наименование", types, TestAccess.With(SuggestionSources.CatalogRead)));
        Assert.Equal([Work.Id], catalog.Types);
        Assert.Empty(catalog.Nested);
        // С правом на документы и без права на справочники — наоборот: источник есть, отказа нет.
        var documents = Assert.IsType<SuggestionPlan.Read>(
            SuggestionSources.For(Work.Id, "Наименование", types, TestAccess.With(SuggestionSources.DocumentRead)));
        Assert.Empty(documents.Types);
        Assert.Equal([Registry.Id], documents.Nested.Select(n => n.OwnerTypeId));
        // Без обоих — отказ: закрытый владелец считается закрытым источником, а не пустотой.
        Assert.IsType<SuggestionPlan.Forbidden>(
            SuggestionSources.For(Work.Id, "Наименование", types, TestAccess.With(SuggestionSources.NomenclatureEdit)));
    }

    /// <summary>
    /// Тип, вложенный в себя тремя полями, к глубине 6 дал бы больше тысячи путей. Набирается не больше
    /// предела, и мелкие — первыми: глубокие срезаются, а не первый уровень.
    /// </summary>
    [Fact]
    public void Путей_не_больше_предела_и_мелкие_первыми()
    {
        var tree = DocumentType.Create("Дерево", "Дерево", DocumentTypeKind.Composite, null, JsonDocument.Parse("{}"),
            TypeOwner.Core, TypeVisibility.Shared);
        tree.UpdateSchema(JsonDocument.Parse($$"""
            {"fields":[{"key":"Наименование","type":"string"},
              {{Field("Дети", "array", tree)}},{{Field("Родитель", "complex", tree)}},{{Field("Аналог", "complex", tree)}}]}
            """));
        var paths = Paths(Nested(tree.Id, Types(tree)), tree.Id);

        Assert.Equal(SuggestionPaths.MaxPaths, paths.Length);
        // Первые два уровня — целиком (3 + 9), третий — сколько влезло; четвёртого нет.
        Assert.Subset(paths.ToHashSet(), new HashSet<string> { "Дети[*].Наименование", "Родитель.Наименование", "Аналог.Наименование" });
        Assert.Equal(12, paths.Count(p => p.Count(c => c == '.') <= 2));
        Assert.DoesNotContain(paths, p => p.Count(c => c == '.') > 3);
    }

    [Fact]
    public void Путь_jsonpath_по_ключам_схемы_со_счётчиком_последнего_массива()
    {
        var path = new SuggestionPath([new("Разделы", true), new("Адрес \"юр\"", false), new("Работы", true)], "Наименование");

        // Сырая строка кавычкой кончаться не может — последняя дописана.
        Assert.Equal("""$."Разделы"[*] ? (!exists(@."$ref"))."Адрес \"юр\"" ? (!exists(@."$ref"))."Работы"[*] ? (!exists(@."$ref") && @."Наименование".type() == "string")."Наименование""" + "\"",
            FieldSuggestionReader.JsonPath(path));
        Assert.Equal("""$."Разделы"[*] ? (!exists(@."$ref"))."Адрес \"юр\"" ? (!exists(@."$ref"))."Работы".size()""",
            FieldSuggestionReader.SizePath(path));
        Assert.Null(FieldSuggestionReader.SizePath(new SuggestionPath([new("Адрес", false)], "Регион")));
    }
}
