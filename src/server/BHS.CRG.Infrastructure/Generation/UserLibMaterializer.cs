using System.Text;
using BHS.CRG.Application.Templates;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Раскладка библиотеки на диск (issue #473) — единая точка для генерации, проверки при сохранении и
/// отладочного бандла. Раскладка обязана совпадать во всех трёх: расхождение означало бы, что
/// проверка зелёная там, где генерация падает.
///
/// <code>
/// &lt;dir&gt;/userlib.typ        — точка входа; её импортирует шаблон (#353, дословно)
/// &lt;dir&gt;/userlib/...        — дерево, структуру которого задаёт пользователь
/// </code>
/// </summary>
public static class UserLibMaterializer
{
    /// <summary>Плейсхолдер пустой библиотеки — файл обязан существовать, иначе импорт в шаблоне не резолвится.</summary>
    public const string EmptyEntrypoint = "// user typst library is empty";

    public static async Task WriteAsync(
        string dir, string? entrypointContent, IReadOnlyList<UserLibFile>? files, CancellationToken ct = default)
    {
        await File.WriteAllTextAsync(
            Path.Combine(dir, UserLibAnalysis.EntrypointName),
            string.IsNullOrEmpty(entrypointContent) ? EmptyEntrypoint : entrypointContent,
            Encoding.UTF8, ct);

        if (files is null || files.Count == 0) return;

        var root = Path.Combine(dir, UserLibPath.FolderName);
        Directory.CreateDirectory(root);
        var rootFull = Path.GetFullPath(root);

        foreach (var file in files)
        {
            var target = Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));

            // Путь проверяется при сохранении, но записи мог наделать и восстановленный бэкап из
            // другой (в том числе более старой) инсталляции. Запись за пределы дерева — не та
            // ошибка, которую стоит ловить один раз.
            if (!target.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Путь файла библиотеки «{file.Path}» выходит за пределы папки {UserLibPath.FolderName}/.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, file.Content, Encoding.UTF8, ct);
        }
    }
}
