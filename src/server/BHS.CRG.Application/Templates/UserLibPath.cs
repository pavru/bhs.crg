using System.Globalization;

namespace BHS.CRG.Application.Templates;

/// <summary>
/// Путь файла внутри дерева библиотеки (issue #473) — относительный, от папки <c>userlib/</c>.
///
/// Путь приходит от пользователя и превращается в путь на диске при материализации во временную
/// папку генерации, поэтому проверка тут не про удобство, а про то, чтобы запись не ушла за пределы
/// <c>userlib/</c>. Логика чистая и лежит в Application — её проверяют тесты, а не живая генерация.
/// </summary>
public static class UserLibPath
{
    /// <summary>Расширение единственного допустимого вида файлов: библиотека — это Typst-код.</summary>
    public const string Extension = ".typ";

    /// <summary>Имя папки дерева во временной папке генерации и внутри отладочного бандла.</summary>
    public const string FolderName = "userlib";

    private const int MaxLength = 200;
    private const int MaxSegments = 10;

    // Windows не создаст файл с этими символами, а часть из них к тому же меняет смысл пути.
    private static readonly char[] Forbidden = ['<', '>', ':', '"', '|', '?', '*'];

    // Зарезервированные имена устройств Windows: файл «CON.typ» не создастся, причём молча и с
    // невнятной ошибкой ввода-вывода уже во время генерации.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Нормализованный путь либо причина отказа. Нормализация — только разделители и лишние пробелы
    /// по краям: менять сам путь молча нельзя, иначе пользователь сохранит одно, а импортировать
    /// придётся другое.
    /// </summary>
    public static bool TryNormalize(string? raw, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        var path = (raw ?? string.Empty).Replace('\\', '/').Trim();
        if (path.Length == 0) { error = "Путь пустой."; return false; }
        if (path.Length > MaxLength) { error = $"Путь длиннее {MaxLength} символов."; return false; }
        if (path.StartsWith('/')) { error = "Путь должен быть относительным — без ведущего «/»."; return false; }

        var segments = path.Split('/');
        if (segments.Length > MaxSegments) { error = $"Слишком глубокая вложенность (больше {MaxSegments} уровней)."; return false; }

        foreach (var segment in segments)
        {
            if (segment.Length == 0) { error = "Пустой сегмент пути (двойной «/» или «/» в конце)."; return false; }
            if (segment is "." or "..") { error = "Сегменты «.» и «..» запрещены."; return false; }
            if (segment != segment.Trim()) { error = $"Сегмент «{segment}» начинается или заканчивается пробелом."; return false; }
            if (segment.EndsWith('.')) { error = $"Сегмент «{segment}» заканчивается точкой."; return false; }
            if (segment.Any(char.IsControl)) { error = "Управляющие символы в пути запрещены."; return false; }
            if (segment.IndexOfAny(Forbidden) >= 0)
            {
                error = $"Сегмент «{segment}» содержит запрещённый символ (из {string.Join(' ', Forbidden)}).";
                return false;
            }
            var withoutExt = Path.GetFileNameWithoutExtension(segment);
            if (ReservedNames.Contains(withoutExt))
            {
                error = $"«{withoutExt}» — зарезервированное имя Windows, файл с таким именем не создастся.";
                return false;
            }
        }

        if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Файл библиотеки должен иметь расширение «{Extension}».";
            return false;
        }
        if (Path.GetFileNameWithoutExtension(segments[^1]).Length == 0)
        {
            error = "Имя файла пустое.";
            return false;
        }

        normalized = path;
        return true;
    }

    /// <summary>
    /// Занимает ли путь имя точки входа. Такой файл дерева с ней неразличим (issue #510): его
    /// диагностика приводится к той же строке, поэтому ошибки садились бы на строку точки входа и
    /// помечались бы «входит в сборку» даже будучи неподключёнными.
    ///
    /// Проверяется НЕ в <see cref="TryNormalize"/>, а при появлении нового пути (issue #512): раньше
    /// такой путь был законным, и запись могла прийти из восстановления бэкапа. Отвергая её при
    /// каждом сохранении, мы сделали бы библиотеку несохраняемой целиком — пользователь не смог бы
    /// даже переименовать виновника. Регистр не значим: на Windows это один и тот же файл.
    /// </summary>
    public static bool TakesEntrypointName(string path) =>
        string.Equals(path, UserLibAnalysis.EntrypointName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Нормализованный путь или исключение — для мест, где отказ уже отсеян валидацией.</summary>
    public static string Normalize(string raw) =>
        TryNormalize(raw, out var normalized, out var error)
            ? normalized
            : throw new ArgumentException(error, nameof(raw));

    /// <summary>
    /// Сравнение путей. Регистр учитываем: Linux в контейнере различает «Gost/» и «gost/», и импорт,
    /// написанный по одному написанию, на другом сломается — расхождение проявилось бы только в
    /// продакшене.
    /// </summary>
    public static bool AreEqual(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>
    /// Путь <paramref name="path"/> лежит внутри папки <paramref name="folder"/> — для переименования
    /// и удаления папки целиком (папки отдельной сущности не имеют, они подразумеваются путями).
    /// </summary>
    public static bool IsInFolder(string path, string folder)
    {
        var prefix = folder.EndsWith('/') ? folder : folder + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>Части пути для отображения деревом: «gost/forms/f3.typ» → [gost, forms, f3.typ].</summary>
    public static string[] Segments(string path) => path.Split('/');

    /// <summary>Проверка, что имя не отличается от другого только регистром — предупредить до записи.</summary>
    public static bool DiffersOnlyByCase(string a, string b) =>
        !AreEqual(a, b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Человеческая сортировка дерева: сначала по папкам, внутри — по имени.</summary>
    public static int Compare(string a, string b) =>
        string.Compare(a, b, CultureInfo.GetCultureInfo("ru-RU"), CompareOptions.StringSort);
}
