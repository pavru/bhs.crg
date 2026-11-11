using System.IO.Compression;
using System.Text;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Отладочный пакет — архив каталога компиляции как есть плюс <c>README.txt</c> (ТЗ CORE-21, CORE-22;
/// change core-generation-input-one-path, D3, D5). Своего представления о входе у пакета нет: что
/// записал <see cref="TypstCompileDirectory" />, то и уходит в архив.
/// </summary>
public static class DebugBundleArchive
{
    public const string ReadmeName = "README.txt";

    /// <summary>Заголовок перечня в README — по нему его и находят (и тест, и человек).</summary>
    public const string RefusalHeading = "Генерация этого документа откажет:";

    public static async Task<byte[]> PackAsync(string dir, CompileLayout layout, BundleNotes notes, CancellationToken ct)
    {
        if (Readme(layout, notes) is { } readme)
            await File.WriteAllTextAsync(Path.Combine(dir, ReadmeName), readme, new UTF8Encoding(false), ct);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(Path.GetRelativePath(dir, file).Replace('\\', '/'), CompressionLevel.Optimal);
                await using var es = entry.Open();
                await using var fs = File.OpenRead(file);
                await fs.CopyToAsync(es, ct);
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// README — только когда есть что сказать (D5): ключ <c>--font-path</c>, если шрифт записан, и
    /// перечень причин отказа, если генерация документ не выпустит. Предупреждения в перечень не
    /// входят: выпуск они не останавливают. Пакет без шрифтов и без отказов README не получает.
    /// </summary>
    public static string? Readme(CompileLayout layout, BundleNotes notes)
    {
        var refusals = notes.Refusals.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var refused = refusals.Count > 0 || layout.UnreadableImages.Count > 0 || layout.InvalidAssetNames.Count > 0;
        if (layout.FontsDir is null && !refused) return null;

        var sb = new StringBuilder();
        sb.Append("Каталог компиляции генерации этого документа. Компилируйте из распакованного каталога:\r\n\r\n");
        sb.Append(layout.FontsDir is null
            ? "  typst compile template.typ output.pdf\r\n"
            : "  typst compile template.typ output.pdf --font-path fonts\r\n");
        if (!refused) return sb.ToString();

        sb.Append("\r\n").Append(RefusalHeading).Append("\r\n");
        foreach (var d in refusals)
            sb.Append($"  - [{(d.Code.Length > 0 ? d.Code : "error")}] {d.Path}: {d.Message}\r\n");
        foreach (var field in layout.UnreadableImages)
            sb.Append($"  - [unreadable-image] {field}: картинка не прочитана из хранилища — файла нет или он недоступен документу.\r\n");
        foreach (var name in layout.InvalidAssetNames)
            sb.Append($"  - [invalid-asset-name] {name}: имя ассета шаблона недопустимо, файл не записан — удалите ассет и загрузите заново под допустимым именем — {TemplateAsset.ImageNameRule}.\r\n");
        return sb.ToString();
    }
}
