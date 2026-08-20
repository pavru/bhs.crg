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
        => IsOllama(name)
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

    /// <summary>
    /// Беда с ВЫБРАННОЙ моделью: движок настроен, но поставщик такой модели не знает (issue #799).
    /// <c>null</c> — претензий нет.
    ///
    /// Отдельно от <see cref="MissingForRecognition" /> намеренно. Ненастроенный движок цепочка
    /// пропускает, а этот — берёт и получает от него отказ; сказать про него «не участвует» значило бы
    /// описать не то, что происходит. Пользователю разница видна: в первом случае документ уходит
    /// следующему движку сразу, во втором — после неудачного запроса.
    ///
    /// <see cref="ModelState.Unknown" /> — молчание: объявить модель несуществующей из-за того, что
    /// Ollama не запущена или на счёте кончились деньги, — худшая из подсказок.
    /// </summary>
    public static string? ModelIssue(string name, IntegrationEngine e, ModelStatus status)
    {
        if (status.State != ModelState.Gone || Blank(e.Model) || MissingForRecognition(name, e) is not null)
            return null;
        var head = IsOllama(name)
            ? $"модель «{e.Model}» не скачана"
            : $"модель «{e.Model}» больше не обслуживается";
        return status.Advice is null ? head : $"{head} — {status.Advice}";
    }

    /// <summary>
    /// Одна ли это модель. Для Ollama имя без тега равно имени с тегом <c>latest</c> — так их пишет и
    /// сама Ollama, и человек в поле настроек, и считать «qwen3-vl» отсутствующей при установленной
    /// «qwen3-vl:latest» было бы придиркой к записи.
    /// </summary>
    public static bool SameModel(string a, string b)
        => Tagged(a).Equals(Tagged(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Движок включён и настроен — цепочка его возьмёт.</summary>
    public static bool IsUsableForRecognition(string name, IntegrationEngine e)
        => e.Enabled && MissingForRecognition(name, e) is null;

    /// <inheritdoc cref="IsUsableForRecognition" />
    public static bool IsUsableForWebSearch(string name, IntegrationEngine e)
        => e.Enabled && MissingForWebSearch(name, e) is null;

    private static string Tagged(string m)
    {
        var s = m.Trim();
        return s.Contains(':') ? s : s + ":latest";
    }

    private static bool IsOllama(string name) => name.Equals("Ollama", StringComparison.OrdinalIgnoreCase);

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}
