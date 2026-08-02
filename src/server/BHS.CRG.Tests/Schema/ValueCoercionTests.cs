using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Schema;

/// <summary>
/// Приведение значения к объявленному типу — исправление аудита «привести» (issue #643).
///
/// Повод — четыре записи «Внешний документ» на живой базе: «Количество листов» объявлено «Цело
/// число», а хранит строку «1». Удаление такое расхождение не чинит, а теряет.
/// </summary>
public class ValueCoercionTests
{
    private static readonly Guid IntId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid AnyNumId = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    private static readonly Guid DateId = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

    private static IReadOnlyDictionary<Guid, PrimitiveType> Prims() => new Dictionary<Guid, PrimitiveType>
    {
        [IntId] = P(IntId, "Цело число", "number", "{\"integer\":true}"),
        [AnyNumId] = P(AnyNumId, "Число", "number", "{}"),
        [DateId] = P(DateId, "Дата", "date", "{}"),
    };

    private static PrimitiveType P(Guid id, string name, string baseType, string constraints) =>
        PrimitiveType.Restore(id, name, name, baseType, null, JsonDocument.Parse(constraints),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static SchemaFieldInfo F(string type, Guid? typeId = null) => new("Поле", type, typeId, "Поле");

    private static bool Coerce(SchemaFieldInfo f, JsonNode? v, out JsonNode? result, out string? reason)
        => ValueCoercion.TryCoerce(f, v, Prims(), out result, out reason);

    [Fact]
    public void Coerce_StringToInteger()
    {
        Assert.True(Coerce(F("primitive", IntId), JsonValue.Create("1"), out var r, out _));
        Assert.Equal("1", r!.ToJsonString()); // именно 1, а не 1.0 — счёт идёт штуками
    }

    [Fact]
    public void Coerce_RussianDecimalComma()
    {
        Assert.True(Coerce(F("primitive", AnyNumId), JsonValue.Create("12,5"), out var r, out _));
        Assert.Equal(12.5, r!.GetValue<double>());
    }

    [Fact]
    public void Coerce_ThousandSpaces()
    {
        Assert.True(Coerce(F("number"), JsonValue.Create("1 234,5"), out var r, out _));
        Assert.Equal(1234.5, r!.GetValue<double>());
    }

    [Fact]
    public void Coerce_RefusesTrailingUnits()
    {
        // Отличие от разбора ячейки набора (QuantityParser), где «10 м» законно читается как 10:
        // здесь правится СОХРАНЁННОЕ значение, и выбросить «м» значит потерять написанное человеком.
        Assert.False(Coerce(F("number"), JsonValue.Create("10 м"), out _, out var reason));
        Assert.Contains("не разбирается как число", reason);
    }

    [Fact]
    public void Coerce_RefusesFractionInIntegerType()
    {
        // Ровно случай #461: «2.1» — иерархическая нумерация, а не опечатка формата. И округление, и
        // отбрасывание дробной части выдумали бы данные.
        Assert.False(Coerce(F("primitive", IntId), JsonValue.Create("2.1"), out _, out var reason));
        Assert.Contains("округление придумало бы данные", reason);
    }

    [Fact]
    public void Coerce_FractionAllowedWhenTypeAllows()
    {
        Assert.True(Coerce(F("primitive", AnyNumId), JsonValue.Create("2.1"), out var r, out _));
        Assert.Equal(2.1, r!.GetValue<double>());
    }

    [Fact]
    public void Coerce_RussianDateToIso()
    {
        Assert.True(Coerce(F("primitive", DateId), JsonValue.Create("01.02.2026"), out var r, out _));
        Assert.Equal("2026-02-01", r!.GetValue<string>()); // инвариантная культура прочла бы 2 января
    }

    [Fact]
    public void Coerce_BooleanFromRussianYes()
    {
        Assert.True(Coerce(F("boolean"), JsonValue.Create("да"), out var r, out _));
        Assert.True(r!.GetValue<bool>());
    }

    [Fact]
    public void Coerce_NumberToStringField()
    {
        Assert.True(Coerce(F("string"), JsonValue.Create(42), out var r, out _));
        Assert.Equal("42", r!.GetValue<string>());
    }

    [Fact]
    public void Coerce_AlreadyCorrect_IsNotAChange()
    {
        Assert.False(Coerce(F("string"), JsonValue.Create("уже строка"), out _, out var reason));
        Assert.Equal("Значение уже нужного вида.", reason);
    }

    [Fact]
    public void Coerce_RefusesCompositeValue()
    {
        Assert.False(Coerce(F("number"), JsonNode.Parse("{\"a\":1}"), out _, out var reason));
        Assert.Contains("вручную", reason);
    }

    [Fact]
    public void Coerce_UnknownPrimitive_Refuses()
    {
        Assert.False(Coerce(F("primitive", Guid.NewGuid()), JsonValue.Create("1"), out _, out var reason));
        Assert.Contains("не определено", reason);
    }
}

/// <summary>Поле схемы по пути аудита (issue #643) — обратная сторона JsonPathEditor.</summary>
public class SchemaPathResolverTests
{
    private static readonly Guid DocId = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid RowId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    private static DocumentType T(Guid id, string fieldsJson) =>
        DocumentType.Restore(id, id.ToString(), id.ToString(), DocumentTypeKind.Composite, null,
            JsonDocument.Parse($"{{\"fields\":{fieldsJson}}}"), JsonDocument.Parse("{}"), false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static IReadOnlyDictionary<Guid, DocumentType> Types() => new[]
    {
        T(DocId, $"[{{\"key\":\"Работы\",\"type\":\"array\",\"typeId\":\"{RowId}\"}},{{\"key\":\"Номер\",\"type\":\"string\"}}]"),
        T(RowId, "[{\"key\":\"Порядок\",\"type\":\"number\"}]"),
    }.ToDictionary(t => t.Id);

    [Fact]
    public void FieldAt_TopLevel() =>
        Assert.Equal("Номер", SchemaPathResolver.FieldAt("Номер", DocId, Types())!.Key);

    [Fact]
    public void FieldAt_InsideArrayRow()
    {
        // Индекс строки схему не меняет — у всех строк таблицы тип один.
        var f = SchemaPathResolver.FieldAt("Работы[3].Порядок", DocId, Types());
        Assert.Equal("number", f!.Type);
    }

    [Fact]
    public void FieldAt_UnknownKey_IsNull() =>
        Assert.Null(SchemaPathResolver.FieldAt("Нет.Такого", DocId, Types()));

    [Fact]
    public void FieldAt_DeeperThanScalar_IsNull() =>
        Assert.Null(SchemaPathResolver.FieldAt("Номер.Внутри", DocId, Types()));
}
