using System.Text.Json;
using BHS.CRG.Api.Mcp;
using BHS.CRG.Application.DataSnapshots;

namespace BHS.CRG.Tests.Mcp;

/// <summary>
/// Кодировка ответов MCP (issue #576). Проверяем не «настройку», а её наблюдаемое следствие: домен
/// русскоязычный, и экранирование каждой буквы в \uXXXX раздувало ответ вчетверо — живой вызов
/// list_quality_documents на 37 тысячах символов данных весил 139 446 и клиентом не принимался.
/// </summary>
public class McpSerializationTests
{
    private record Sample(string Name);

    [Theory]
    [InlineData("Сертификат соответствия")]
    [InlineData("Кабель ВВГнг(А)-LS 3х2.5")]
    public void Cyrillic_IsNotEscaped(string value)
    {
        var json = JsonSerializer.Serialize(new Sample(value), McpSerialization.ToolOptions);

        Assert.Contains(value, json);
        Assert.DoesNotContain("\\u04", json);
    }

    /// <summary>
    /// Ровно то, ради чего взят UnicodeRanges.All вместо UnsafeRelaxedJsonEscaping: кириллица
    /// проходит, а угловые скобки и амперсанд — нет. Куда клиент денет текст дальше, мы не знаем.
    /// </summary>
    [Fact]
    public void HtmlSensitiveCharacters_StayEscaped()
    {
        var json = JsonSerializer.Serialize(new Sample("<b>АО «Ромашка» & Ко</b>"), McpSerialization.ToolOptions);

        Assert.DoesNotContain("<b>", json);
        Assert.Contains("\\u003C", json);
        Assert.Contains("\\u0026", json);
        Assert.Contains("АО «Ромашка»", json);
    }

    /// <summary>Правка чинила размер, а не форму: ключи остаются camelCase, как их видит агент.</summary>
    [Fact]
    public void PropertyNames_StayCamelCase()
    {
        var json = JsonSerializer.Serialize(
            new SnapshotPage<Sample>([new("Иванов")], 0, 25, 1, false), McpSerialization.ToolOptions);

        Assert.Contains("\"items\"", json);
        Assert.Contains("\"truncated\"", json);
    }

    [Fact]
    public void Ratio_OnCyrillicPayload_IsAboutFourfold()
    {
        var items = Enumerable.Range(0, 50)
            .Select(i => new Sample($"Сертификат соответствия № {i} на кабельную продукцию"))
            .ToArray();

        var escaped = JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var plain = JsonSerializer.Serialize(items, McpSerialization.ToolOptions);

        Assert.True(escaped.Length > plain.Length * 3,
            $"ожидали кратное сокращение, получили {escaped.Length} против {plain.Length}");
    }

    /// <summary>Ресурсы сериализуем сами (McpJsonResource) — на них правило распространяется тоже.</summary>
    [Fact]
    public void ResourceOptions_ShareTheRule()
    {
        var json = JsonSerializer.Serialize(new Sample("Ведомость материалов"), McpSerialization.ResourceOptions);

        Assert.Contains("Ведомость материалов", json);
        Assert.DoesNotContain("\\u04", json);
    }
}
