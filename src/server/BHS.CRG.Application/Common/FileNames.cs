namespace BHS.CRG.Application.Common;

public static class FileNames
{
    /// <summary>Имя файла без запрещённых символов (недопустимые → '_'). Пусто/только мусор → fallback.</summary>
    public static string Sanitize(string? name, string fallback = "файл")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        // Оба разделителя добавлены к платформенному списку явно: на Linux обратный слэш —
        // обычный символ имени, и «отдел\акт.pdf» уезжало в имя файла как есть. Windows такое
        // заменяла, Linux пропускал — а работает система на Linux (issue #854).
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\']).ToHashSet();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }
}
