using System.Text.Json;
using System.Text.RegularExpressions;

namespace BHS.CRG.Application.Settings;

/// <summary>
/// Форма значения настройки «Закреплённые страницы» (ТЗ AUTH-16.4, work-04).
///
/// <para>Закрепление — адрес страницы навигации (<c>NavItem.to</c> клиента), а не код: второго реестра
/// страниц ради закреплений нет. Сервер список хранит, но не читает — ни меню, ни доступ по нему не
/// строятся: меню собирает клиент по ответу <c>/api/account/access</c>.</para>
///
/// <para>Чего здесь НЕТ нарочно: сверки адресов с навигацией. Навигацию знает клиент, и страница,
/// убранная новой версией, обязана остаться в списке с пометкой «страницы больше нет», а не пропасть
/// молча (AUTH-16.4). По той же причине не сверяется доступ: право вернут — закрепление на месте.</para>
/// </summary>
public static partial class PinnedSectionValues
{
    /// <summary>Предел закреплений — как у «Избранных строек» (решение владельца, #1217).</summary>
    public const int MaxPinned = 20;

    /// <summary>Предел длины одного адреса.</summary>
    public const int MaxPathLength = 200;

    /// <summary>
    /// Предел длины значения: двадцать адресов в кавычках, запятые и скобки — с запасом на
    /// экранирование JSON.
    /// </summary>
    public const int MaxLength = 4_200;

    /// <summary>
    /// Список закреплений: JSON-массив строк, от одного до двадцати, без повторов, в порядке человека.
    /// Пустой список не хранится — последнее закрепление снимают удалением ключа, как избранное:
    /// пустой массив рядом с отсутствием ключа дал бы два способа сказать одно и то же.
    /// </summary>
    public static bool IsList(string value)
    {
        try
        {
            using var json = JsonDocument.Parse(value);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return false;
            var count = json.RootElement.GetArrayLength();
            if (count is 0 or > MaxPinned) return false;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return false;
                var path = item.GetString()!;
                if (path.Length > MaxPathLength || !PathShape().IsMatch(path) || !seen.Add(path)) return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Путь от корня приложения: начинается с одной <c>/</c>, без схемы и хоста, без строки запроса и
    /// якоря, без пробелов и обратной черты. <c>//host</c> и <c>/\host</c> браузер читает как адрес
    /// ДРУГОГО сайта — закрепление стало бы ссылкой наружу. Строку запроса не храним: состояние таблицы
    /// живёт в сохранённых представлениях (CORE-33), а закрепляется страница.
    /// </summary>
    [GeneratedRegex(@"^/(?![/\\])[^\s?#\\]*$")]
    private static partial Regex PathShape();
}
