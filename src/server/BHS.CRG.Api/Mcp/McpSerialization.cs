using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ModelContextProtocol;

namespace BHS.CRG.Api.Mcp;

/// <summary>
/// Кодировка ответов MCP (issue #576).
///
/// Кодировщик System.Text.Json по умолчанию экранирует всё не-ASCII: каждая кириллическая буква
/// становится <c>П</c> — шесть символов вместо одного. На живом вызове
/// <c>list_quality_documents</c> это дало ответ в 139 446 символов, из которых 123 210 (88 %) были
/// экранированием при 37 тысячах символов настоящих данных, — и клиент отказался его принимать.
/// Домен здесь русскоязычный целиком, поэтому налог платят ВСЕ выдачи, а больнее всего страничные:
/// <c>get_rows</c> упирался в лимит вчетверо раньше, чем позволяет его собственный потолок строк.
///
/// <see cref="UnicodeRanges.All"/>, а не <c>UnsafeRelaxedJsonEscaping</c>: кириллица проходит как
/// есть, но <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> по-прежнему экранируются. Ответ уходит в JSON-RPC,
/// а не в HTML, однако что клиент сделает с текстом дальше — не наше знание, и терять эту защиту
/// ради байтов, которых она почти не стоит, незачем.
///
/// Базы намеренно РАЗНЫЕ и совпадают с тем, что было до правки: у инструментов — настройки самого
/// SDK (там живут конвертеры протокола), у ресурсов — веб-умолчания. Задача была починить кодировку,
/// а не заодно переписать форму ответов: смена базы поменяла бы, например, вид перечислений.
/// </summary>
public static class McpSerialization
{
    /// <summary>Для значений, возвращаемых инструментами: сериализует их SDK.</summary>
    public static readonly JsonSerializerOptions ToolOptions =
        new(McpJsonUtilities.DefaultOptions) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    /// <summary>Для содержимого ресурсов: его сериализуем мы сами, см. <see cref="McpJsonResource"/>.</summary>
    public static readonly JsonSerializerOptions ResourceOptions =
        new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
}
