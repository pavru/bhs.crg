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
}
