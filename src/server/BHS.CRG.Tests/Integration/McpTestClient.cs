using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Разговор с MCP по HTTP — так же, как это делает любой клиент: JSON-RPC поверх <c>/mcp</c>.
///
/// Вынесено из <see cref="McpHttpSessionTests" /> вместе с разбором SSE (issue #948): вторым
/// потребителем стали ворота инструментов, а две копии разбора ответа разошлись бы — и разошлись бы
/// молча, потому что каждая копия продолжала бы проходить свои тесты.
///
/// ⚠️ Ходить по HTTP здесь ОБЯЗАТЕЛЬНО. Ворота инструментов ставятся фильтрами транспорта и
/// спрашивают о правах владельца токена; вызов метода инструмента напрямую их не проходит вовсе —
/// и «зелено» означало бы лишь, что метод работает.
/// </summary>
internal static class McpTestClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Рукопожатие. В stateless-режиме необязательно, но так делает настоящий клиент.</summary>
    public static object Initialize() => new
    {
        jsonrpc = "2.0",
        id = 1,
        method = "initialize",
        @params = new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "test", version = "1.0.0" },
        },
    };

    public static Task<HttpResponseMessage> SendAsync(HttpClient client, object message)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(message, Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return client.SendAsync(request);
    }

    /// <summary>Ответ приходит либо чистым JSON, либо потоком SSE — как и у любого клиента.</summary>
    public static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var payload = (response.Content.Headers.ContentType?.MediaType ?? "").Contains("event-stream")
            ? string.Concat(body.Split('\n')
                .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                .Select(l => l[5..].Trim()))
            : body;

        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.Clone();
    }

    /// <summary>Успешный ответ; отказ роняет тест с текстом отказа, а не с «нет свойства result».</summary>
    public static async Task<JsonElement> ResultOfAsync(HttpResponseMessage response)
    {
        var root = await BodyAsync(response);
        Assert.False(root.TryGetProperty("error", out var error),
            error.ValueKind == JsonValueKind.Undefined ? "" : error.ToString());
        return root.GetProperty("result");
    }

    /// <summary>Один вызов метода: отправка, разбор, результат.</summary>
    public static async Task<JsonElement> CallAsync(
        HttpClient client, string method, object? parameters = null, int id = 2)
        => await ResultOfAsync(await SendAsync(client, Message(method, parameters, id)));

    /// <summary>То же, но без требования успеха: нужен разбор отказа.</summary>
    public static async Task<JsonElement> TryCallAsync(
        HttpClient client, string method, object? parameters = null, int id = 2)
        => await BodyAsync(await SendAsync(client, Message(method, parameters, id)));

    private static object Message(string method, object? parameters, int id) => parameters is null
        ? new { jsonrpc = "2.0", id, method }
        : new { jsonrpc = "2.0", id, method, @params = parameters };

    /// <summary>Имена инструментов из ответа <c>tools/list</c>.</summary>
    public static IReadOnlyList<string> NamesOf(JsonElement result, string property) =>
        [.. result.GetProperty(property).EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)];
}
