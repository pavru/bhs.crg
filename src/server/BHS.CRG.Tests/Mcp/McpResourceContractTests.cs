using BHS.CRG.Api.Mcp;
using ModelContextProtocol.Protocol;

namespace BHS.CRG.Tests.Mcp;

/// <summary>
/// SDK принимает от ресурсной функции только <see cref="ResourceContents"/>, строку или AIContent,
/// а произвольный POCO роняет ЧТЕНИЕ РЕСУРСА в рантайме («Unsupported result type») — компилятор
/// молчит, и наружу это видно лишь как -32603 у клиента. Так уже уехало чтение наборов данных (#415).
/// Поэтому контракт проверяем рефлексией, а не только живым вызовом.
/// </summary>
public class McpResourceContractTests
{
    /// <summary>Ресурсы — из общей описи (<see cref="McpInventory" />, issue #1337), а не списком
    /// классов: ресурс невписанного класса не проверялся бы вовсе. По имени ресурса, а не по классу:
    /// так проверяется ровно тот метод, который нашла опись, — какой бы он ни был видимости.</summary>
    public static TheoryData<string> ResourceNames =>
        new(McpInventory.Declared().Where(p => p.Kind == McpKind.Resource).Select(p => p.Name));

    [Fact]
    public void Resources_AreFound() => Assert.NotEmpty(ResourceNames);

    [Theory]
    [MemberData(nameof(ResourceNames))]
    public void ResourceMethod_ReturnsSupportedType(string name)
    {
        var m = McpInventory.Declared().Single(p => p.Kind == McpKind.Resource && p.Name == name).Method;

        var returned = m.ReturnType;
        // Разворачиваем Task<T>/ValueTask<T> — SDK смотрит на результат, а не на обёртку.
        if (returned.IsGenericType &&
            (returned.GetGenericTypeDefinition() == typeof(Task<>) ||
             returned.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            returned = returned.GetGenericArguments()[0];

        Assert.True(
            typeof(ResourceContents).IsAssignableFrom(returned) || returned == typeof(string),
            $"{m.DeclaringType?.Name}.{m.Name} возвращает {returned.Name}: SDK примет только " +
            $"ResourceContents или string — заверните через McpJsonResource.Json.");
    }

    /// <summary>Отсутствующий объект обязан быть ошибкой: пустой JSON выглядел бы как успешное чтение.</summary>
    [Fact]
    public void Json_OnNull_Throws()
        => Assert.Throws<ModelContextProtocol.McpException>(
            () => McpJsonResource.Json<string>("bhs://document/x", null));

    [Fact]
    public void Json_SerializesWithJsonMimeType()
    {
        var contents = McpJsonResource.Json("bhs://document/x", new { Name = "Акт", Count = 2 });
        var text = Assert.IsType<TextResourceContents>(contents);
        Assert.Equal("application/json", text.MimeType);
        Assert.Equal("bhs://document/x", text.Uri);
        // camelCase — та же конвенция, что у инструментов, иначе ресурс и инструмент дают разные ключи.
        // Кириллица идёт как есть: экранирование раздувало ответы вчетверо (#576, McpSerialization).
        Assert.Contains("\"name\":\"Акт\"", text.Text);
        Assert.Contains("\"count\":2", text.Text);
    }
}
