using System.Text;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// Имя файла модуля блоков (issue #772): <c>typeblocks-&lt;слаг&gt;.typ</c>.
///
/// <para><b>Слаг, а не код типа напрямую.</b> Код — доменное значение, которое пользователь вводит
/// свободно, а имя файла обязано пережить файловую систему. Развязка избавляет раскол от целого
/// класса блокирующих валидаций: тип с «неудобным» кодом не теряет блоки и не роняет сборку, он
/// просто получает соседний файл. Полный код остаётся в провенанс-комментарии первой строкой файла,
/// поэтому адресность ошибки (<c>typeblocks-Организация.typ:12</c>) не страдает.</para>
///
/// <para><b>Регистр — главная ловушка, а не запрещённые символы.</b> Коды типов уникальны
/// регистрозависимо, а <c>Акт.typ</c> и <c>акт.typ</c> на Windows один файл, на Linux два: один тип
/// молча съел бы блоки другого, причём только на части платформ. Поэтому уникальность слагов
/// считается регистронезависимо, и столкнувшийся получает суффикс.</para>
/// </summary>
public static class TypeBlockSlug
{
    /// <summary>
    /// Префикс имени модуля. Модули лежат РЯДОМ с агрегатором, а не в подпапке <c>typeblocks/</c>, и
    /// это не вопрос вкуса: тексты блоков пишет пользователь, и в них живут относительные пути —
    /// <c>import "userlib.typ"</c> стоит во всех семнадцати наших модулях, а рядом бывают
    /// <c>image("assets/…")</c> и <c>systemlib.typ</c>. Подпапка меняла бы точку отсчёта для всех
    /// них разом: проверено живьём — шесть документов из девяти упали с «file not found
    /// …/typeblocks/userlib.typ». Переписывать чужие пути при сборке значило бы менять смысл
    /// написанного человеком по догадке; плоская раскладка не трогает ничего.
    /// </summary>
    public const string FilePrefix = "typeblocks-";

    /// <summary>Агрегатор — точка входа, которую импортирует шаблон.</summary>
    public const string EntrypointName = "typeblocks.typ";

    /// <summary>Имя файла модуля по слагу.</summary>
    public static string FileNameFor(string slug) => $"{FilePrefix}{slug}.typ";

    private const int MaxLength = 60;

    // Зарезервированные имена DOS живы в Win32 до сих пор: файл «NUL.typ» создать нельзя.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Слаги для набора типов, уникальные регистронезависимо. Порядок входа задаёт, кто получит
    /// «чистое» имя, а кто суффикс, — поэтому вызывающий обязан подавать типы в стабильном порядке
    /// (у нас — по коду), иначе файлы переименовывались бы между сборками.
    /// </summary>
    /// <param name="keys">Пары «ключ типа → предпочитаемое имя» (код типа, а при пустом — имя типа).</param>
    public static Dictionary<TKey, string> AssignUnique<TKey>(IEnumerable<(TKey Key, string Preferred)> keys)
        where TKey : notnull
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<TKey, string>();
        foreach (var (key, preferred) in keys)
        {
            var basis = Sanitize(preferred);
            var slug = basis;
            for (int n = 2; !used.Add(slug); n++) slug = Truncate(basis, MaxLength - 4) + "-" + n;
            result[key] = slug;
        }
        return result;
    }

    /// <summary>Один слаг без учёта соседей — для тестов и одиночных вызовов.</summary>
    public static string Sanitize(string? preferred)
    {
        var sb = new StringBuilder();
        foreach (var c in (preferred ?? "").Trim())
        {
            // Запрещённое Win32 + разделители путей + управляющие. Точка тоже: она отделяет
            // расширение, и «Акт.черновик» дал бы файл «Акт.черновик.typ» — Typst его импортирует,
            // но провенанс в имени становится нечитаемым, а обход папки — неоднозначным.
            if (c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '.' || char.IsControl(c))
                sb.Append('_');
            else if (char.IsWhiteSpace(c)) sb.Append('_');
            else sb.Append(c);
        }

        var s = Truncate(sb.ToString(), MaxLength).Trim('_');
        if (s.Length == 0) s = "type";
        if (ReservedNames.Contains(s)) s = "_" + s;
        return s;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
