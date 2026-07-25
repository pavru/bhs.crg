using System.Reflection;
using BHS.CRG.Api.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Tests.Mcp;

/// <summary>
/// SDK принимает от ресурсной функции только <see cref="ResourceContents"/>, строку или AIContent,
/// а произвольный POCO роняет ЧТЕНИЕ РЕСУРСА в рантайме («Unsupported result type») — компилятор
/// молчит, и наружу это видно лишь как -32603 у клиента. Так уже уехало чтение наборов данных (#415).
/// Поэтому контракт проверяем рефлексией, а не только живым вызовом.
/// </summary>
public class McpResourceContractTests
{
    public static TheoryData<Type> ResourceTypes =>
        new(typeof(DataSnapshotResources), typeof(DomainSnapshotResources));

    [Theory]
    [MemberData(nameof(ResourceTypes))]
    public void ResourceMethods_ReturnSupportedType(Type resourceType)
    {
        var methods = resourceType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<McpServerResourceAttribute>() is not null)
            .ToList();

        Assert.NotEmpty(methods);

        foreach (var m in methods)
        {
            var returned = m.ReturnType;
            // Разворачиваем Task<T>/ValueTask<T> — SDK смотрит на результат, а не на обёртку.
            if (returned.IsGenericType &&
                (returned.GetGenericTypeDefinition() == typeof(Task<>) ||
                 returned.GetGenericTypeDefinition() == typeof(ValueTask<>)))
                returned = returned.GetGenericArguments()[0];

            Assert.True(
                typeof(ResourceContents).IsAssignableFrom(returned) || returned == typeof(string),
                $"{resourceType.Name}.{m.Name} возвращает {returned.Name}: SDK примет только " +
                $"ResourceContents или string — заверните через McpJsonResource.Json.");
        }
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
        Assert.Contains("\"name\":\"\\u0410\\u043A\\u0442\"", text.Text);
        Assert.Contains("\"count\":2", text.Text);
    }
}
