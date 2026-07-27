using System.Text.RegularExpressions;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>Строка вывода Typst в формате <c>--diagnostic-format short</c>.</summary>
public record TypstShortDiagnostic(string File, int Line, int Column, string Severity, string Message);

/// <summary>
/// Разбор коротких диагностик Typst CLI: <c>путь:строка:колонка: severity: сообщение</c>.
///
/// Вынесено из <see cref="TypstSyntaxChecker"/>, где шаблон был прибит к одному имени файла
/// (<c>typeblocks\.typ</c>). Для дерева библиотеки (issue #473) ошибка приходит из произвольного
/// файла, и такой шаблон дал бы худший вид отказа — ТИХИЙ ЗЕЛЁНЫЙ: ошибок «нет», потому что не
/// совпал путь.
/// </summary>
public static partial class TypstShortDiagnostics
{
    // Путь — всё до «:строка:колонка:». Разделитель может быть и «/», и «\» (Windows), поэтому
    // забираем путь жадно до последней подходящей пары чисел в строке.
    [GeneratedRegex(@"^(?<file>.+?):(?<line>\d+):(?<col>\d+):\s*(?<sev>error|warning):\s*(?<msg>.*)$",
        RegexOptions.Multiline)]
    private static partial Regex ShortDiagRe { get; }

    public static IReadOnlyList<TypstShortDiagnostic> Parse(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return [];
        var result = new List<TypstShortDiagnostic>();
        foreach (Match m in ShortDiagRe.Matches(stderr))
            result.Add(new TypstShortDiagnostic(
                Normalize(m.Groups["file"].Value),
                int.Parse(m.Groups["line"].Value),
                int.Parse(m.Groups["col"].Value),
                m.Groups["sev"].Value,
                m.Groups["msg"].Value.Trim()));
        return result;
    }

    /// <summary>
    /// Путь из диагностики — в вид, которым оперирует приложение: разделители «/», без префикса
    /// временной папки (Typst печатает путь так, как получил его от нас — относительно
    /// рабочей папки процесса).
    /// </summary>
    private static string Normalize(string file)
    {
        var path = file.Replace('\\', '/').Trim();
        return path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
    }
}
