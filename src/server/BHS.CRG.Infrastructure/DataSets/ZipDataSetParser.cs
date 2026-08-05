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
        var budget = new UnpackBudget(MaxTotalBytes);
        var sources = new List<DataSetSourceInfo>();

        foreach (var entry in zip.Entries.OrderBy(e => e.FullName))
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // каталоги

            var format = DetectEntryFormat(entry.FullName);
            if (format is null) continue;

            ct.ThrowIfCancellationRequested();
            var entryBytes = ReadEntry(entry, budget);
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
        // Предел на ЧИСЛО записей здесь намеренно не проверяется, в отличие от DetectSourcesAsync:
        // читаем ровно одну запись, а архивы, загруженные до появления предела, уже привязаны к
        // источникам — отказ на этом пути означал бы, что рабочий набор данных ломается обновлением
        // и починить его нечем, кроме перезаливки.
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

        var entryBytes = ReadEntry(entry, new UnpackBudget(MaxEntryBytes));
        var parser = Factory.GetParser(format);

        // Для форматов с единственным источником innerSheet не используется.
        var result = await parser.ParseAsync(entryBytes, innerSheet ?? "default", columnExpressions, ct);
        return result;
    }

    /// <summary>
    /// Потолок на распакованный размер ОДНОЙ записи. Отдельным файлом набор данных принимается до
    /// <c>UploadLimits.DataSetFile</c> = 50 МБ; внутри архива запись читается целиком в память, так
    /// что потолок держим того же порядка с запасом, а не «сколько не жалко».
    /// </summary>
    private const long MaxEntryBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Потолок на СУММАРНЫЙ распакованный объём за один разбор архива.
    ///
    /// Без него пределы не складывались: две тысячи записей по 64 МБ — это 128 ГБ распаковки в одном
    /// запросе. Памяти это не съедает (запись за раз одна), но поток занят часами, а входной архив
    /// при хорошей сжимаемости весит единицы мегабайт.
    /// </summary>
    private const long MaxTotalBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Потолок на число записей: перебор в <c>DetectSourcesAsync</c> идёт по всем подряд, а архив из
    /// десятков тысяч крошечных файлов стоит дорого сам по себе, независимо от их размера.
    /// </summary>
    private const int MaxEntries = 2_000;

    /// <summary>Начальный буфер под запись. Дальше <see cref="MemoryStream"/> растёт сам.</summary>
    private const int InitialBufferBytes = 256 * 1024;

    /// <summary>Остаток разрешённого объёма распаковки на весь разбор архива.</summary>
    private sealed class UnpackBudget(long remaining)
    {
        public long Remaining { get; set; } = remaining;
    }

    /// <summary>
    /// Читает запись архива с потолком на распакованный размер и с общим бюджетом на архив.
    /// </summary>
    /// <remarks>
    /// Заявленному в заголовке размеру (<c>entry.Length</c>) верить нельзя ВООБЩЕ: он берётся из
    /// самого архива, то есть из пользовательского файла. Прежний код выделял по нему буфер сразу —
    /// архив на сотню килобайт с заявленными гигабайтами укладывал процесс ещё до чтения. Начальный
    /// буфер поэтому фиксированный и маленький: подсказка из заголовка — это та же подсказка от
    /// нападающего, только ограниченная сверху.
    ///
    /// Zip slip тут не при чём: на диск ничего не пишется, распаковка идёт в память.
    /// </remarks>
    private static byte[] ReadEntry(ZipArchiveEntry entry, UnpackBudget budget)
    {
        using var stream = entry.Open();
        using var ms = new MemoryStream(InitialBufferBytes);
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
            if (total > budget.Remaining)
                throw new ArgumentException(
                    $"Суммарный размер файлов в архиве превышает предел " +
                    $"{MaxTotalBytes / 1024 / 1024} МБ в распакованном виде.");
            ms.Write(buffer, 0, read);
        }
        budget.Remaining -= total;
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
