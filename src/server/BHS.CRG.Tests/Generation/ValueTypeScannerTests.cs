using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Соответствие значений объявленному типу (issue #461). Повод — реальное поле «ПорядковыйНомер»:
/// объявлено примитивом «Цело число», а хранит иерархическую нумерацию «2.1», «3.4», «10».
/// Проверка молчала, и документ выпускался как ни в чём не бывало.
/// </summary>
public class ValueTypeScannerTests
{
    private static readonly Guid IntegerTypeId = Guid.NewGuid();
    private static readonly Guid WorkTypeId = Guid.NewGuid();

    private static PrimitiveType Integer() => Make<PrimitiveType>(
        ("Name", "Цело число"), ("Code", "Integer"), ("BaseType", "number"),
        ("Constraints", JsonDocument.Parse("""{"integer":true}""")), ("Id", IntegerTypeId));

    private static T Make<T>(params (string Name, object Value)[] props) where T : class
    {
        var o = (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
        foreach (var (name, value) in props)
        {
            var p = typeof(T).GetProperty(name)
                ?? throw new InvalidOperationException($"нет свойства {name}");
            p.GetSetMethod(nonPublic: true)!.Invoke(o, [value]);
        }
        return o;
    }

    private static GenerationContext Context(string json)
    {
        var ctx = GenerationContext.FromJson(JsonDocument.Parse(json), JsonDocument.Parse("{}"));
        return ctx;
    }

    private static List<ResolutionDiagnostic> ScanTopLevel(string json, params SchemaFieldInfo[] fields)
    {
        var diagnostics = new List<ResolutionDiagnostic>();
        ValueTypeScanner.Scan(Context(json), fields,
            new Dictionary<Guid, DocumentType>(),
            new Dictionary<Guid, PrimitiveType> { [IntegerTypeId] = Integer() },
            diagnostics);
        return diagnostics;
    }

    private static SchemaFieldInfo IntegerField(string key = "Номер") =>
        new(key, "primitive", IntegerTypeId, "Порядковый номер");

    [Fact]
    public void StringWhereNumberDeclared_IsReported()
    {
        var d = Assert.Single(ScanTopLevel("""{"Номер":"2.1"}""", IntegerField()));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("value-type", d.Code);
        Assert.Contains("ожидается", d.Message);
        Assert.Contains("хранится строка", d.Message);
    }

    /// <summary>Тот самый случай: базовый тип сошёлся, а ограничение «только целые» — нет.</summary>
    [Fact]
    public void FractionalWhereIntegerDeclared_IsReported()
    {
        var d = Assert.Single(ScanTopLevel("""{"Номер":3.3}""", IntegerField()));
        Assert.Contains("только целые", d.Message);
    }

    [Fact]
    public void ConformingValue_IsSilent()
        => Assert.Empty(ScanTopLevel("""{"Номер":10}""", IntegerField()));

    [Fact]
    public void MissingValue_IsSilent()
        => Assert.Empty(ScanTopLevel("""{}""", IntegerField()));

    [Fact]
    public void NullValue_IsSilent()
        => Assert.Empty(ScanTopLevel("""{"Номер":null}""", IntegerField()));

    /// <summary>Расчётное поле производное: претензия к его значению — претензия к выражению (#368).</summary>
    [Fact]
    public void ComputedField_IsSkipped()
        => Assert.Empty(ScanTopLevel("""{"Номер":"строка"}""",
            IntegerField() with { Computed = true, Expression = "1" }));

    [Fact]
    public void PlainNumberField_ChecksBaseType()
    {
        var d = Assert.Single(ScanTopLevel("""{"Кол":"пять"}""", new SchemaFieldInfo("Кол", "number", null, "Количество")));
        Assert.Contains("ожидается число", d.Message);
    }

    [Fact]
    public void TableFieldWithScalarValue_IsReported()
    {
        var d = Assert.Single(ScanTopLevel("""{"Работы":"текст"}""",
            new SchemaFieldInfo("Работы", "array", WorkTypeId, "Работы")));
        Assert.Contains("таблица", d.Message);
    }

    /// <summary>Ссылки и файлы проверяет резолвер ссылок — здесь их трогать нечем.</summary>
    [Theory]
    [InlineData("doc-ref")]
    [InlineData("file")]
    [InlineData("image")]
    public void ReferenceLikeFields_AreSkipped(string type)
        => Assert.Empty(ScanTopLevel("""{"П":123}""", new SchemaFieldInfo("П", type, null, "Поле")));

    /// <summary>Битый шаблон — ошибка настройки типа, а не данных: одна опечатка администратора не
    /// должна давать предупреждение на каждой строке.</summary>
    [Fact]
    public void BrokenPattern_DoesNotProduceNoise()
    {
        var typeId = Guid.NewGuid();
        var broken = Make<PrimitiveType>(
            ("Name", "Кривой"), ("Code", "Broken"), ("BaseType", "string"),
            ("Constraints", JsonDocument.Parse("""{"pattern":"([unclosed"}""")), ("Id", typeId));

        var diagnostics = new List<ResolutionDiagnostic>();
        ValueTypeScanner.Scan(Context("""{"П":"что угодно"}"""),
            [new SchemaFieldInfo("П", "primitive", typeId, "Поле")],
            new Dictionary<Guid, DocumentType>(),
            new Dictionary<Guid, PrimitiveType> { [typeId] = broken },
            diagnostics);

        Assert.Empty(diagnostics);
    }

    // ── Арность union'а (issue #756) ──────────────────────────────────────────
    //
    // Записать такое приложение не даёт, но PUT …/requisites кладёт тело как есть, и строка с двумя
    // ключами приезжает из восстановленной копии или правки JSONB руками. Проверка нужна потому, что
    // ЕДИНСТВЕННЫЙ путь через такие данные в редакторе — потеря части из них: открыв строку, он
    // покажет первый заполненный вариант, а первая же правка выбросит остальные.

    private static readonly Guid UnionTypeId = Guid.NewGuid();
    private static readonly Guid UnionChildId = Guid.NewGuid();

    private static DocumentType Union(Guid id, string name, string schema, Guid? parentId = null) =>
        Make<DocumentType>(("Id", id), ("Name", name), ("ParentId", parentId),
            ("Schema", JsonDocument.Parse(schema.Replace('\'', '"'))));

    private static List<ResolutionDiagnostic> ScanUnion(string json, params DocumentType[] types)
    {
        var diagnostics = new List<ResolutionDiagnostic>();
        ValueTypeScanner.Scan(Context(json),
            [new SchemaFieldInfo("Документ", "complex", UnionTypeId, "Документ")],
            types.ToDictionary(t => t.Id),
            new Dictionary<Guid, PrimitiveType>(),
            diagnostics);
        return diagnostics;
    }

    private const string UnionSchema =
        "{'tags':['type.union'],'fields':["
        + "{'key':'Проект','type':'doc-ref','title':'Проект'},"
        + "{'key':'Реестр','type':'doc-ref','title':'Реестр'}]}";

    [Fact]
    public void UnionWithTwoFilledVariants_IsReported()
    {
        var d = Assert.Single(ScanUnion(
            """{"Документ":{"Проект":{"$ref":"catalog","entryId":"a"},"Реестр":{"$ref":"catalog","entryId":"b"}}}""",
            Union(UnionTypeId, "Документ произвольный", UnionSchema)));

        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("union-arity", d.Code);
        Assert.Equal("Документ", d.Path);
        Assert.Contains("заполнено 2", d.Message);
        Assert.Contains("«Проект»", d.Message);
        Assert.Contains("«Реестр»", d.Message);
    }

    [Fact]
    public void UnionWithOneFilledVariant_IsSilent()
        => Assert.Empty(ScanUnion("""{"Документ":{"Проект":{"$ref":"catalog","entryId":"a"}}}""",
            Union(UnionTypeId, "Документ произвольный", UnionSchema)));

    /// <summary>Снятая ссылка — это <c>{}</c>. Считай мы её вторым вариантом, предупреждение сыпалось
    /// бы на здоровых данных: пустой объект остаётся в реквизитах после «Снять ссылку».</summary>
    [Fact]
    public void EmptyObjectVariant_DoesNotCount()
        => Assert.Empty(ScanUnion("""{"Документ":{"Проект":{"$ref":"catalog","entryId":"a"},"Реестр":{}}}""",
            Union(UnionTypeId, "Документ произвольный", UnionSchema)));

    /// <summary>Тэг union'а наследуется: подтип объявляет только свои варианты (#747).</summary>
    [Fact]
    public void InheritedUnionTag_IsHonoured()
    {
        // Тэг — на предке, варианты — у потомка; поле объявлено ПОТОМКОМ.
        var types = new Dictionary<Guid, DocumentType>
        {
            [UnionTypeId] = Union(UnionTypeId, "База", "{'tags':['type.union'],'fields':[]}"),
            [UnionChildId] = Union(UnionChildId, "Потомок", UnionSchema, UnionTypeId),
        };
        var diagnostics = new List<ResolutionDiagnostic>();
        ValueTypeScanner.Scan(Context("""{"Документ":{"Проект":{"x":1},"Реестр":{"y":2}}}"""),
            [new SchemaFieldInfo("Документ", "complex", UnionChildId, "Документ")],
            types, new Dictionary<Guid, PrimitiveType>(), diagnostics);

        var d = Assert.Single(diagnostics);
        Assert.Equal("union-arity", d.Code);
        Assert.Contains("заполнено 2", d.Message);
    }

    /// <summary>Обычному составному типу арность не предъявляем — там «и», а не «одно из».</summary>
    [Fact]
    public void NonUnionComposite_IsSilent()
        => Assert.Empty(ScanUnion("""{"Документ":{"Проект":{"x":1},"Реестр":{"y":2}}}""",
            Union(UnionTypeId, "Обычный", UnionSchema.Replace("'tags':['type.union'],", ""))));

    /// <summary>Строки таблицы проверяются каждая: union чаще всего стоит элементом массива.</summary>
    [Fact]
    public void UnionInsideArrayRow_IsReportedWithRowPath()
    {
        var diagnostics = new List<ResolutionDiagnostic>();
        ValueTypeScanner.Scan(
            Context("""{"Документы":[{"Проект":{"a":1}},{"Проект":{"a":1},"Реестр":{"b":2}}]}"""),
            [new SchemaFieldInfo("Документы", "array", UnionTypeId, "Документы")],
            new Dictionary<Guid, DocumentType> { [UnionTypeId] = Union(UnionTypeId, "Документ", UnionSchema) },
            new Dictionary<Guid, PrimitiveType>(),
            diagnostics);

        var d = Assert.Single(diagnostics);
        Assert.Equal("Документы[1]", d.Path);
    }
}
