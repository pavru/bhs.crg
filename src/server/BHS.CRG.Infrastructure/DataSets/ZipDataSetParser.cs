using System.IO.Compression;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Парсер ZIP-архивов (расширения .zip, .gsfx и др.).
/// Каждый файл внутри архива становится отдельным DataSetSource.
/// Для файлов с несколькими листами (Excel) — формат sheetOrPath: "entry.xlsx::SheetName".
/// </summary>
public class ZipDataSetParser(IServiceProvider services) : IDataSetParser
{
    // Получаем фабрику из DI во время вызова метода, а не при конструировании,
    // чтобы разорвать циклическую зависимость (ZipParser → Factory → IEnumerable<IParser> → ZipParser).
    private DataSetParserFactory Factory => services.GetRequiredService<DataSetParserFactory>();

    public bool CanParse(DataSetFormat format) => format is DataSetFormat.Zip;

    public async Task<IReadOnlyList<DataSetSourceInfo>> DetectSourcesAsync(byte[] bytes, CancellationToken ct)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read, leaveOpen: false);
        EnsureEntryCountAllowed(zip);
        var sources = new List<DataSetSourceInfo>();

        foreach (var entry in zip.Entries.OrderBy(e => e.FullName))
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // каталоги

            var format = DetectEntryFormat(entry.FullName);
            if (format is null) continue;

            ct.ThrowIfCancellationRequested();
            var entryBytes = ReadEntry(entry);
            var parser = Factory.GetParser(format.Value);
            var entrySources = await parser.DetectSourcesAsync(entryBytes, ct);

            foreach (var s in entrySources)
            {
                // У CSV/XML/JSON один источник — sheetOrPath = путь файла в архиве.
                // У Excel несколько листов — sheetOrPath = "path/file.xlsx::SheetName".
                var sheetOrPath = entrySources.Count == 1
                    ? entry.FullName
                    : $"{entry.FullName}::{s.SheetOrPath}";

                var displayName = entrySources.Count == 1
                    ? entry.Name
                    : $"{entry.Name} / {s.Name}";

                sources.Add(new DataSetSourceInfo(displayName, sheetOrPath, s.Columns, s.RowCount));
            }
        }

        return sources;
    }

    public async Task<DataSetParseResult> ParseAsync(byte[] bytes, string sheetOrPath, string? columnExpressions, CancellationToken ct)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read, leaveOpen: false);
        EnsureEntryCountAllowed(zip);

        string entryPath;
        string? innerSheet;

        var sepIdx = sheetOrPath.IndexOf("::", StringComparison.Ordinal);
        if (sepIdx >= 0)
        {
            entryPath = sheetOrPath[..sepIdx];
            innerSheet = sheetOrPath[(sepIdx + 2)..];
        }
        else
        {
            entryPath = sheetOrPath;
            innerSheet = null;
        }

        var entry = zip.GetEntry(entryPath);
        if (entry is null) return new DataSetParseResult([], []);

        var format = DetectEntryFormat(entryPath)
            ?? throw new InvalidOperationException($"Неизвестный формат файла в архиве: {entryPath}");

        var entryBytes = ReadEntry(entry);
        var parser = Factory.GetParser(format);

        // Для форматов с единственным источником innerSheet не используется.
        var result = await parser.ParseAsync(entryBytes, innerSheet ?? "default", columnExpressions, ct);
        return result;
    }

    /// <summary>
    /// Потолок на распакованный размер ОДНОЙ записи архива. Отдельные файлы наборов данных
    /// принимаются до 500 МБ, но внутри архива запись читается целиком в память, и держать такой же
    /// потолок здесь значит отдать процесс первому же вложенному файлу.
    /// </summary>
    private const long MaxEntryBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Потолок на число записей: перебор в <c>DetectSourcesAsync</c> идёт по всем подряд, а архив из
    /// десятков тысяч крошечных файлов стоит дорого сам по себе, независимо от их размера.
    /// </summary>
    private const int MaxEntries = 2_000;

    /// <summary>
    /// Читает запись архива с потолком на распакованный размер.
    /// </summary>
    /// <remarks>
    /// Заявленному в заголовке размеру (<c>entry.Length</c>) верить нельзя: он берётся из самого
    /// архива, то есть из пользовательского файла. Прежний код выделял по нему буфер сразу — то
    /// есть архив на сотню килобайт с заявленными гигабайтами укладывал процесс ещё до чтения.
    /// Читаем потоком со счётчиком, буфер растёт по мере надобности.
    ///
    /// Zip slip тут не при чём: на диск ничего не пишется, распаковка идёт в память.
    /// </remarks>
    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        // Заявленный размер используем только как ПОДСКАЗКУ и только когда он правдоподобен.
        var hint = entry.Length is > 0 and <= MaxEntryBytes ? (int)entry.Length : 0;

        using var stream = entry.Open();
        using var ms = new MemoryStream(hint);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxEntryBytes)
                throw new ArgumentException(
                    $"Файл «{entry.FullName}» в архиве слишком велик в распакованном виде " +
                    $"(предел {MaxEntryBytes / 1024 / 1024} МБ). Загрузите его отдельным файлом.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>Отказ по числу записей — до того, как начнём их читать.</summary>
    private static void EnsureEntryCountAllowed(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxEntries)
            throw new ArgumentException(
                $"В архиве слишком много файлов ({zip.Entries.Count}, предел {MaxEntries}).");
    }

    internal static DataSetFormat? DetectEntryFormat(string entryName)
    {
        var ext = Path.GetExtension(entryName).ToLowerInvariant();
        return ext switch
        {
            ".csv" or ".txt" => DataSetFormat.Csv,
            ".xlsx"          => DataSetFormat.Xlsx,
            ".xls"           => DataSetFormat.Xls,
            ".xml"           => DataSetFormat.Xml,
            ".json"          => DataSetFormat.Json,
            _                => null,
        };
    }
}
