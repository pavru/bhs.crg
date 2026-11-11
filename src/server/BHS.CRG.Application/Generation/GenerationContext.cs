using System.Text.Json;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Полный контекст данных для рендеринга шаблона.
/// Собирается EntityResolver-ом из реквизитов + pluginData.
/// </summary>
public class GenerationContext
{
    private readonly Dictionary<string, object?> _data = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string key, object? value) => _data[key] = value;

    public IReadOnlyDictionary<string, object?> Data => _data;

    /// <summary>
    /// Копия контекста с одним заменённым ключом. Значения не копируются — их никто не меняет; копия
    /// нужна, чтобы запрос одного шаблона не видел <c>params</c> следующего (запросы держат контекст
    /// ссылкой и могут быть собраны все до компиляции первого).
    /// </summary>
    public GenerationContext With(string key, object? value)
    {
        var copy = new GenerationContext();
        foreach (var (k, v) in _data) copy._data[k] = v;
        copy._data[key] = value;
        return copy;
    }

    public static GenerationContext FromJson(JsonDocument requisites, JsonDocument pluginData)
    {
        var ctx = new GenerationContext();

        foreach (var prop in requisites.RootElement.EnumerateObject())
            ctx.Set(prop.Name, prop.Value.Clone());

        foreach (var prop in pluginData.RootElement.EnumerateObject())
            ctx.Set(prop.Name, prop.Value.Clone());

        return ctx;
    }
}
