using System.Text;
using BHS.CRG.Application.Generation;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Раскладка блоков типов на диск (issue #772) — единая точка для генерации, проверки блоков и
/// отладочного бандла, по образцу <see cref="UserLibMaterializer"/>. Раскладка обязана совпадать во
/// всех трёх: расхождение означало бы, что проверка зелёная там, где генерация падает.
///
/// <code>
/// &lt;dir&gt;/typeblocks.typ        — агрегатор; его импортирует шаблон (#353, дословно)
/// &lt;dir&gt;/typeblocks/&lt;слаг&gt;.typ — модуль на тип
/// </code>
/// </summary>
public static class TypeBlocksMaterializer
{
    /// <summary>Плейсхолдер: агрегатор обязан существовать даже без единого блока, иначе
    /// `#import "typeblocks.typ": *` не резолвится и падает КАЖДЫЙ документ.</summary>
    public const string EmptyEntrypoint = "// no composite-type render functions defined";

    public static async Task WriteAsync(
        string dir, IReadOnlyList<TypstBlockFile>? files, CancellationToken ct = default)
    {
        var entrypoint = Path.Combine(dir, TypeBlockSlug.EntrypointName);
        if (files is null || files.Count == 0)
        {
            await File.WriteAllTextAsync(entrypoint, EmptyEntrypoint, Encoding.UTF8, ct);
            return;
        }

        var rootFull = Path.GetFullPath(dir);
        foreach (var file in files)
        {
            var target = Path.GetFullPath(
                Path.Combine(dir, file.Path.Replace('/', Path.DirectorySeparatorChar)));

            // Пути мы формируем сами (слаг санитизирован), но раскладка пишет в файловую систему по
            // строке из данных — проверка стоит одну строку и снимает целый класс сюрпризов.
            if (!target.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Путь файла блоков «{file.Path}» выходит за пределы папки компиляции.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, file.Content, Encoding.UTF8, ct);
        }
    }
}
