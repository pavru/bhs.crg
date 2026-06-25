using System.Text.Json;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Полный контекст данных для рендеринга шаблона.
/// Собирается EntityResolver-ом из реквизитов + entityRefs + pluginData.
/// </summary>
public class GenerationContext
{
    private readonly Dictionary<string, object?> _data = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string key, object? value) => _data[key] = value;

    public IReadOnlyDictionary<string, object?> Data => _data;

    public static GenerationContext FromJson(JsonDocument requisites, JsonDocument entityRefs, JsonDocument pluginData)
    {
        var ctx = new GenerationContext();

        foreach (var prop in requisites.RootElement.EnumerateObject())
            ctx.Set(prop.Name, prop.Value.Clone());

        foreach (var prop in entityRefs.RootElement.EnumerateObject())
            ctx.Set(prop.Name, prop.Value.Clone());

        foreach (var prop in pluginData.RootElement.EnumerateObject())
            ctx.Set(prop.Name, prop.Value.Clone());

        return ctx;
    }
}
