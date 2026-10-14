using System.Text.Json;
using System.Text.RegularExpressions;

namespace BHS.CRG.Application.Settings;

/// <summary>
/// Форма значений настроек рабочих пространств (ТЗ AUTH-16.1, AUTH-16.5; issue #1001).
///
/// <para>Пространство — понятие клиента (AUTH-16.2): сервер его ХРАНИТ за человеком, но не читает —
/// ни один адрес не отбирает по нему данные. Проверка здесь — о форме, а не о смысле: значение
/// приходит снаружи, и сохранённая каша не прочиталась бы клиентом на другом компьютере, где
/// исправить её уже нечем.</para>
///
/// <para>Чего здесь НЕТ нарочно: сверки кодов модулей с включёнными. Пространство переживает
/// выключение модуля (AUTH-16.3): модуль включат обратно, а состав пропал бы.</para>
/// </summary>
public static partial class WorkspaceSettingValues
{
    public const int MaxWorkspaces = 50;
    public const int MaxNameLength = 60;
    public const int MaxModules = 20;

    /// <summary>
    /// Имя системного пространства «все доступные». Своё с таким именем дало бы в переключателе две
    /// одинаковые строки с разным составом (ревью PR #1330). Названия модулей сервер здесь не
    /// сверяет — их знает реестр модулей, а не настройки; клиент запирает их сам.
    /// </summary>
    public const string AllName = "Все доступные модули";

    /// <summary>
    /// Выбор пространства: «все доступные» (<c>all</c>), системное одного модуля (<c>module:costs</c>)
    /// или своё (<c>own:&lt;id&gt;</c>).
    /// </summary>
    public static readonly Regex Choice = ChoiceShape();

    /// <summary>
    /// Список своих: массив <c>{ id, name, modules }</c>. Имя непустое и единственное среди своих
    /// без учёта регистра и пробелов по краям (AUTH-16.5), модулей хотя бы один, без повторов.
    /// </summary>
    public static bool IsOwnList(string value)
    {
        try
        {
            using var json = JsonDocument.Parse(value);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return false;
            if (json.RootElement.GetArrayLength() > MaxWorkspaces) return false;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return false;
                if (!Text(item, "id", out var id) || !IdShape().IsMatch(id) || !ids.Add(id)) return false;
                if (!Text(item, "name", out var name)) return false;
                name = name.Trim();
                if (name.Length is 0 or > MaxNameLength || !names.Add(name)) return false;
                if (string.Equals(name, AllName, StringComparison.OrdinalIgnoreCase)) return false;

                if (!item.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array)
                    return false;
                var codes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var code in modules.EnumerateArray())
                    if (code.ValueKind != JsonValueKind.String || !ModuleShape().IsMatch(code.GetString()!)
                        || !codes.Add(code.GetString()!))
                        return false;
                if (codes.Count is 0 or > MaxModules) return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Text(JsonElement item, string name, out string value)
    {
        value = item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
        return value.Length > 0;
    }

    [GeneratedRegex("^(all|module:[a-z][a-z0-9_-]{0,31}|own:[A-Za-z0-9-]{1,40})$")]
    private static partial Regex ChoiceShape();

    [GeneratedRegex("^[A-Za-z0-9-]{1,40}$")]
    private static partial Regex IdShape();

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}$")]
    private static partial Regex ModuleShape();
}
