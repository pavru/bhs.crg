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

    public async Task<DataSetParseResult> ParseAsync(byte[] bytes, string sheetOrPath, CancellationToken ct)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read, leaveOpen: false);

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
        var result = await parser.ParseAsync(entryBytes, innerSheet ?? "default", ct);
        return result;
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var ms = new MemoryStream((int)Math.Max(entry.Length, 0));
        stream.CopyTo(ms);
        return ms.ToArray();
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
