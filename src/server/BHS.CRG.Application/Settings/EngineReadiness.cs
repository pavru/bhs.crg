namespace BHS.CRG.Application.Settings;

/// <summary>
/// Настроен ли движок настолько, чтобы им пользоваться, — и если нет, чего именно не хватает (issue #797).
///
/// Правило одно на всех, потому что раньше оно жило двумя копиями (<c>IsUsable</c> в цепочке
/// распознавания и в веб-поиске) и ни одна не была видна пользователю: движок с галкой «включён», но
/// без ключа, молча выпадал из перебора. В настройках он при этом выглядел работающим — узнать, что
/// он не участвует, было неоткуда. Третьей копией на клиенте эту дыру закрывать нельзя: разъехавшись,
/// бейдж начал бы обещать участие движку, которого сервер не берёт.
/// </summary>
public static class EngineReadiness
{
    /// <summary>Чего не хватает движку распознавания; <c>null</c> — настроен.</summary>
    public static string? MissingForRecognition(string name, IntegrationEngine e)
        // Ollama локальная и ключа не спрашивает — ей нужна выбранная модель.
        => name.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
            ? Blank(e.Model) ? "не выбрана модель" : null
            : Blank(e.ApiKey) ? "не задан ключ" : null;

    /// <summary>Чего не хватает движку веб-поиска; <c>null</c> — настроен.</summary>
    public static string? MissingForWebSearch(string name, IntegrationEngine e)
    {
        if (!name.Equals("Yandex", StringComparison.OrdinalIgnoreCase))
            return Blank(e.ApiKey) ? "не задан ключ" : null;
        // Яндексу нужны оба: ключ и каталог. Называем недостающее, а не «настроен неполностью».
        return (Blank(e.ApiKey), Blank(e.FolderId)) switch
        {
            (true, true) => "не заданы ключ и идентификатор каталога",
            (true, false) => "не задан ключ",
            (false, true) => "не задан идентификатор каталога",
            _ => null,
        };
    }

    /// <summary>Движок включён и настроен — цепочка его возьмёт.</summary>
    public static bool IsUsableForRecognition(string name, IntegrationEngine e)
        => e.Enabled && MissingForRecognition(name, e) is null;

    /// <inheritdoc cref="IsUsableForRecognition" />
    public static bool IsUsableForWebSearch(string name, IntegrationEngine e)
        => e.Enabled && MissingForWebSearch(name, e) is null;

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}
