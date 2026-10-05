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
