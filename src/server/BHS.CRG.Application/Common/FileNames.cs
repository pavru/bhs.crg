namespace BHS.CRG.Application.Common;

public static class FileNames
{
    /// <summary>Имя файла без запрещённых символов (недопустимые → '_'). Пусто/только мусор → fallback.</summary>
    public static string Sanitize(string? name, string fallback = "файл")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }
}
