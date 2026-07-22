using System.Text.Json.Nodes;
using BHS.CRG.Application.Schema;

namespace BHS.CRG.Tests.Schema;

/// <summary>Точечная правка JSON по пути аудита (issue #350): remove/rename, вложенность, элементы массива.</summary>
public class JsonPathEditorTests
{
    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void Remove_TopLevelKey()
    {
        var root = Obj("{\"A\":1,\"НовыеРаботы\":{\"x\":1}}");
        Assert.True(JsonPathEditor.Remove(root, "НовыеРаботы", out var old));
        Assert.False(root.ContainsKey("НовыеРаботы"));
        Assert.True(root.ContainsKey("A"));
        Assert.Contains("\"x\":1", old);
    }

    [Fact]
    public void Remove_NestedKeyInArrayItem()
    {
        var root = Obj("{\"Работы\":[{\"Наименование\":\"a\",\"Лишнее\":5}]}");
        Assert.True(JsonPathEditor.Remove(root, "Работы[0].Лишнее", out _));
        Assert.False(((JsonObject)root["Работы"]![0]!).ContainsKey("Лишнее"));
        Assert.True(((JsonObject)root["Работы"]![0]!).ContainsKey("Наименование"));
    }

    [Fact]
    public void Remove_MissingPath_ReturnsFalse()
    {
        var root = Obj("{\"A\":1}");
        Assert.False(JsonPathEditor.Remove(root, "Нет", out var old));
        Assert.Null(old);
    }

    [Fact]
    public void Rename_ToEmptyTarget_MovesValue()
    {
        var root = Obj("{\"НовыеРаботы\":{\"n\":1}}");
        Assert.True(JsonPathEditor.Rename(root, "НовыеРаботы", "Работы", out var old, out var reason));
        Assert.Null(reason);
        Assert.False(root.ContainsKey("НовыеРаботы"));
        Assert.True(root.ContainsKey("Работы"));
        Assert.Equal(1, (int)root["Работы"]!["n"]!);
        Assert.Contains("\"n\":1", old);
    }

    [Fact]
    public void Rename_ToFilledTarget_SkipsWithReason()
    {
        var root = Obj("{\"НовыеРаботы\":{\"n\":1},\"Работы\":{\"m\":2}}");
        Assert.False(JsonPathEditor.Rename(root, "НовыеРаботы", "Работы", out _, out var reason));
        Assert.Contains("уже заполнено", reason);
        Assert.True(root.ContainsKey("НовыеРаботы")); // источник не тронут
        Assert.Equal(2, (int)root["Работы"]!["m"]!); // цель не перезаписана
    }

    [Fact]
    public void Rename_EmptyObjectTarget_AllowsOverwrite()
    {
        var root = Obj("{\"Старое\":{\"n\":1},\"Новое\":{}}");
        Assert.True(JsonPathEditor.Rename(root, "Старое", "Новое", out _, out _));
        Assert.Equal(1, (int)root["Новое"]!["n"]!);
    }

    [Fact]
    public void Rename_MissingSource_ReturnsFalse()
    {
        var root = Obj("{\"A\":1}");
        Assert.False(JsonPathEditor.Rename(root, "Нет", "Цель", out _, out var reason));
        Assert.NotNull(reason);
    }
}
