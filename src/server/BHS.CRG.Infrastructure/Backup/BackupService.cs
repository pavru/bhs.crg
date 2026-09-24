using System.Data;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Backup;

public partial class BackupService(AppDbContext db, IBlobStorage blob, ILogger<BackupService> logger,
    BHS.CRG.Application.Activity.IActivityLog journal)
{
    // v2 (issue #84): общие данные теперь DomainObject (без документной фасеты). Старые копии (v1)
    // несовместимы — чистый разрыв (решение пользователя): импорт отклоняется.
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Версия сборки, которой сделана копия. Поле манифеста существует ровно для разбора «чем это
    /// снято», и константа в коде («1.0.0», не менявшаяся с первых версий) делала его бесполезным:
    /// все копии выглядели одинаково, независимо от того, какой сборкой сняты.
    ///
    /// Читаем из СВОЕЙ сборки, а не из входной: версия у всех проектов решения одна
    /// (Directory.Build.props), а входной сборкой под тестовым хостом оказывается прогонщик тестов —
    /// и в манифест уехала бы его версия вместо нашей.
    /// </summary>
    public static string CurrentAppVersion { get; } =
        typeof(BackupService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+', 2)[0]
        ?? "0.0.0";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // ── Export ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Снять копию в файл по заданному пути (issue #831). Основной путь: так копия ложится в
    /// каталог на сервере, откуда её и восстанавливают, не пересекая сеть.
    /// </summary>
    /// <param name="path">Куда писать. Вызывающий пишет во временный файл и переименовывает его —
    /// прерванный экспорт не должен оставлять в каталоге огрызок, неотличимый от копии.</param>
    /// <param name="progress">Отчёт «сколько файлов из скольких» для фоновой задачи; null — молча.</param>
    public async Task<BackupSummary> ExportToFileAsync(
        string path, BackupScope scope = BackupScope.Configuration,
        Func<int, int, Task>? progress = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var manifest = await BuildManifestAsync(scope, warnings, ct);

        // Архив собирается на ДИСКЕ, а не в памяти. Пока копия несла только ассеты шаблонов, это
        // были единицы мегабайт и MemoryStream ничего не стоил. С библиотекой качества (issue #687)
        // размер задаётся числом сертификатов и растёт годами: MemoryStream удваивает буфер, то есть
        // на пике держит около двух объёмов архива в куче больших объектов, и упирается в
        // int.MaxValue — причём отказ пришёл бы ровно тогда, когда копия нужнее всего.
        await using var file = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, FileOptions.Asynchronous);

        var blobPaths = ExtractBlobPaths(manifest);
        var summary = BuildSummary(manifest, blobPaths.Count, warnings);
        var missingBlobs = 0;

        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Write manifest.json
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Fastest);
            await using (var w = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(w, manifest, JsonOptions, ct);

            // Write binary blobs
            var done = 0;
            foreach (var blobPath in blobPaths)
            {
                try
                {
                    // await using, а не голый вызов: поток от хранилища держит соединение, и до
                    // issue #687 их было по числу ассетов шаблона — единицы. Теперь их по числу
                    // сертификатов в библиотеке, и неосвобождённые ответы исчерпают пул соединений
                    // клиента MinIO — экспорт не упадёт, а повиснет, что разбирать заметно труднее.
                    await using var blobStream = await blob.DownloadAsync(blobPath, ct);
                    var entry = zip.CreateEntry($"blobs/{blobPath}", CompressionLevel.NoCompression);
                    await using var ew = entry.Open();
                    await blobStream.CopyToAsync(ew, ct);
                }
                catch (Exception ex)
                {
                    // Blob missing in storage — skip, DB reference kept intact
                    logger.LogWarning(ex, "Бинарный файл отсутствует в хранилище при экспорте бэкапа: {BlobPath}", blobPath);
                    missingBlobs++;
                }

                done++;
                if (progress is not null) await progress(done, blobPaths.Count);
            }

            if (missingBlobs > 0)
                warnings.Add(
                    $"Файлов не оказалось в хранилище: {missingBlobs} из {blobPaths.Count} — " +
                    "в копию они не попали, и после восстановления ссылки на них останутся битыми.");

            // Паспорт пишем ПОСЛЕДНИМ, хотя читается он первым: до конца прогона по блобам не
            // известно, чего в хранилище не оказалось, а поле «что пропущено» заведено именно
            // затем, чтобы узнать это при снятии копии, а не при восстановлении. Порядок записей
            // в архиве на чтение не влияет — оглавление zip лежит в конце файла.
            summary = summary with { Warnings = warnings.Count > 0 ? warnings.ToArray() : null };
            await BackupFileStore.WriteSummaryAsync(zip, summary, ct);
        }

        return summary;
    }

    /// <summary>
    /// Копия одним потоком, без каталога на сервере. Прямого потребителя у этой формы больше нет —
    /// экспорт идёт фоновой задачей в каталог (issue #831), — но она остаётся точкой, на которой
    /// стоят тесты round-trip: путь внутри тот же самый, отличается только место записи.
    /// </summary>
    public async Task<(Stream ZipStream, string FileName)> ExportAsync(
        BackupScope scope = BackupScope.Configuration, CancellationToken ct = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"crg-backup-{Guid.NewGuid():N}.zip");
        var summary = await ExportToFileAsync(path, scope, null, ct);

        // DeleteOnClose: файл исчезает, как только поток закроют — отдельной уборки не нужно, и она
        // не потеряется при разрыве соединения.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None,
            bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        return (stream, BackupFileStore.BuildFileName(summary.CreatedAt, summary.AppVersion));
    }

    /// <summary>
    /// Паспорт копии: чем снята, когда и что внутри. Счёт разделов — то, что список копий
    /// показывает как «состав»; названия здесь, а не на клиенте, потому что новый раздел копии
    /// добавляется здесь же и не должен требовать правки в двух местах.
    /// </summary>
    private static BackupSummary BuildSummary(
        BackupManifest manifest, int blobCount, IReadOnlyList<string> warnings)
    {
        BackupSectionCount[] sections =
        [
            new("Типы документов", manifest.DocumentTypes.Length),
            new("Шаблоны", manifest.Templates.Length),
            new("Ассеты шаблонов", manifest.TemplateAssets?.Length ?? 0),
            new("Справочник", manifest.CatalogEntities.Length),
            new("Общие данные", manifest.CommonDataEntries.Length),
            new("Примитивные типы", manifest.PrimitiveTypes?.Length ?? 0),
            new("Перечисления", manifest.EnumTypes?.Length ?? 0),
            new("Профили распознавания", manifest.RecognitionProfiles?.Length ?? 0),
            new("Шаблоны маппинга", manifest.DataSetBindingTemplates?.Length ?? 0),
            new("Рецепты обработки", manifest.DataSetProcessingTemplates?.Length ?? 0),
            new("Алиасы сверки", manifest.ReconciliationAliases?.Length ?? 0),
            new("Документы качества", manifest.QualityDocuments?.Length ?? 0),
            new("Файлы библиотеки Typst", manifest.TypstUserLibFiles?.Count ?? 0),
            new("Журнал действий", manifest.ActivityLog?.Length ?? 0),
            // Проектные данные (issue #833) — в полной копии.
            new("Стройки", manifest.Constructions?.Length ?? 0),
            new("Разделы", manifest.Sections?.Length ?? 0),
            new("Комплекты", manifest.DocumentSets?.Length ?? 0),
            new("Строки плана", manifest.DocumentSetPlans?.Length ?? 0),
            new("Документы комплектов", manifest.Documents?.Length ?? 0),
            new("Выпущенные файлы", manifest.Documents?.Sum(d => d.GeneratedFiles.Length) ?? 0),
            new("Наборы данных", manifest.DataSetFiles?.Length ?? 0),
            new("Источники данных", manifest.DataSetSources?.Length ?? 0),
            new("Привязки наборов", manifest.DataSetBindings?.Length ?? 0),
            new("Сверки", manifest.Reconciliations?.Length ?? 0),
            new("Связки с материалами", manifest.MaterialQualityLinks?.Length ?? 0),
        ];

        return new BackupSummary(
            manifest.SchemaVersion, manifest.AppVersion, manifest.CreatedAt,
            blobCount, sections.Where(s => s.Count > 0).ToArray(),
            manifest.IncludesProjectData == true,
            warnings.Count > 0 ? warnings.ToArray() : null);
    }

    // ── Оценка размера ────────────────────────────────────────────────────────

    /// <summary>
    /// Сколько будет весить копия, снятая прямо сейчас, — не снимая её (issue #711), и в каком
    /// составе (issue #833: составов два — настройка и настройка вместе с проектной работой).
    ///
    /// Зачем вообще. Восстановление отказывает на архиве больше предела, а экспорт про этот предел
    /// не знал вовсе и молча отдавал архив любого размера. Пока копия несла ассеты шаблонов,
    /// разойтись этим числам было негде; с библиотекой качества (issue #687) вес задаётся тем,
    /// сколько сертификатов накопилось, и растёт годами. Система исправно делала бы копии, которые
    /// сама же откажется принять, а узнали бы об этом при восстановлении — то есть после аварии,
    /// когда выбора уже нет.
    ///
    /// <para><b>Почему это оценка, а не выдумка.</b> Манифест сериализуется через тот же Deflate и с
    /// тем же уровнем, что и запись в архив, — считаем сжатый размер, а не исходный. Разница здесь
    /// не косметическая: в общих данных лежат картинки в base64, и несжатый объём завышал бы вес в
    /// разы, то есть тревога приходила бы задолго до повода. Сканы кладутся в архив БЕЗ сжатия,
    /// поэтому сумма их размеров — точное значение, а не приближение.</para>
    ///
    /// <para>Стоит это одного построения манифеста и по запросу размера на каждый блоб (HEAD, без
    /// выкачивания содержимого). Поэтому вызывается по требованию — с раскрытого раздела настроек,
    /// а не при каждой загрузке страницы.</para>
    /// </summary>
    public async Task<BackupSizeEstimate> EstimateSizeAsync(
        long limitBytes, BackupScope scope = BackupScope.Configuration, CancellationToken ct = default)
    {
        // Считаем ТОЛЬКО запрошенный состав. Полный манифест — это все объекты с их данными и все
        // источники наборов ВМЕСТЕ С КЭШЕМ разбора; держать его в памяти ради строки на экране
        // настроек можно лишь тогда, когда именно этот состав человек и выбрал. Пока выбрана
        // «настройка», проектные данные не читаются вовсе — как и до issue #833.
        var manifest = await BuildManifestAsync(scope, [], ct);
        return new BackupSizeEstimate(
            scope.ToString(),
            await MeasureAsync(manifest, new Dictionary<string, long?>(StringComparer.Ordinal), ct),
            limitBytes);
    }

    /// <summary>Вес одного состава: сжатый манифест плюс блобы как есть плюс заголовки записей.</summary>
    private async Task<BackupSizeVariant> MeasureAsync(
        BackupManifest manifest, Dictionary<string, long?> sizes, CancellationToken ct)
    {
        var counter = new CountingStream();
        await using (var deflate = new DeflateStream(counter, CompressionLevel.Fastest, leaveOpen: true))
            await JsonSerializer.SerializeAsync(deflate, manifest, JsonOptions, ct);
        var manifestBytes = counter.Written;

        var paths = ExtractBlobPaths(manifest);
        var overhead = EntryOverhead("manifest.json") + EntryOverhead(BackupFileStore.SummaryEntryName);

        // Паспорт копии (issue #831) - вторая JSON-запись архива. Считаем её тем же Deflate: без
        // неё оценка занижала бы вес на её размер, а сходство оценки с настоящим архивом
        // проверяется с точностью до сотен байт - то есть разъехалось бы сразу и молча.
        var summaryCounter = new CountingStream();
        await using (var deflate = new DeflateStream(summaryCounter, CompressionLevel.Fastest, leaveOpen: true))
            await deflate.WriteAsync(BackupFileStore.SummaryBytes(BuildSummary(manifest, paths.Count, [])), ct);
        manifestBytes += summaryCounter.Written;

        long blobBytes = 0;
        var missing = 0;
        foreach (var path in paths)
        {
            overhead += EntryOverhead($"blobs/{path}");
            // Размер каждого блоба спрашиваем один раз на обе оценки: конфигурационные файлы
            // входят в обе, и повторный HEAD по каждому из них удвоил бы стоимость запроса.
            if (!sizes.TryGetValue(path, out var size))
                sizes[path] = size = await blob.GetSizeAsync(path, ct);
            // Недоступный блоб экспорт пропускает с предупреждением - оценка считает его так же.
            if (size is null) missing++;
            else blobBytes += size.Value;
        }

        return new BackupSizeVariant(
            TotalBytes: manifestBytes + blobBytes + overhead,
            ManifestBytes: manifestBytes,
            BlobBytes: blobBytes,
            BlobCount: paths.Count,
            MissingBlobCount: missing);
    }

    private async Task<BackupManifest> BuildManifestAsync(
        BackupScope scope, List<string> warnings, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || !db.Database.IsRelational())
            return await ReadManifestAsync(scope, warnings, ct);

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var manifest = await ReadManifestAsync(scope, warnings, ct);
        await tx.CommitAsync(ct);
        return manifest;
    }
}
