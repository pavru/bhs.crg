using System.Text.Json;
using BHS.CRG.Application.Documents;

namespace BHS.CRG.Tests.Documents;

// issue #99: Merge принимает РЕЗОЛВНУТЫЕ значения (скаляр=строка, @@ref={$ref,entryId}), а не превью.
public class CommonDataBindingMergeTests
{
    private static JsonDocument Json(string json) => JsonDocument.Parse(json);
    private static Dictionary<string, object?> Resolved(params (string k, object? v)[] items) =>
        items.ToDictionary(x => x.k, x => x.v);

    [Fact]
    public void Scalar_OverwritesMatchingKey()
    {
        var current = Json("""{"inn":"старое","name":"Не трогать"}""");
        var merged = CommonDataBindingMerge.Merge(current, Resolved(("inn", "новое")));

        Assert.Equal("новое", merged.RootElement.GetProperty("inn").GetString());
        Assert.Equal("Не трогать", merged.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void Scalar_EmptyValue_DoesNotOverwriteExisting()
    {
        var current = Json("""{"inn":"ручное значение"}""");
        var merged = CommonDataBindingMerge.Merge(current, Resolved(("inn", "")));

        Assert.Equal("ручное значение", merged.RootElement.GetProperty("inn").GetString());
    }

    [Fact]
    public void RefValue_WritesRefObject_NotDisplayString()
    {
        // Ключевой инвариант #99: составное поле хранит {$ref:catalog, entryId}, а не «🔗 …».
        var id = Guid.NewGuid();
        var current = Json("{}");
        var refVal = new Dictionary<string, object?> { ["$ref"] = "catalog", ["entryId"] = id.ToString() };
        var merged = CommonDataBindingMerge.Merge(current, Resolved(("Орг", refVal)));

        var org = merged.RootElement.GetProperty("Орг");
        Assert.Equal(JsonValueKind.Object, org.ValueKind);
        Assert.Equal("catalog", org.GetProperty("$ref").GetString());
        Assert.Equal(id.ToString(), org.GetProperty("entryId").GetString());
    }

    [Fact]
    public void Tabular_WritesArrayIntoTargetFieldKey_EvenWhenEmpty()
    {
        var current = Json("""{"Чертежи":[{"old":true}]}""");
        var merged = CommonDataBindingMerge.Merge(current, Resolved(("Чертежи", new List<Dictionary<string, object?>>())));

        Assert.Equal(JsonValueKind.Array, merged.RootElement.GetProperty("Чертежи").ValueKind);
        Assert.Equal(0, merged.RootElement.GetProperty("Чертежи").GetArrayLength());
    }

    [Fact]
    public void AbsentKey_LeavesExistingValueUntouched()
    {
        // Нет матча / ошибка источника → ключа нет в резолве → прежнее значение не трогаем.
        var current = Json("""{"inn":"прежнее"}""");
        var merged = CommonDataBindingMerge.Merge(current, Resolved());

        Assert.Equal("прежнее", merged.RootElement.GetProperty("inn").GetString());
    }

    [Fact]
    public void FileValue_WritesNestedObjectNotString()
    {
        var current = Json("{}");
        var fileValue = new Dictionary<string, object?>
        {
            ["$type"] = "file", ["blobPath"] = "bhs-crg/x_report.pdf",
            ["fileName"] = "report.pdf", ["mimeType"] = "application/pdf", ["size"] = 123L,
        };
        var merged = CommonDataBindingMerge.Merge(current, Resolved(("Чертёж", fileValue)));

        var chertezh = merged.RootElement.GetProperty("Чертёж");
        Assert.Equal(JsonValueKind.Object, chertezh.ValueKind);
        Assert.Equal("report.pdf", chertezh.GetProperty("fileName").GetString());
        Assert.Equal(123, chertezh.GetProperty("size").GetInt64());
    }

    [Fact]
    public void MultipleValues_MergeWithoutClobberingEachOther()
    {
        var current = Json("{}");
        var merged = CommonDataBindingMerge.Merge(current, Resolved(
            ("inn", "123"),
            ("Листы", new List<Dictionary<string, object?>> { new() { ["НомерЛиста"] = "1" } })));

        Assert.Equal("123", merged.RootElement.GetProperty("inn").GetString());
        Assert.Equal(1, merged.RootElement.GetProperty("Листы").GetArrayLength());
    }
}
