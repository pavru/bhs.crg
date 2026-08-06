namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Узнаваемые начала значений из файлов-примеров (<c>appsettings.json</c>, <c>deploy/.env.example</c>).
///
/// Список общий для всех проверок конфигурации намеренно. Разведи их по guard-ам — и первая же
/// правка <c>.env.example</c> починит одну проверку и оставит вторую сверять исчезнувшую заглушку,
/// причём молча: тесты у каждой свои и обе останутся зелёными.
///
/// Сверяем по НАЧАЛУ строки: заглушку часто правят частично, оставляя узнаваемый хвост от примера
/// — такое значение ничем не лучше исходного.
/// </summary>
public static class ConfigPlaceholders
{
    /// <summary>
    /// Записи здесь обязаны быть УЗНАВАЕМЫМИ, а не просто похожими на секрет: обычное английское
    /// слово в этом списке отвергало бы настоящие значения с заведомо неверной причиной, и человек
    /// шёл бы искать своё значение в файлах-примерах, где его нет.
    /// </summary>
    private static readonly string[] Prefixes =
    [
        "CHANGE_ME",
        "CHANGEME",
    ];

    /// <summary>Похоже ли значение на невыправленную заглушку из файла-примера.</summary>
    public static bool LooksLikeExample(string value, params string[] extraPrefixes)
    {
        foreach (var prefix in Prefixes)
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var prefix in extraPrefixes)
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Заглушка ВНУТРИ составного значения. Строка подключения собирается развёртыванием из частей
    /// (<c>Password=${POSTGRES_PASSWORD}</c>), и невыправленный пароль оказывается в её середине —
    /// проверка по началу строки его не увидит.
    /// </summary>
    public static bool ContainsExample(string value)
    {
        foreach (var prefix in Prefixes)
            if (value.Contains(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
