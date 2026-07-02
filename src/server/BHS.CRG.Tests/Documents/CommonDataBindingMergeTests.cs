using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;

namespace BHS.CRG.Tests.Documents;

public class CommonDataBindingMergeTests
{
    private static JsonDocument Json(string json) => JsonDocument.Parse(json);

    private static BindingPreviewDto Scalar(Dictionary<string, string?> data) =>
        new(Guid.NewGuid(), "Источник", "Файл", "scalar", null, 1, data, null);

    private static BindingPreviewDto Tabular(string targetFieldKey, List<Dictionary<string, string?>> rows) =>
        new(Guid.NewGuid(), "Источник", "Файл", "tabular", targetFieldKey, rows.Count, rows, null);

    private static BindingPreviewDto Error() =>
        new(Guid.NewGuid(), "Источник", "Файл", "error", null, 0, new { }, "Источник недоступен");

    [Fact]
    public void Scalar_OverwritesMatchingKey()
    {
        var current = Json("""{"inn":"старое","name":"Не трогать"}""");
        var previews = new[] { Scalar(new Dictionary<string, string?> { ["inn"] = "новое" }) };

        var merged = CommonDataBindingMerge.Merge(current, previews);

        Assert.Equal("новое", merged.RootElement.GetProperty("inn").GetString());
        Assert.Equal("Не трогать", merged.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void Scalar_EmptyValue_DoesNotOverwriteExisting()
    {
        var current = Json("""{"inn":"ручное значение"}""");
        var previews = new[] { Scalar(new Dictionary<string, string?> { ["inn"] = "" }) };

        var merged = CommonDataBindingMerge.Merge(current, previews);

        Assert.Equal("ручное значение", merged.RootElement.GetProperty("inn").GetString());
    }

    [Fact]
    public void Tabular_WritesArrayIntoTargetFieldKey_EvenWhenEmpty()
    {
        var current = Json("""{"Чертежи":[{"old":true}]}""");
        var previews = new[] { Tabular("Чертежи", new List<Dictionary<string, string?>>()) };

        var merged = CommonDataBindingMerge.Merge(current, previews);

        Assert.Equal(JsonValueKind.Array, merged.RootElement.GetProperty("Чертежи").ValueKind);
        Assert.Equal(0, merged.RootElement.GetProperty("Чертежи").GetArrayLength());
    }

    [Fact]
    public void ErrorBinding_LeavesExistingValueUntouched()
    {
        var current = Json("""{"inn":"прежнее"}""");
        var previews = new[] { Error() };

        var merged = CommonDataBindingMerge.Merge(current, previews);

        Assert.Equal("прежнее", merged.RootElement.GetProperty("inn").GetString());
    }

    [Fact]
    public void MultipleBindings_MergeWithoutClobberingEachOther()
    {
        var current = Json("{}");
        var previews = new[]
        {
            Scalar(new Dictionary<string, string?> { ["inn"] = "123" }),
            Tabular("Листы", new List<Dictionary<string, string?>> { new() { ["НомерЛиста"] = "1" } }),
        };

        var merged = CommonDataBindingMerge.Merge(current, previews);

        Assert.Equal("123", merged.RootElement.GetProperty("inn").GetString());
        Assert.Equal(1, merged.RootElement.GetProperty("Листы").GetArrayLength());
    }
}
