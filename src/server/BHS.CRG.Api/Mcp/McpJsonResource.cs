using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace BHS.CRG.Api.Mcp;

/// <summary>
/// Упаковка формы-снимка в ответ ресурса MCP.
///
/// SDK принимает от ресурсной функции только <see cref="ResourceContents"/>, строку или AIContent —
/// произвольный POCO падает с «Unsupported result type» уже в рантайме, без ошибки компиляции.
/// Поэтому сериализуем сами, единообразно для наборов данных (#415) и домена (#419).
/// </summary>
public static class McpJsonResource
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// JSON-содержимое ресурса, либо ошибка «не найдено»: отдать <c>null</c> текстом значило бы
    /// сообщить клиенту об успехе там, где объекта нет.
    /// </summary>
    public static ResourceContents Json<T>(string uri, T? value) where T : class
        => value is null
            ? throw new McpException($"Ресурс не найден: {uri}")
            : new TextResourceContents
            {
                Uri = uri,
                MimeType = "application/json",
                Text = JsonSerializer.Serialize(value, Options),
            };
}
