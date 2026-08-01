using System.Text.Json;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Приведение значения из ячейки к объявленному типу поля (issue #466). До него числовые поля
/// накапливали строки: на рабочем комплекте 246 предупреждений из 247 были именно этим.
/// </summary>
public class DataSetValueCoercionTests
{
    private static readonly Guid NumberTypeId = Guid.NewGuid();
    private static readonly Guid StringTypeId = Guid.NewGuid();

    private static readonly Dictionary<Guid, PrimitiveType> Primitives = new()
    {
        [NumberTypeId] = PrimitiveType.Create("Количество", "Qty", "number", null, JsonDocument.Parse("{}")),
        [StringTypeId] = PrimitiveType.Create("Иерархический номер", "Hier", "string", null, JsonDocument.Parse("{}")),
    };

    private static object? Coerce(object? value, SchemaFieldInfo? field)
        => DataSetValueCoercion.Coerce(value, field, Primitives);

    private static SchemaFieldInfo Field(string type, Guid? typeId = null)
        => new("Кол", type, typeId, "Количество");

    [Theory]
    [InlineData("125", 125d)]
    [InlineData("12,5", 12.5d)]          // русская запятая — та же, что читает сверка
    [InlineData("1 234,5", 1234.5d)]
    [InlineData("125,5 м", 125.5d)]      // единицы в той же ячейке
    public void NumericField_GetsNumber(string cell, double expected)
    {
        Assert.Equal(expected, Assert.IsType<double>(Coerce(cell, Field("number"))));
        Assert.Equal(expected, Assert.IsType<double>(Coerce(cell, Field("primitive", NumberTypeId))));
    }

    /// <summary>Молча подменить на ноль нельзя: ноль — заявленное количество, неразобранное —
    /// неизвестность. Расхождение остаётся видимым в проверке документа.</summary>
    [Theory]
    [InlineData("нет данных")]
    [InlineData("—")]
    public void Unparseable_StaysString(string cell)
        => Assert.Equal(cell, Assert.IsType<string>(Coerce(cell, Field("number"))));

    [Fact]
    public void StringField_IsUntouched()
        => Assert.Equal("3.10", Coerce("3.10", Field("primitive", StringTypeId)));

    [Fact]
    public void UnknownField_IsUntouched()
        => Assert.Equal("125", Coerce("125", null));

    /// <summary>Собранные объекты и ссылки уже имеют свою форму — приводить там нечего.</summary>
    [Fact]
    public void NonStringValue_IsUntouched()
    {
        var obj = new Dictionary<string, object?> { ["a"] = 1 };
        Assert.Same(obj, Coerce(obj, Field("number")));
    }

    /// <summary>
    /// Пустая ячейка в ЧИСЛОВОМ поле — отсутствие значения, а не значение неверного типа (#544).
    /// Возвращаем null: вызывающие пишут значение под `if (value is not null)`, поэтому ключа не
    /// будет вовсе. Прежде она оседала пустой строкой, и на протоколе измерений изоляции из 473
    /// числовых ячеек 238 давали предупреждение «ожидается число, а хранится строка» — за таким
    /// списком уже не разглядеть настоящую проблему.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyCell_InNumericField_BecomesNothing(string cell)
    {
        Assert.Null(Coerce(cell, Field("number")));
        Assert.Null(Coerce(cell, Field("primitive", NumberTypeId)));
    }

    /// <summary>А в строковом поле пустая ячейка законна — там ничего не меняем.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyCell_InTextField_IsUntouched(string cell)
    {
        Assert.Equal(cell, Coerce(cell, Field("string")));
        Assert.Equal(cell, Coerce(cell, Field("primitive", StringTypeId)));
    }

    private static DocumentType Composite(Guid id, string name, string fieldsJson)
    {
        var t = DocumentType.Create(name, name + id.ToString("N")[..6], DocumentTypeKind.Composite, null,
            JsonDocument.Parse(fieldsJson));
        typeof(DocumentType).GetProperty("Id")!.GetSetMethod(nonPublic: true)!.Invoke(t, [id]);
        return t;
    }

    /// <summary>
    /// Числа в собранном inline-объекте лежат ГЛУБЖЕ поля строки: на рабочих данных длины кабеля живут
    /// в «КабельнаяЛинияТрасса.КабельПроложено.ДлинаЛинии». Без обхода внутрь 95 значений так и
    /// остались бы строками — найдено живой проверкой, а не рассуждением.
    /// </summary>
    [Fact]
    public void NestedInlineObject_IsCoercedByItsOwnSchema()
    {
        var innerId = Guid.NewGuid();
        var outerId = Guid.NewGuid();

        var inner = Composite(innerId, "Кабель",
            $$"""{"fields":[{"key":"ДлинаЛинии","title":"Длина","type":"primitive","typeId":"{{NumberTypeId}}"}]}""");
        var outer = Composite(outerId, "Трасса",
            $$"""{"fields":[{"key":"КабельПроложено","title":"Проложено","type":"complex","typeId":"{{innerId}}"}]}""");

        var types = new Dictionary<Guid, DocumentType> { [innerId] = inner, [outerId] = outer };
        var value = new Dictionary<string, object?>
        {
            ["КабельПроложено"] = new Dictionary<string, object?> { ["ДлинаЛинии"] = "48,5" },
        };

        var result = (Dictionary<string, object?>)DataSetValueCoercion.Coerce(
            value, new SchemaFieldInfo("Трасса", "complex", outerId, "Трасса"), Primitives, types)!;

        var nested = (Dictionary<string, object?>)result["КабельПроложено"]!;
        Assert.Equal(48.5d, Assert.IsType<double>(nested["ДлинаЛинии"]));
    }

    /// <summary>
    /// В собранном inline-объекте пустая числовая ячейка тоже не оставляет ключа (#544). Ключ со
    /// значением none опаснее отсутствующего: `it.at("X", default: "") != ""` его пропустит, а
    /// следом сломается конкатенация.
    /// </summary>
    [Fact]
    public void EmptyCell_InNestedObject_LeavesNoKey()
    {
        var rowTypeId = Guid.NewGuid();
        var types = new Dictionary<Guid, DocumentType>
        {
            [rowTypeId] = Composite(rowTypeId, "Строка",
                """{"fields":[{"key":"Длина","type":"number"},{"key":"Марка","type":"string"}]}"""),
        };
        var obj = new Dictionary<string, object?> { ["Длина"] = "", ["Марка"] = "" };

        var result = DataSetValueCoercion.CoerceObject(obj, rowTypeId, Primitives, types);

        Assert.False(result.ContainsKey("Длина"));
        Assert.Equal("", result["Марка"]);
    }
}
