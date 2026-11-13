using System.Text.RegularExpressions;

namespace BHS.CRG.Tests.Common;

/// <summary>
/// Исходники решения — для сторожей-переписей, которые читают код, а не сборку (issue #1104).
///
/// <para>Заведён общим, потому что тот же обход лежал копиями в полудюжине сторожей
/// (<c>ActivityLogInventoryTests</c>, <c>InvoiceWritePathTests</c> и другие): правка правила обхода —
/// «не считать ещё один генерируемый каталог» — расходилась бы по ним молча. Старшие копии переезжают
/// сюда по мере правок.</para>
/// </summary>
public static class SourceTree
{
    public static string SolutionDir { get; } = FindSolutionDir();

    /// <summary>Рукописные файлы проекта: без <c>obj</c>, <c>bin</c> и миграций.</summary>
    public static IEnumerable<string> Files(string project) =>
        Directory.EnumerateFiles(Path.Combine(SolutionDir, project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Generated.Any(part => f.Contains($"{Path.DirectorySeparatorChar}{part}{Path.DirectorySeparatorChar}")));

    public static string Relative(string full) => Path.GetRelativePath(SolutionDir, full).Replace('\\', '/');

    public static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    /// <summary>Строка файла целиком — чтобы решить, чем именно она занята.</summary>
    public static string LineAt(string text, int index)
    {
        var from = text.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;
        var to = text.IndexOf('\n', index);
        return text[from..(to < 0 ? text.Length : to)].TrimEnd('\r');
    }

    /// <summary>
    /// Текст без комментариев — строчных (<c>//</c>, <c>///</c>, в том числе в хвосте строки кода) и
    /// блочных (<c>/* … */</c>). Переводы строк сохраняются, номера строк совпадают с файлом.
    ///
    /// <para>С учётом строковых литералов: <c>"https://…"</c> комментарием не считается, иначе всё после
    /// адреса в той же строке выпало бы из проверки молча. Различаются обычные строки, verbatim
    /// (<c>@"…"</c>, кавычка удваивается), сырые (<c>"""…"""</c>) и символы (<c>'"'</c>). Вложенные
    /// строки в дырах интерполяции не разбираются — чётность кавычек их выравнивает.</para>
    /// </summary>
    public static string WithoutComments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (c == '/' && next == '*')
            {
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    if (text[i] == '\n') sb.Append('\n');
                    i++;
                }
                i = Math.Min(i + 2, text.Length);
            }
            else if (c == '"') i = CopyString(text, i, sb);
            else if (c == '\'') i = CopyChar(text, i, sb);
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Обращение к таблице в тексте SQL: после <c>FROM / JOIN / INTO / UPDATE / TABLE / TRUNCATE</c>, с
    /// кавычками вокруг схемы и имени или без. Кавычка в исходнике бывает <c>"</c>, <c>\"</c> и удвоенной
    /// <c>""</c> (verbatim-строка).
    ///
    /// <para><paramref name="schemaOptional" />: <c>false</c> — без схемы не совпадает. Так у таблиц
    /// модуля: путь поиска базы — схема ядра, и голое имя до таблицы модуля не дойдёт, а совпадало бы с
    /// LINQ (<c>from reports in …</c>, <c>group … into reports</c>).</para>
    /// </summary>
    public static Regex SqlTouch(string? schema, bool schemaOptional, IEnumerable<string> tables)
    {
        var prefix = schema is null
            ? ""
            : $@"(?:{Quote}{Regex.Escape(schema)}{Quote}\.){(schemaOptional ? "?" : "")}";
        return new Regex(
            $@"\b(?:FROM|INTO|UPDATE|JOIN|TABLE|TRUNCATE)\s+(?:ONLY\s+)?{prefix}{Quote}(?:{string.Join('|', tables.Select(Regex.Escape))})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private const string Quote = @"(?:\\?""){0,2}";

    private static int CopyString(string text, int i, System.Text.StringBuilder sb)
    {
        var verbatim = i > 0 && (text[i - 1] == '@' || (text[i - 1] == '$' && i > 1 && text[i - 2] == '@'));
        var run = 0;
        while (i + run < text.Length && text[i + run] == '"') run++;

        if (run >= 3 && !verbatim)
        {
            // Сырая строка: кончается такой же серией кавычек.
            sb.Append(text, i, run);
            var end = text.IndexOf(new string('"', run), i + run, StringComparison.Ordinal);
            var stop = end < 0 ? text.Length : end + run;
            sb.Append(text, i + run, stop - i - run);
            return stop;
        }
        if (run == 2 && !verbatim)
        {
            sb.Append("\"\"");
            return i + 2;
        }

        sb.Append('"');
        var j = i + 1;
        while (j < text.Length)
        {
            var c = text[j];
            if (!verbatim && c == '\\' && j + 1 < text.Length)
            {
                sb.Append(c).Append(text[j + 1]);
                j += 2;
                continue;
            }
            if (c == '"')
            {
                if (verbatim && j + 1 < text.Length && text[j + 1] == '"')
                {
                    sb.Append("\"\"");
                    j += 2;
                    continue;
                }
                sb.Append('"');
                return j + 1;
            }
            if (!verbatim && c == '\n') return j; // незакрытая строка — до конца строки, не дальше
            sb.Append(c);
            j++;
        }
        return j;
    }

    private static int CopyChar(string text, int i, System.Text.StringBuilder sb)
    {
        sb.Append('\'');
        var j = i + 1;
        while (j < text.Length && text[j] != '\'' && text[j] != '\n')
        {
            if (text[j] == '\\' && j + 1 < text.Length) sb.Append(text[j++]);
            sb.Append(text[j++]);
        }
        if (j < text.Length && text[j] == '\'') sb.Append(text[j++]);
        return j;
    }

    private static readonly string[] Generated = ["obj", "bin", "Migrations"];

    private static string FindSolutionDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден каталог решения (BHS.CRG.slnx) выше " + AppContext.BaseDirectory +
                " — сторож читает исходники, и без них проверять нечего.");
    }
}
