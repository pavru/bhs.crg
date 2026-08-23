using System.Data;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Reconciliation;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Backup;

public class BackupService(AppDbContext db, IBlobStorage blob, ILogger<BackupService> logger)
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
            // Проектные данные (issue #833) — в полной копии.
            new("Стройки", manifest.Constructions?.Length ?? 0),
            new("Разделы", manifest.Sections?.Length ?? 0),
            new("Комплекты", manifest.DocumentSets?.Length ?? 0),
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

    private async Task<BackupManifest> ReadManifestAsync(
        BackupScope scope, List<string> warnings, CancellationToken ct)
    {
        var docTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var templates = await db.Templates.AsNoTracking().ToListAsync(ct);
        // Шаблон без своего типа документа (issue #833). У Template нет внешнего ключа на
        // DocumentType, и удаление типа оставляло шаблоны сиротами — на рабочей базе таких семь.
        // Класть их в копию незачем: восстановление всё равно пропустит их с предупреждением, но
        // произойдёт это после аварии. Отказываемся здесь и говорим об этом в паспорте копии.
        var typeIds = docTypes.Select(t => t.Id).ToHashSet();
        var orphanTemplates = templates.Where(t => !typeIds.Contains(t.DocumentTypeId)).ToList();
        if (orphanTemplates.Count > 0)
        {
            templates = templates.Except(orphanTemplates).ToList();
            warnings.Add(
                $"Пропущено шаблонов без своего типа документа: {orphanTemplates.Count} " +
                $"({string.Join(", ", orphanTemplates.Take(5).Select(t => t.Name + " v" + t.Version))}" +
                (orphanTemplates.Count > 5 ? ", ..." : "") + ").");
        }
        var catalogEntities = await db.CatalogEntities.AsNoTracking().ToListAsync(ct);
        var commonDataEntries = await db.DomainObjects.AsNoTracking().Where(o => o.Facet == null).ToListAsync(ct);
        var primitiveTypes = await db.PrimitiveTypes.AsNoTracking().ToListAsync(ct);
        var enumTypes = await db.EnumTypes.AsNoTracking().ToListAsync(ct);
        var templateAssets = await db.TemplateAssets.AsNoTracking().ToListAsync(ct);
        var userLib = await db.TypstUserLibs.AsNoTracking().FirstOrDefaultAsync(ct);
        var userLibFiles = await db.TypstUserLibFiles.AsNoTracking().OrderBy(f => f.Path).ToListAsync(ct);
        var recognitionProfiles = await db.RecognitionProfiles.AsNoTracking().ToListAsync(ct);
        var bindingTemplates = await db.DataSetBindingTemplates.AsNoTracking().ToListAsync(ct);
        var processingTemplates = await db.DataSetProcessingTemplates.AsNoTracking().ToListAsync(ct);
        // Библиотека качества целиком, всех уровней (решение по issue #687). Отбирать по уровню
        // «Система» было бы разумно по смыслу областей, но документ уровня комплекта — тот же
        // сертификат, просто подшитый к проекту, и половина библиотеки после восстановления хуже
        // целой. Цена решения — вес: сканы это мегабайты, и копия растёт вместе с библиотекой.
        var qualityDocuments = await db.QualityDocuments.AsNoTracking().ToListAsync(ct);

        // Алиасы: переносим РЕШЕНИЯ человека — подтверждённые и отклонённые. Предложенные не берём:
        // это неразобранный шум (в том числе от агента), который на новой системе появится заново.
        // Отклонённые важны не меньше подтверждённых: они и существуют затем, чтобы предложение не
        // всплывало снова, и потеря их означала бы разбирать те же предложения второй раз.
        var aliases = await db.ReconciliationAliases.AsNoTracking()
            .Where(a => a.Status != AliasStatus.Proposed)
            .ToListAsync(ct);

        // Проектные данные (issue #833) читаются ТОЛЬКО для полной копии: конфигурационная
        // остаётся ровно тем, чем была, и весит столько же. Порядок чтения не важен - снимок один.
        var full = scope == BackupScope.Full;
        var constructions = full ? await db.Constructions.AsNoTracking().ToListAsync(ct) : [];
        var sections = full ? await db.Sections.AsNoTracking().ToListAsync(ct) : [];
        var sets = full ? await db.DocumentSets.AsNoTracking().ToListAsync(ct) : [];
        var documents = full
            ? await db.DomainObjects.AsNoTracking().Include(o => o.Facet)
                .Where(o => o.Facet != null).ToListAsync(ct)
            : [];
        var generatedFiles = full ? await db.GeneratedFiles.AsNoTracking().ToListAsync(ct) : [];
        var dataSetFiles = full ? await db.DataSetFiles.AsNoTracking().ToListAsync(ct) : [];
        var dataSetSources = full ? await db.DataSetSources.AsNoTracking().ToListAsync(ct) : [];
        var dataSetBindings = full ? await db.DataSetBindings.AsNoTracking().ToListAsync(ct) : [];
        var reconciliations = full ? await db.Reconciliations.AsNoTracking().ToListAsync(ct) : [];
        var materialLinks = full ? await db.MaterialQualityLinks.AsNoTracking().ToListAsync(ct) : [];

        // Привязка, потерявшая объект-владельца, — та же сирота, что и шаблон без типа: внешнего
        // ключа на OwnerId нет, и на рабочей базе таких две из двенадцати. В копии от неё вреда
        // нет, но восстановление всё равно её отбросит — значит, отбрасываем здесь и говорим вслух.
        if (full)
        {
            var objectIds = documents.Select(o => o.Id)
                .Concat(commonDataEntries.Select(o => o.Id))
                .ToHashSet();
            var orphanBindings = dataSetBindings.Where(b => !objectIds.Contains(b.OwnerId)).ToList();
            if (orphanBindings.Count > 0)
            {
                dataSetBindings = dataSetBindings.Except(orphanBindings).ToList();
                warnings.Add($"Пропущено привязок наборов без объекта-владельца: {orphanBindings.Count}.");
            }
        }

        var filesByObject = generatedFiles.GroupBy(f => f.ObjectId)
            .ToDictionary(g => g.Key, g => g.ToArray());

        return new BackupManifest(
            SchemaVersion: CurrentSchemaVersion,
            AppVersion: CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes: docTypes.Select(dt => new BackupDocumentType(
                dt.Id, dt.Name, dt.Code, dt.Kind.ToString(), dt.ParentId, dt.IsAbstract,
                dt.Schema.RootElement.Clone(), dt.PluginBindings.RootElement.Clone(),
                dt.CreatedAt, dt.UpdatedAt, dt.Group, dt.AllowsProxy)).ToArray(),
            Templates: templates.Select(t => new BackupTemplate(
                t.Id, t.DocumentTypeId, t.Name, t.Content, t.Version,
                t.IsActive, t.IsDefault,
                t.CreatedAt, t.UpdatedAt, t.Parameters, t.Comment)).ToArray(),
            CatalogEntities: catalogEntities.Select(e => new BackupCatalogEntity(
                e.Id, e.EntityType, e.DisplayName, e.Data.RootElement.Clone(), e.OwnerId,
                e.CreatedAt, e.UpdatedAt)).ToArray(),
            CommonDataEntries: commonDataEntries.Select(e => new BackupCommonDataEntry(
                e.Id, e.DisplayName ?? "", e.CompositeTypeId, e.Data.RootElement.Clone(),
                e.ScopeLevel.ToString(), e.ScopeId,
                e.CreatedAt, e.UpdatedAt, e.Aliases.ToArray())).ToArray(),
            PrimitiveTypes: primitiveTypes.Select(p => new BackupPrimitiveType(
                p.Id, p.Name, p.Code, p.BaseType, p.Description,
                p.Constraints.RootElement.Clone(),
                p.CreatedAt, p.UpdatedAt, p.Group)).ToArray(),
            EnumTypes: enumTypes.Select(e => new BackupEnumType(
                e.Id, e.Name, e.Code, e.Description, e.Values.RootElement.Clone(),
                e.CreatedAt, e.UpdatedAt, e.Group)).ToArray(),
            TemplateAssets: templateAssets.Select(a => new BackupTemplateAsset(
                a.Id, a.Scope.ToString(), a.ScopeId, a.Kind.ToString(),
                a.Name, a.FileName, a.MimeType, a.BlobPath, a.FontFamilyName,
                a.CreatedAt, a.UpdatedAt)).ToArray(),
            TypstUserLib: userLib is null ? null
                : new BackupTypstUserLib(userLib.Content, userLib.CreatedAt, userLib.UpdatedAt),
            TypstUserLibFiles: userLibFiles
                .Select(f => new BackupTypstUserLibFile(f.Id, f.Path, f.Content, f.CreatedAt, f.UpdatedAt))
                .ToList(),
            RecognitionProfiles: recognitionProfiles.Select(p => new BackupRecognitionProfile(
                p.Id, p.Name, p.Code, p.Kind.ToString(),
                p.Fields.RootElement.Clone(), p.Shape?.RootElement.Clone(),
                p.IsBuiltIn, p.IsModified, p.CreatedAt, p.UpdatedAt,
                p.RowColumns?.RootElement.Clone(), p.BuiltInHash)).ToArray(),
            DataSetBindingTemplates: bindingTemplates.Select(t => new BackupDataSetBindingTemplate(
                t.Id, t.DocumentTypeId, t.Name, t.TargetFieldKey, t.ColumnMappings,
                t.SortOrder, t.CreatedAt, t.UpdatedAt)).ToArray(),
            ReconciliationAliases: aliases.Select(a => new BackupReconciliationAlias(
                a.Id, a.AliasKey, a.AliasLabel, a.CanonicalKey, a.CanonicalLabel,
                a.Status.ToString(), a.Note, a.ProposedBy, a.ConfirmedBy,
                a.CreatedAt, a.UpdatedAt)).ToArray(),
            DataSetProcessingTemplates: processingTemplates.Select(t => new BackupDataSetProcessingTemplate(
                t.Id, t.Name, t.SheetOrPath, t.ColumnExpressions,
                t.RowFilter, t.ComputedColumns, t.SortSpec,
                t.CreatedAt, t.UpdatedAt)).ToArray(),
            QualityDocuments: qualityDocuments.Select(q => new BackupQualityDocument(
                q.Id, q.DocumentTypeId, q.DisplayName, q.Requisites.RootElement.Clone(),
                q.Scope.ToString(), q.ScopeId, q.Source.ToString(), q.SourceUrl,
                q.ScanBlobPath, q.ScanFileName, q.ScanMimeType,
                q.CreatedAt, q.UpdatedAt)).ToArray(),
            IncludesProjectData: full,
            Constructions: full ? constructions.Select(c => new BackupConstruction(
                c.Id, c.Name, c.CreatedByUserId, c.ProfileObjectId, c.CreatedAt, c.UpdatedAt)).ToArray() : null,
            Sections: full ? sections.Select(x => new BackupSection(
                x.Id, x.ConstructionId, x.Name, x.ProfileObjectId, x.CreatedAt, x.UpdatedAt)).ToArray() : null,
            DocumentSets: full ? sets.Select(x => new BackupDocumentSet(
                x.Id, x.SectionId, x.Name, x.ProfileObjectId, x.CreatedAt, x.UpdatedAt)).ToArray() : null,
            Documents: full ? documents.Select(o => new BackupDocument(
                o.Id, o.ScopeId ?? Guid.Empty, o.CompositeTypeId, o.DisplayName, o.Data.RootElement.Clone(),
                o.Aliases.ToArray(), o.Facet!.Status.ToString(), o.Facet.SortOrder,
                o.Facet.TemplateId, o.Facet.TemplateIds, o.Facet.TemplateParams,
                o.Facet.PluginData.RootElement.Clone(),
                (filesByObject.TryGetValue(o.Id, out var gf) ? gf : [])
                    .Select(f => new BackupGeneratedFile(
                        f.Id, f.Format.ToString(), f.BlobPath, f.TemplateId, f.CreatedAt, f.UpdatedAt))
                    .ToArray(),
                o.CreatedAt, o.UpdatedAt)).ToArray() : null,
            DataSetFiles: full ? dataSetFiles.Select(f => new BackupDataSetFile(
                f.Id, f.Name, f.Format.ToString(), f.BlobPath, f.Scope.ToString(), f.ScopeId,
                f.PreprocessingProfile, f.Grouping, f.InvoiceRawData, f.RecognitionProfiles,
                f.CreatedAt, f.UpdatedAt)).ToArray() : null,
            DataSetSources: full ? dataSetSources.Select(x => new BackupDataSetSource(
                x.Id, x.FileId, x.Name, x.SheetOrPath, x.ColumnExpressions,
                x.CachedSchema, x.CachedRowCount, x.CachedData, x.Tags,
                x.RowFilter, x.ComputedColumns, x.SortSpec, x.StaleReason?.ToString(),
                x.MaterializeTypeId, x.MaterializeMapping, x.MaterializeDiscriminator,
                x.MaterializeByIdColumn, x.CreatedAt, x.UpdatedAt)).ToArray() : null,
            DataSetBindings: full ? dataSetBindings.Select(b => new BackupDataSetBinding(
                b.Id, b.OwnerId, b.SourceId, b.TargetFieldKey, b.Mapping,
                b.CreatedAt, b.UpdatedAt)).ToArray() : null,
            Reconciliations: full ? reconciliations.Select(r => new BackupReconciliationDefinition(
                r.Id, r.Name, r.Scope.ToString(), r.ScopeId, r.Spec.RootElement.Clone(),
                r.CreatedAt, r.UpdatedAt)).ToArray() : null,
            MaterialQualityLinks: full ? materialLinks.Select(l => new BackupMaterialQualityLink(
                l.Id, l.Scope.ToString(), l.ScopeId, l.MaterialKey, l.MaterialLabel,
                l.QualityDocumentId, l.CreatedAt, l.UpdatedAt)).ToArray() : null);
    }

    // ── Import ────────────────────────────────────────────────────────────────

    public async Task<RestoreReport> ImportAsync(Stream zipStream, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new ConflictException("Файл не является резервной копией BHS.CRG (отсутствует manifest.json).");

        BackupManifest manifest;
        // Читаем прямо из записи архива, без промежуточного MemoryStream: манифест несёт картинки
        // в base64, на рабочей системе это сотни мегабайт, и лишняя копия целиком в куче больших
        // объектов ничего не давала — разбор и так идёт вперёд по потоку (issue #831).
        await using (var es = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(es, JsonOptions, ct)
                       ?? throw new ConflictException("Не удалось прочитать manifest.json.");

        string? conversionNotice = null;
        var warnings = new List<string>();
        var restoredBlobPaths = new HashSet<string>(StringComparer.Ordinal);

        if (manifest.SchemaVersion > CurrentSchemaVersion)
            warnings.Add($"Резервная копия создана в более новой версии системы (schema v{manifest.SchemaVersion}). Часть данных могла быть пропущена.");
        else if (manifest.SchemaVersion < CurrentSchemaVersion)
            throw new ConflictException(
                $"Резервная копия создана в старом формате (schema v{manifest.SchemaVersion}) и несовместима с текущей версией " +
                $"после унификации объектов (issue #84). Восстановление такой копии невозможно.");

        // Restore blobs first (before DB, so references are valid on use)
        var blobEntries = zip.Entries.Where(e => e.FullName.StartsWith("blobs/", StringComparison.OrdinalIgnoreCase)).ToList();
        int blobsRestored = 0;
        foreach (var entry in blobEntries)
        {
            var blobPath = entry.FullName["blobs/".Length..];
            if (string.IsNullOrEmpty(blobPath)) continue;
            try
            {
                var contentType = GetContentTypeFromExtension(Path.GetExtension(blobPath));
                using var entryMs = new MemoryStream();
                await using (var es = entry.Open())
                    await es.CopyToAsync(entryMs, ct);
                entryMs.Position = 0;
                await blob.PutAsync(blobPath, entryMs, contentType, ct);
                restoredBlobPaths.Add(blobPath);
                blobsRestored++;
            }
            catch (Exception ex)
            {
                warnings.Add($"Не удалось восстановить файл «{blobPath}»: {ex.Message}");
            }
        }

        if (blobEntries.Count > 0)
            warnings.Insert(0, $"Файлы: восстановлено {blobsRestored} из {blobEntries.Count}.");


        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var stats = new RestoreStats();
            await RestorePrimitiveTypesAsync(manifest.PrimitiveTypes ?? [], stats, warnings, ct);
            await RestoreEnumTypesAsync(manifest.EnumTypes ?? [], stats, warnings, ct);
            await RestoreRecognitionProfilesAsync(manifest.RecognitionProfiles ?? [], stats, warnings, ct);
            await RestoreDocumentTypesAsync(manifest.DocumentTypes, stats, warnings, ct);
            await RestoreTemplatesAsync(manifest.Templates, stats, warnings, ct);
            await RestoreTemplateAssetsAsync(manifest.TemplateAssets ?? [], stats, warnings, ct);
            await RestoreTypstUserLibAsync(manifest.TypstUserLib, stats, ct);
            await RestoreTypstUserLibFilesAsync(manifest.TypstUserLibFiles, stats, ct);
            await RestoreCatalogEntitiesAsync(manifest.CatalogEntities, stats, warnings, ct);
            // Носители областей — ДО общих данных и документов качества (issue #833). Порядок тут
            // не вкусовой: запись уровня стройки восстановиться не может, пока стройки нет, и
            // прежде она молча пропускалась с предупреждением «относится к стройке, которой нет».
            // Предупреждение исчезает само, когда носители в копии есть; у старой копии их нет —
            // и поведение остаётся прежним.
            await RestoreConstructionsAsync(manifest.Constructions ?? [], stats, ct);
            await RestoreSectionsAsync(manifest.Sections ?? [], stats, warnings, ct);
            await RestoreDocumentSetsAsync(manifest.DocumentSets ?? [], stats, warnings, ct);
            await RestoreCommonDataEntriesAsync(manifest.CommonDataEntries, stats, warnings, ct);
            // Документы комплектов — после типов (тип документа) и после комплектов (носитель).
            await RestoreDocumentsAsync(manifest.Documents ?? [], stats, warnings, ct);
            // После типов документов: шаблон маппинга висит на типе и без него бессмыслен.
            await RestoreDataSetBindingTemplatesAsync(manifest.DataSetBindingTemplates ?? [], stats, warnings, ct);
            // Зависимостей нет вовсе — место в порядке произвольно.
            await RestoreReconciliationAliasesAsync(manifest.ReconciliationAliases ?? [], stats, warnings, ct);
            await RestoreDataSetProcessingTemplatesAsync(manifest.DataSetProcessingTemplates ?? [], stats, ct);
            // После типов документов: подтип сертификата — обычный тип, и без него документ качества
            // не показать.
            await RestoreQualityDocumentsAsync(
                manifest.QualityDocuments ?? [], restoredBlobPaths,
                manifest.IncludesProjectData == true, stats, warnings, ct);
            // Наборы данных: файл → источники → привязки. Привязка адресует и источник, и объект-
            // владельца, поэтому идёт последней из трёх и после документов с общими данными.
            await RestoreDataSetFilesAsync(manifest.DataSetFiles ?? [], stats, warnings, ct);
            await RestoreDataSetSourcesAsync(manifest.DataSetSources ?? [], stats, warnings, ct);
            await RestoreDataSetBindingsAsync(manifest.DataSetBindings ?? [], stats, warnings, ct);
            // Определение сверки адресует источники по идентификатору — только после них.
            await RestoreReconciliationsAsync(manifest.Reconciliations ?? [], stats, warnings, ct);
            // Связка «материал ↔ документ качества» — после самих документов качества.
            await RestoreMaterialQualityLinksAsync(manifest.MaterialQualityLinks ?? [], stats, warnings, ct);
            await tx.CommitAsync(ct);

            return new RestoreReport(true, conversionNotice, warnings,
                stats.DocumentTypesCreated, stats.DocumentTypesUpdated,
                stats.TemplatesCreated, stats.TemplatesUpdated,
                stats.CatalogEntitiesCreated, stats.CatalogEntitiesUpdated,
                stats.CommonDataEntriesCreated, stats.CommonDataEntriesUpdated,
                stats.PrimitiveTypesCreated, stats.PrimitiveTypesUpdated,
                stats.EnumTypesCreated, stats.EnumTypesUpdated,
                stats.TemplateAssetsCreated, stats.TemplateAssetsUpdated,
                stats.TypstUserLibRestored,
                stats.TypstUserLibFilesRestored,
                stats.RecognitionProfilesCreated, stats.RecognitionProfilesUpdated,
                stats.DataSetBindingTemplatesCreated, stats.DataSetBindingTemplatesUpdated,
                stats.ReconciliationAliasesCreated, stats.ReconciliationAliasesUpdated,
                stats.DataSetProcessingTemplatesCreated, stats.DataSetProcessingTemplatesUpdated,
                stats.QualityDocumentsCreated, stats.QualityDocumentsUpdated,
                stats.ProjectSections());
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            warnings.Insert(0, $"Ошибка восстановления БД: {ex.Message}");
            // Файлы пишутся ДО транзакции (ссылки должны быть валидны к моменту использования) и
            // откатом не снимаются. Удалять их здесь нельзя: путь мог совпасть с уже существующим
            // файлом, и «компенсация» уничтожила бы чужие данные. Поэтому говорим прямо.
            if (blobsRestored > 0)
                warnings.Insert(1,
                    $"В хранилище остались {blobsRestored} файлов из копии: запись файлов идёт до " +
                    "транзакции БД и откатом не отменяется. Повторное восстановление перезапишет их " +
                    "теми же данными — удалять вручную не требуется.");
            return new RestoreReport(false, conversionNotice, warnings, 0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    // ── Ссылки на документы ───────────────────────────────────────────────────

    /// <summary>
    /// Идентификаторы ДОКУМЕНТОВ, на которые ссылается запись общих данных.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Документы в копию не входят осознанно. Но запись общих данных может нести <c>_baseRef</c> на
    /// документ (наследование реквизитов, issue #71) или <c>$ref</c> вида «document»/«instance»
    /// внутри реквизитов.
    /// </para>
    /// <para>
    /// Оборванная ссылка ничего не ломает сразу: при генерации резолвер просто вернёт собственные
    /// данные объекта — без ошибки, без предупреждения и без унаследованных полей. То есть дефект
    /// проявится далеко от восстановления, в неверном PDF, и связать одно с другим будет уже нечем.
    /// Поэтому собираем адреса и проверяем их наличие в БД.
    /// </para>
    /// <para>
    /// Читаем строки ТОЛЬКО убедившись, что это строки: <c>Data</c> — произвольный пользовательский
    /// JSON, и поле с именем <c>$ref</c> или <c>_baseRef.kind</c> нестрокового вида уронило бы
    /// <c>GetString()</c>, а с ним и всё восстановление. Так же осторожничают и остальные читатели
    /// ссылок в коде.
    /// </para>
    /// </remarks>
    private static void CollectReferencedDocumentIds(JsonElement element, HashSet<Guid> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // Наследование от базового экземпляра: kind = "instance" означает документ.
                if (element.TryGetProperty("_baseRef", out var baseRef) &&
                    baseRef.ValueKind == JsonValueKind.Object &&
                    baseRef.TryGetProperty("kind", out var kind) &&
                    kind.ValueKind == JsonValueKind.String && kind.GetString() == "instance" &&
                    baseRef.TryGetProperty("id", out var baseId) &&
                    baseId.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(baseId.GetString(), out var baseGuid))
                {
                    ids.Add(baseGuid);
                }
                // Протягивание поля из реквизитов другого документа.
                if (element.TryGetProperty("$ref", out var refType) &&
                    refType.ValueKind == JsonValueKind.String &&
                    refType.GetString() is "document" or "instance" &&
                    element.TryGetProperty("instanceId", out var instId) &&
                    instId.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(instId.GetString(), out var instGuid))
                {
                    ids.Add(instGuid);
                }
                foreach (var prop in element.EnumerateObject())
                    CollectReferencedDocumentIds(prop.Value, ids);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectReferencedDocumentIds(item, ids);
                break;
        }
    }

    /// <summary>«1 запись», «2 записи», «5 записей» — счёт в предупреждениях читает человек.</summary>
    private static string Records(int n)
    {
        var tens = n % 100;
        if (tens is >= 11 and <= 14) return $"{n} записей";
        return (n % 10) switch
        {
            1 => $"{n} запись",
            2 or 3 or 4 => $"{n} записи",
            _ => $"{n} записей",
        };
    }

    /// <summary>Согласование сказуемого со счётом: «1 запись ссылается», «2 записи ссылаются».</summary>
    private static string Agree(int n, string singular, string plural) =>
        n % 10 == 1 && n % 100 != 11 ? singular : plural;

    /// <summary>
    /// Тот же счёт, но в родительном падеже — для предлогов, которые его требуют: «у 1 записи»,
    /// «у 2 записей». Именительный <see cref="Records" /> после «у» даёт «у 1 запись».
    /// </summary>
    private static string RecordsGenitive(int n) =>
        n % 10 == 1 && n % 100 != 11 ? $"{n} записи" : $"{n} записей";

    /// <summary>
    /// Служебные заголовки zip на одну запись: локальный заголовок (30 байт) и запись в каталоге
    /// (46), причём имя файла лежит в обоих — отсюда удвоение.
    ///
    /// Круглой константы «сотня байт на запись» тут мало: пути блобов длинные, а имена файлов
    /// кириллические, то есть в UTF-8 вдвое длиннее видимых. На рабочей базе (45 файлов) такая
    /// константа занижала оценку почти на 6 КБ; формула по длине имени сошлась с настоящим архивом
    /// с точностью до десятков байт.
    /// </summary>
    private static long EntryOverhead(string entryName) => 76 + 2L * Encoding.UTF8.GetByteCount(entryName);

    // ── Blob path extraction ──────────────────────────────────────────────────

    private static HashSet<string> ExtractBlobPaths(BackupManifest manifest)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in manifest.CommonDataEntries)
            CollectBlobPaths(e.Data, paths);
        foreach (var e in manifest.CatalogEntities)
            CollectBlobPaths(e.Data, paths);
        // Файлы ассетов шаблонов (issue #403) — графика/шрифты в blob-хранилище.
        foreach (var a in manifest.TemplateAssets ?? [])
            if (!string.IsNullOrEmpty(a.BlobPath)) paths.Add(a.BlobPath);
        // Сканы документов качества (issue #687). Скан — не иллюстрация к документу, а он сам:
        // библиотека без сканов не подтверждает ничего, и переносить её метаданными было бы
        // переносом пустых карточек. Реквизиты обходим тем же сборщиком — там могут лежать
        // вложения (тот же формат, что у реквизитов экземпляра документа).
        foreach (var q in manifest.QualityDocuments ?? [])
        {
            if (!string.IsNullOrEmpty(q.ScanBlobPath)) paths.Add(q.ScanBlobPath);
            CollectBlobPaths(q.Requisites, paths);
        }
        // Проектные данные (issue #833).
        foreach (var d in manifest.Documents ?? [])
        {
            CollectBlobPaths(d.Data, paths);
            foreach (var f in d.GeneratedFiles) paths.Add(f.BlobPath);
        }
        // Файл набора данных - то самое сырьё, из которого документы и собираются. У системных
        // наборов блоба нет вовсе: их сырьё - данные самой системы, а в BlobPath лежит сентинел.
        foreach (var f in manifest.DataSetFiles ?? [])
            if (!string.IsNullOrEmpty(f.BlobPath) && f.Format != "System") paths.Add(f.BlobPath);
        return paths;
    }

    private static void CollectBlobPaths(JsonElement element, HashSet<string> paths)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("$type", out var typeEl) &&
                    typeEl.ValueKind == JsonValueKind.String &&
                    typeEl.GetString() is "file" or "image" &&
                    element.TryGetProperty("blobPath", out var pathEl) &&
                    pathEl.GetString() is { Length: > 0 } path)
                {
                    paths.Add(path);
                    // И ОРИГИНАЛ картинки, если он есть (issue #534). Уменьшение — производная, и
                    // всё обещание «оригинал сохранён» держится на этом блобе; без него
                    // восстановление из архива оставило бы ссылку на несуществующий файл.
                    if (element.TryGetProperty("originalBlobPath", out var origEl) &&
                        origEl.GetString() is { Length: > 0 } original)
                    {
                        paths.Add(original);
                    }
                }
                else
                {
                    foreach (var prop in element.EnumerateObject())
                        CollectBlobPaths(prop.Value, paths);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectBlobPaths(item, paths);
                break;
        }
    }

    private static string GetContentTypeFromExtension(string ext) =>
        ext.ToLowerInvariant().TrimStart('.') switch
        {
            "pdf"  => "application/pdf",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "xls"  => "application/vnd.ms-excel",
            "png"  => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif"  => "image/gif",
            "webp" => "image/webp",
            "svg"  => "image/svg+xml",
            "ttf"  => "font/ttf",
            "otf"  => "font/otf",
            "ttc"  => "font/collection",
            _ => "application/octet-stream",
        };

    // ── Restore helpers ───────────────────────────────────────────────────────

    private async Task RestorePrimitiveTypesAsync(
        BackupPrimitiveType[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        var existingIds = await db.PrimitiveTypes.Select(e => e.Id).ToHashSetAsync(ct);
        foreach (var item in items)
        {
            var entity = PrimitiveType.Restore(
                item.Id, item.Name, item.Code, item.BaseType, item.Description,
                JsonDocument.Parse(item.Constraints.GetRawText()),
                item.CreatedAt, item.UpdatedAt, group: item.Group);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.PrimitiveTypesUpdated++; else stats.PrimitiveTypesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RestoreEnumTypesAsync(
        BackupEnumType[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        var existingIds = await db.EnumTypes.Select(e => e.Id).ToHashSetAsync(ct);
        foreach (var item in items)
        {
            var entity = EnumType.Restore(
                item.Id, item.Name, item.Code, item.Description,
                JsonDocument.Parse(item.Values.GetRawText()),
                item.CreatedAt, item.UpdatedAt, item.Group);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.EnumTypesUpdated++; else stats.EnumTypesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RestoreRecognitionProfilesAsync(
        BackupRecognitionProfile[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        var existingIds = await db.RecognitionProfiles.Select(e => e.Id).ToHashSetAsync(ct);
        var skippedBuiltIn = 0;
        foreach (var item in items)
        {
            if (!Enum.TryParse<RecognitionProfileKind>(item.Kind, out var kind))
            {
                warnings.Add($"Профиль распознавания «{item.Name}»: неизвестный вид «{item.Kind}», пропущен.");
                continue;
            }
            // Ловушка машины времени: копия несёт ЗАВОДСКОЙ профиль в старой редакции и при
            // восстановлении в более новую версию затёрла бы улучшенный дефолт. Нетронутые встроенные
            // пропускаем — их переутвердит сидер при старте; восстанавливаем только правленные
            // пользователем (в них есть что терять) и полностью пользовательские профили.
            if (item is { IsBuiltIn: true, IsModified: false })
            {
                skippedBuiltIn++;
                continue;
            }
            var entity = RecognitionProfile.Restore(
                item.Id, item.Name, item.Code, kind,
                JsonDocument.Parse(item.Fields.GetRawText()),
                item.RowColumns is { } rc ? JsonDocument.Parse(rc.GetRawText()) : null,
                item.Shape is { } sh ? JsonDocument.Parse(sh.GetRawText()) : null,
                item.IsBuiltIn, item.IsModified, item.BuiltInHash, builtInOutdated: false,
                item.CreatedAt, item.UpdatedAt);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.RecognitionProfilesUpdated++; else stats.RecognitionProfilesCreated++;
        }
        if (skippedBuiltIn > 0)
            warnings.Add($"Профили распознавания: {skippedBuiltIn} встроенных пропущено (не правились) — " +
                         "они переутверждаются системой при старте, чтобы копия не откатила их к старой редакции.");
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RestoreTemplateAssetsAsync(
        BackupTemplateAsset[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        var existingIds = await db.TemplateAssets.Select(e => e.Id).ToHashSetAsync(ct);
        // scopeId ссылается на шаблон/тип документа (для System — null); проверяем валидность ссылки,
        // чтобы не оставить осиротевший ассет (зеркалим защиту из RestoreTemplatesAsync).
        var validTemplateIds = await db.Templates.Select(e => e.Id).ToHashSetAsync(ct);
        var validDocTypeIds = await db.DocumentTypes.Select(e => e.Id).ToHashSetAsync(ct);
        foreach (var item in items)
        {
            if (!Enum.TryParse<TemplateAssetScope>(item.Scope, out var scope))
            {
                warnings.Add($"Ассет шаблона «{item.Name}»: неизвестная область «{item.Scope}», пропущен.");
                continue;
            }
            if (!Enum.TryParse<TemplateAssetKind>(item.Kind, out var kind))
            {
                warnings.Add($"Ассет шаблона «{item.Name}»: неизвестный вид «{item.Kind}», пропущен.");
                continue;
            }
            var scopeOk = scope switch
            {
                TemplateAssetScope.Template => item.ScopeId is { } sid && validTemplateIds.Contains(sid),
                TemplateAssetScope.DocumentType => item.ScopeId is { } sid && validDocTypeIds.Contains(sid),
                _ => true, // System — scopeId == null
            };
            if (!scopeOk)
            {
                warnings.Add($"Ассет шаблона «{item.Name}»: цель области ({item.Scope} {item.ScopeId}) не найдена, пропущен.");
                continue;
            }
            var entity = TemplateAsset.Restore(
                item.Id, scope, item.ScopeId, kind,
                item.Name, item.FileName, item.MimeType, item.BlobPath, item.FontFamilyName,
                item.CreatedAt, item.UpdatedAt);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.TemplateAssetsUpdated++; else stats.TemplateAssetsCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RestoreTypstUserLibAsync(
        BackupTypstUserLib? item, RestoreStats stats, CancellationToken ct)
    {
        if (item is null) return; // старый бэкап без userlib — нечего восстанавливать
        var existing = await db.TypstUserLibs.FirstOrDefaultAsync(l => l.Id == TypstUserLib.SingletonId, ct);
        if (existing is not null)
            existing.UpdateContent(item.Content);
        else
            db.TypstUserLibs.Add(TypstUserLib.Restore(item.Content, item.CreatedAt, item.UpdatedAt));
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        stats.TypstUserLibRestored = true;
    }

    /// <summary>
    /// Дерево библиотеки (issue #473). Восстановление ЗАМЕЩАЮЩЕЕ: дерево — единое целое, и оставить
    /// на целевой системе файлы, которых в копии нет, значит собрать библиотеку, которой никогда не
    /// существовало (лишний файл переопределил бы одноимённую функцию молча).
    /// </summary>
    private async Task RestoreTypstUserLibFilesAsync(
        IReadOnlyList<BackupTypstUserLibFile>? items, RestoreStats stats, CancellationToken ct)
    {
        if (items is null) return; // бэкап предыдущей версии — секции просто нет, дерево не трогаем

        var existing = await db.TypstUserLibFiles.ToListAsync(ct);
        db.TypstUserLibFiles.RemoveRange(existing);
        foreach (var item in items)
            db.TypstUserLibFiles.Add(TypstUserLibFile.Restore(
                item.Id, item.Path, item.Content, item.CreatedAt, item.UpdatedAt));

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        stats.TypstUserLibFilesRestored = items.Count;
    }

    private async Task RestoreDocumentTypesAsync(
        BackupDocumentType[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        var existingIds = await db.DocumentTypes.Select(e => e.Id).ToHashSetAsync(ct);
        var sorted = TopologicalSortDocTypes(items);
        foreach (var item in sorted)
        {
            if (!Enum.TryParse<DocumentTypeKind>(item.Kind, out var kind))
            {
                warnings.Add($"Тип документа «{item.Name}»: неизвестный вид «{item.Kind}», пропущен.");
                continue;
            }
            var entity = DocumentType.Restore(
                item.Id, item.Name, item.Code, kind, item.ParentId,
                JsonDocument.Parse(item.Schema.GetRawText()),
                JsonDocument.Parse(item.PluginBindings.GetRawText()),
                item.IsAbstract, item.CreatedAt, item.UpdatedAt, item.Group, item.AllowsProxy);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.DocumentTypesUpdated++; else stats.DocumentTypesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RestoreTemplatesAsync(
        BackupTemplate[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        var existingIds = await db.Templates.Select(e => e.Id).ToHashSetAsync(ct);
        var validDocTypeIds = await db.DocumentTypes.Select(e => e.Id).ToHashSetAsync(ct);
        foreach (var item in items)
        {
            if (!validDocTypeIds.Contains(item.DocumentTypeId))
            {
                warnings.Add($"Шаблон «{item.Name}» v{item.Version}: тип документа {item.DocumentTypeId} не найден, пропущен.");
                continue;
            }
            var entity = Template.Restore(
                item.Id, item.DocumentTypeId, item.Name, item.Content, item.Version,
                item.IsActive, item.IsDefault,
                item.CreatedAt, item.UpdatedAt, item.Parameters, item.Comment);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.TemplatesUpdated++; else stats.TemplatesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RestoreCatalogEntitiesAsync(
        BackupCatalogEntity[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        var existingIds = await db.CatalogEntities.Select(e => e.Id).ToHashSetAsync(ct);
        foreach (var item in items)
        {
            var entity = CatalogEntity.Restore(
                item.Id, item.EntityType, item.DisplayName,
                JsonDocument.Parse(item.Data.GetRawText()),
                item.OwnerId, item.CreatedAt, item.UpdatedAt);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.CatalogEntitiesUpdated++; else stats.CatalogEntitiesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RestoreCommonDataEntriesAsync(
        BackupCommonDataEntry[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        // Общие данные восстанавливаем как DomainObject без документной фасеты (issue #84).
        var existingIds = await db.DomainObjects.Select(e => e.Id).ToHashSetAsync(ct);
        var validDocTypeIds = await db.DocumentTypes.Select(e => e.Id).ToHashSetAsync(ct);

        // Носители областей, на которые запись может ссылаться. Стройки, разделы и комплекты в копию
        // не входят осознанно — значит на чистой системе таких носителей нет вовсе.
        var setIds = await db.DocumentSets.Select(e => e.Id).ToHashSetAsync(ct);
        var sectionIds = await db.Sections.Select(e => e.Id).ToHashSetAsync(ct);
        var constructionIds = await db.Constructions.Select(e => e.Id).ToHashSetAsync(ct);
        var orphanedByScope = 0;

        // Ссылки на документы: собираем адреса у ВОССТАНОВЛЕННЫХ записей (пропущенные считать
        // незачем — их в системе не будет) и проверяем наличие адресатов в БД. Без проверки
        // предупреждение кричало бы и в самом обычном случае — восстановлении в живую систему, где
        // все документы на месте и все ссылки разрешаются.
        var referencedDocumentIds = new HashSet<Guid>();
        var entryIdsByDocument = new Dictionary<Guid, List<Guid>>();

        foreach (var item in items)
        {
            if (!validDocTypeIds.Contains(item.CompositeTypeId))
            {
                warnings.Add($"Общие данные «{item.DisplayName}»: тип {item.CompositeTypeId} не найден, пропущен.");
                continue;
            }
            if (!Enum.TryParse<CatalogScope>(item.Scope, out var scope))
            {
                warnings.Add($"Общие данные «{item.DisplayName}»: неизвестная область «{item.Scope}», пропущена.");
                continue;
            }
            // Запись привязана к комплекту/разделу/стройке, которых в этой системе нет. НЕ пропускаем:
            // это пользовательские данные, и потерять их при восстановлении хуже, чем внести
            // невидимыми — выборки фильтруют по паре «область + носитель», так что в интерфейсе их
            // не будет, пока носитель не появится. Но и молчать об этом нельзя: отчёт называл бы их
            // успешно восстановленными.
            if (!ScopeCarrierExists(scope, item.ScopeId, setIds, sectionIds, constructionIds))
                orphanedByScope++;

            var refs = new HashSet<Guid>();
            CollectReferencedDocumentIds(item.Data, refs);
            foreach (var refId in refs)
            {
                referencedDocumentIds.Add(refId);
                if (!entryIdsByDocument.TryGetValue(refId, out var list))
                    entryIdsByDocument[refId] = list = [];
                list.Add(item.Id);
            }

            var entity = DomainObject.Restore(
                item.Id, item.CompositeTypeId, item.DisplayName,
                JsonDocument.Parse(item.Data.GetRawText()),
                scope, item.ScopeId, item.CreatedAt, item.UpdatedAt, item.Aliases);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.CommonDataEntriesUpdated++; else stats.CommonDataEntriesCreated++;
        }

        if (orphanedByScope > 0)
        {
            warnings.Add(
                $"Общие данные: {Records(orphanedByScope)} " +
                $"{Agree(orphanedByScope, "относится", "относятся")} к комплектам, разделам или стройкам, " +
                "которых в этой системе нет. Они восстановлены, но в интерфейсе не появятся, пока не " +
                "будут созданы соответствующие объекты (проектные данные в резервную копию не входят).");
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        // Наличие адресатов проверяем ПОСЛЕ записи: документы могли приехать не из копии, а уже быть
        // в системе — при восстановлении в живую установку это обычное дело, и молчать тут правильно.
        if (referencedDocumentIds.Count > 0)
        {
            var presentIds = await db.DomainObjects
                .Where(o => referencedDocumentIds.Contains(o.Id))
                .Select(o => o.Id)
                .ToHashSetAsync(ct);

            var affectedEntries = entryIdsByDocument
                .Where(kv => !presentIds.Contains(kv.Key))
                .SelectMany(kv => kv.Value)
                .ToHashSet();

            if (affectedEntries.Count > 0)
                warnings.Add(
                    $"Общие данные: {Records(affectedEntries.Count)} " +
                    $"{Agree(affectedEntries.Count, "ссылается", "ссылаются")} на документы, которых в " +
                    "этой системе нет (документы в резервную копию не входят). При генерации " +
                    "унаследованные от них поля подставлены не будут — молча, поэтому проверьте такие записи.");
        }
    }

    /// <summary>Существует ли объект, к области которого привязана запись общих данных.</summary>
    private static bool ScopeCarrierExists(
        CatalogScope scope, Guid? scopeId,
        HashSet<Guid> setIds, HashSet<Guid> sectionIds, HashSet<Guid> constructionIds) => scope switch
    {
        // Системный уровень носителя не имеет — он и есть «вся система».
        CatalogScope.System => true,
        _ when scopeId is null => false,
        CatalogScope.Set => setIds.Contains(scopeId.Value),
        CatalogScope.Section => sectionIds.Contains(scopeId.Value),
        CatalogScope.Construction => constructionIds.Contains(scopeId.Value),
        _ => true,
    };

    // ── Topological sort ──────────────────────────────────────────────────────

    private static BackupDocumentType[] TopologicalSortDocTypes(BackupDocumentType[] items)
    {
        var result = new List<BackupDocumentType>(items.Length);
        var remaining = items.ToHashSet();
        var addedIds = new HashSet<Guid>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(x => x.ParentId == null || addedIds.Contains(x.ParentId.Value)).ToList();
            if (ready.Count == 0) { result.AddRange(remaining); break; }
            foreach (var r in ready) { result.Add(r); addedIds.Add(r.Id); remaining.Remove(r); }
        }
        return [.. result];
    }

    private async Task RestoreDataSetBindingTemplatesAsync(
        BackupDataSetBindingTemplate[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existingIds = await db.DataSetBindingTemplates.Select(e => e.Id).ToHashSetAsync(ct);
        // Та же проверка, что у шаблонов документов: шаблон маппинга к несуществующему типу
        // восстановился бы записью, которую никогда не видно, а отчёт назвал бы её успешной.
        var validDocTypeIds = await db.DocumentTypes.Select(e => e.Id).ToHashSetAsync(ct);
        foreach (var item in items)
        {
            if (!validDocTypeIds.Contains(item.DocumentTypeId))
            {
                warnings.Add($"Шаблон маппинга «{item.Name}»: тип документа {item.DocumentTypeId} не найден, пропущен.");
                continue;
            }
            var entity = DataSetBindingTemplate.Restore(
                item.Id, item.DocumentTypeId, item.Name, item.TargetFieldKey, item.ColumnMappings,
                item.SortOrder, item.CreatedAt, item.UpdatedAt);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.DataSetBindingTemplatesUpdated++;
            else stats.DataSetBindingTemplatesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    private async Task RestoreReconciliationAliasesAsync(
        BackupReconciliationAlias[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;

        var valid = new List<(BackupReconciliationAlias Item, AliasStatus Status)>();
        foreach (var item in items)
        {
            if (Enum.TryParse<AliasStatus>(item.Status, out var status)) valid.Add((item, status));
            else warnings.Add($"Алиас «{item.AliasLabel}» → «{item.CanonicalLabel}»: неизвестный статус «{item.Status}», пропущен.");
        }
        if (valid.Count == 0) return;

        // Тождество алиаса — КЛЮЧ, а не идентификатор: на нём стоит уникальный индекс, и так же
        // считает путь записи в приложении (повторное предложение по тому же ключу правит запись,
        // а не плодит вторую). Upsert по Id разошёлся бы с этим на самом обычном сценарии: на
        // целевой системе предложения родились заново, с другими Id, но с теми же ключами —
        // вставка упала бы на индексе, а восстановление идёт одной транзакцией, то есть вместе с
        // алиасами откатились бы и типы, и шаблоны, и каталог.
        //
        // Поэтому конфликтующие записи (по ключу ИЛИ по идентификатору) сначала удаляем, и удаление
        // отправляем в БД ОТДЕЛЬНЫМ сохранением: иначе вставка и удаление уехали бы одним пакетом,
        // и уникальный индекс успел бы сработать на промежуточном состоянии.
        var keys = valid.Select(v => v.Item.AliasKey).ToHashSet(StringComparer.Ordinal);
        var ids = valid.Select(v => v.Item.Id).ToHashSet();
        var conflicting = await db.ReconciliationAliases
            .Where(a => keys.Contains(a.AliasKey) || ids.Contains(a.Id))
            .ToListAsync(ct);
        var replacedKeys = conflicting.Select(a => a.AliasKey).ToHashSet(StringComparer.Ordinal);

        if (conflicting.Count > 0)
        {
            db.ReconciliationAliases.RemoveRange(conflicting);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        foreach (var (item, status) in valid)
        {
            var entity = ReconciliationAlias.Restore(
                item.Id, item.AliasKey, item.AliasLabel, item.CanonicalKey, item.CanonicalLabel,
                status, item.Note, item.ProposedBy, item.ConfirmedBy, item.CreatedAt, item.UpdatedAt);
            db.Entry(entity).State = EntityState.Added;
            if (replacedKeys.Contains(item.AliasKey)) stats.ReconciliationAliasesUpdated++;
            else stats.ReconciliationAliasesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Рецепты обработки источников (issue #687). Ни одной внешней ссылки — ни проверять, ни
    /// сортировать нечего, поэтому и предупреждений здесь не бывает.
    /// </summary>
    private async Task RestoreDataSetProcessingTemplatesAsync(
        BackupDataSetProcessingTemplate[] items, RestoreStats stats, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existingIds = await db.DataSetProcessingTemplates.Select(e => e.Id).ToHashSetAsync(ct);
        foreach (var item in items)
        {
            var entity = DataSetProcessingTemplate.Restore(
                item.Id, item.Name, item.SheetOrPath, item.ColumnExpressions,
                item.RowFilter, item.ComputedColumns, item.SortSpec,
                item.CreatedAt, item.UpdatedAt);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.DataSetProcessingTemplatesUpdated++;
            else stats.DataSetProcessingTemplatesCreated++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Библиотека документов качества со сканами (issue #687).
    /// </summary>
    /// <param name="restoredBlobPaths">
    /// Адреса файлов, реально записанных в хранилище из этого архива. Нужны, чтобы не назвать
    /// успехом карточку без скана: у документа качества скан — это сам документ, а не иллюстрация к
    /// нему, и восстановленный сертификат, чей файл в архив не попал, ничего не подтверждает. Для
    /// ассетов шаблонов такой проверки нет намеренно: отсутствие шрифта ухудшает вёрстку, но не
    /// превращает объект в неправду.
    /// </param>
    private async Task RestoreQualityDocumentsAsync(
        BackupQualityDocument[] items, HashSet<string> restoredBlobPaths,
        bool projectDataInBackup, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;

        var existingIds = await db.QualityDocuments.Select(e => e.Id).ToHashSetAsync(ct);
        var validDocTypeIds = await db.DocumentTypes.Select(e => e.Id).ToHashSetAsync(ct);

        // Носители областей — как у общих данных: комплекты, разделы и стройки в копию не входят.
        var setIds = await db.DocumentSets.Select(e => e.Id).ToHashSetAsync(ct);
        var sectionIds = await db.Sections.Select(e => e.Id).ToHashSetAsync(ct);
        var constructionIds = await db.Constructions.Select(e => e.Id).ToHashSetAsync(ct);
        var orphanedByScope = 0;
        var withoutScan = 0;
        var scanDropped = 0;
        var nameClashes = 0;

        // Скан у уже существующих карточек и занятые имена в областях — обе выборки нужны ДО записи:
        // после SaveChanges обе покажут уже восстановленное состояние.
        var liveScans = await db.QualityDocuments
            .Where(d => d.ScanBlobPath != null)
            .Select(d => d.Id)
            .ToHashSetAsync(ct);
        var liveNames = await db.QualityDocuments
            .Select(d => new { d.Id, d.DisplayName, d.Scope, d.ScopeId })
            .ToListAsync(ct);

        foreach (var item in items)
        {
            if (!validDocTypeIds.Contains(item.DocumentTypeId))
            {
                warnings.Add($"Документ качества «{item.DisplayName}»: тип {item.DocumentTypeId} не найден, пропущен.");
                continue;
            }
            if (!Enum.TryParse<CatalogScope>(item.Scope, out var scope))
            {
                warnings.Add($"Документ качества «{item.DisplayName}»: неизвестная область «{item.Scope}», пропущен.");
                continue;
            }
            if (!Enum.TryParse<QualityDocSource>(item.Source, out var source))
            {
                warnings.Add($"Документ качества «{item.DisplayName}»: неизвестный источник «{item.Source}», пропущен.");
                continue;
            }

            // Область не разрешается — не пропускаем (как и общие данные): библиотека
            // переиспользуема, и документ, подшитый к исчезнувшему комплекту, всё равно остаётся
            // сертификатом. Но и молчать нельзя — в интерфейсе его не будет видно.
            if (!ScopeCarrierExists(scope, item.ScopeId, setIds, sectionIds, constructionIds))
                orphanedByScope++;

            if (item.ScanBlobPath is { Length: > 0 } scanPath && !restoredBlobPaths.Contains(scanPath))
                withoutScan++;

            // Карточка уже есть, скан у неё есть, а копия принесла её БЕЗ скана: скан загрузили уже
            // после снятия копии. Восстановление обнулит указатель — и обещание «добавляет и
            // обновляет, но ничего не удаляет» тут перестаёт быть правдой. Данные всё равно берём из
            // копии (иначе восстановление перестанет быть восстановлением), но молчать об этом
            // нельзя: по смыслу этой библиотеки скан и есть документ.
            if (string.IsNullOrEmpty(item.ScanBlobPath) && liveScans.Contains(item.Id))
                scanDropped++;

            // Имя документа уникально в своей области (issue #588). Восстановление — единственный
            // путь записи мимо этой проверки, и обойти её здесь приходится: копия несёт состояние
            // как есть, а отказ на полпути откатил бы всю транзакцию из-за косметики. Но дубль,
            // возникший оттого, что те же сертификаты успели завести руками, в списке неразличим —
            // о нём говорим.
            if (liveNames.Any(d =>
                    d.Id != item.Id && d.Scope == scope && d.ScopeId == item.ScopeId &&
                    string.Equals(d.DisplayName.Trim(), item.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase)))
                nameClashes++;

            var entity = QualityDocument.Restore(
                item.Id, item.DocumentTypeId, item.DisplayName,
                JsonDocument.Parse(item.Requisites.GetRawText()),
                scope, item.ScopeId, source, item.SourceUrl,
                item.ScanBlobPath, item.ScanFileName, item.ScanMimeType,
                item.CreatedAt, item.UpdatedAt);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) stats.QualityDocumentsUpdated++; else stats.QualityDocumentsCreated++;
        }

        if (orphanedByScope > 0)
            warnings.Add(
                $"Документы качества: {Records(orphanedByScope)} " +
                $"{Agree(orphanedByScope, "относится", "относятся")} к комплектам, разделам или стройкам, " +
                "которых в этой системе нет. Они восстановлены, но в библиотеке не появятся, пока не " +
                "будут созданы соответствующие объекты (проектные данные в резервную копию не входят).");

        if (withoutScan > 0)
            warnings.Add(
                $"Документы качества: у {RecordsGenitive(withoutScan)} скан не восстановлен — файла не " +
                "было в архиве или его не удалось записать. Карточка документа откроется, но сам " +
                "сертификат по ней не показать: скан нужно загрузить заново.");

        if (scanDropped > 0)
            warnings.Add(
                $"Документы качества: у {RecordsGenitive(scanDropped)} скан был в этой системе, но в " +
                "копии его нет — он загружен уже после её снятия. Указатель на файл снят по копии; " +
                "сам файл в хранилище остался, но карточка на него больше не ссылается.");

        if (nameClashes > 0)
            warnings.Add(
                $"Документы качества: {Records(nameClashes)} " +
                $"{Agree(nameClashes, "совпадает", "совпадают")} по имени с уже заведёнными в той же " +
                "области. Уникальность имён (иначе в списке выбирают вслепую) при восстановлении не " +
                "проверяется — разберите такие пары вручную.");

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        // Связки с материалами (MaterialQualityLink) в КОНФИГУРАЦИОННУЮ копию не входят: они
        // адресуют материалы комплектов, а комплектов там нет. Сказать об этом надо — иначе
        // восстановивший увидит полную библиотеку и решит, что вернулась и проделанная работа по
        // привязке.
        //
        // Три условия разом, и каждое своё:
        //   • копия КОНФИГУРАЦИОННАЯ — в полной связки переносятся (issue #833), даже когда их
        //     ноль: говорить там «не переносятся» значит объявлять потерянным то, чего и не было;
        //   • связок нет и В СИСТЕМЕ — восстановление ничего не удаляет, и на самом обычном пути
        //     (админ накатывает конфигурационную копию, чтобы вернуть шаблон) все связки целы;
        //     безусловное «библиотека вернулась непривязанной» позвало бы делать заново работу,
        //     которая никуда не девалась.
        // Тем же рассуждением проверяет себя предупреждение о ссылках на документы выше.
        if (!projectDataInBackup && !await db.MaterialQualityLinks.AnyAsync(ct))
            warnings.Add(
                "Документы качества: связки с материалами копией не переносятся — они относятся к " +
                "комплектам, которых в копии нет. Библиотека восстановлена непривязанной.");
    }

    // ── Проектные данные (issue #833) ─────────────────────────────────────────

    /// <summary>
    /// Общий приём для проектных секций: то, что есть — обновить, чего нет — добавить. Ничего не
    /// удаляем, как и во всей остальной копии: запись, созданная на целевой системе после снятия
    /// копии, остаётся.
    /// </summary>
    private async Task<(int Created, int Updated)> UpsertAsync<TEntity>(
        IEnumerable<(Guid Id, TEntity Entity)> items, HashSet<Guid> existingIds, CancellationToken ct)
        where TEntity : class
    {
        int created = 0, updated = 0;
        foreach (var (id, entity) in items)
        {
            var exists = existingIds.Contains(id);
            db.Entry(entity).State = exists ? EntityState.Modified : EntityState.Added;
            if (exists) updated++; else created++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return (created, updated);
    }

    private async Task RestoreConstructionsAsync(
        BackupConstruction[] items, RestoreStats stats, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existing = await db.Constructions.Select(x => x.Id).ToHashSetAsync(ct);
        var (c, u) = await UpsertAsync(items.Select(i => (i.Id, Construction.Restore(
            i.Id, i.Name, i.CreatedByUserId, i.ProfileObjectId, i.CreatedAt, i.UpdatedAt))), existing, ct);
        stats.Count("Стройки", c, u);
    }

    private async Task RestoreSectionsAsync(
        BackupSection[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existing = await db.Sections.Select(x => x.Id).ToHashSetAsync(ct);
        var constructions = await db.Constructions.Select(x => x.Id).ToHashSetAsync(ct);

        var (ok, orphans) = Split(items, i => constructions.Contains(i.ConstructionId));
        Warn(warnings, orphans.Count, "разделов", "их стройки нет ни в копии, ни в системе");

        var (c, u) = await UpsertAsync(ok.Select(i => (i.Id, Section.Restore(
            i.Id, i.ConstructionId, i.Name, i.ProfileObjectId, i.CreatedAt, i.UpdatedAt))), existing, ct);
        stats.Count("Разделы", c, u);
    }

    private async Task RestoreDocumentSetsAsync(
        BackupDocumentSet[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existing = await db.DocumentSets.Select(x => x.Id).ToHashSetAsync(ct);
        var sections = await db.Sections.Select(x => x.Id).ToHashSetAsync(ct);

        var (ok, orphans) = Split(items, i => sections.Contains(i.SectionId));
        Warn(warnings, orphans.Count, "комплектов", "их раздела нет ни в копии, ни в системе");

        var (c, u) = await UpsertAsync(ok.Select(i => (i.Id, DocumentSet.Restore(
            i.Id, i.SectionId, i.Name, i.ProfileObjectId, i.CreatedAt, i.UpdatedAt))), existing, ct);
        stats.Count("Комплекты", c, u);
    }

    /// <summary>
    /// Документы комплектов вместе с фасетой и выпущенными файлами.
    ///
    /// Фасета выставляется отдельной записью change-tracker'а: она живёт своей строкой
    /// (<c>document_facets</c>), и состояние объекта на неё не переходит — забудь про это, и
    /// документ восстановился бы без статуса и выбранного шаблона, то есть перестал бы быть
    /// документом.
    /// </summary>
    private async Task RestoreDocumentsAsync(
        BackupDocument[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existing = await db.DomainObjects.Select(x => x.Id).ToHashSetAsync(ct);
        var facets = await db.DocumentFacets.Select(x => x.ObjectId).ToHashSetAsync(ct);
        var sets = await db.DocumentSets.Select(x => x.Id).ToHashSetAsync(ct);
        var types = await db.DocumentTypes.Select(x => x.Id).ToHashSetAsync(ct);

        var (ok, skipped) = Split(items, i => sets.Contains(i.SetId) && types.Contains(i.CompositeTypeId));
        Warn(warnings, skipped.Count, "документов", "их комплекта или типа нет ни в копии, ни в системе");

        int created = 0, updated = 0;
        foreach (var item in ok)
        {
            if (!TryParseEnum<DocumentStatus>(item.Status, "статус документа", warnings, out var status))
                continue;

            var obj = DomainObject.RestoreDocument(
                item.Id, item.CompositeTypeId, item.DisplayName,
                JsonDocument.Parse(item.Data.GetRawText()), item.SetId,
                item.CreatedAt, item.UpdatedAt, item.Aliases,
                status, item.SortOrder,
                item.TemplateId, item.TemplateIds, item.TemplateParams,
                JsonDocument.Parse(item.PluginData.GetRawText()));

            var exists = existing.Contains(item.Id);
            db.Entry(obj).State = exists ? EntityState.Modified : EntityState.Added;
            db.Entry(obj.Facet!).State = facets.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (exists) updated++; else created++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        stats.Count("Документы комплектов", created, updated);

        await RestoreGeneratedFilesAsync(ok, stats, warnings, ct);
    }

    /// <summary>
    /// Выпущенные файлы документов. Отдельным проходом после самих документов: строка ссылается на
    /// фасету, и до её появления вставка отвергается внешним ключом.
    /// </summary>
    private async Task RestoreGeneratedFilesAsync(
        IReadOnlyList<BackupDocument> documents, RestoreStats stats, List<string> warnings,
        CancellationToken ct)
    {
        var files = documents.SelectMany(d => d.GeneratedFiles.Select(f => (Document: d, File: f)))
            .Where(x => TryParseEnum<OutputFormat>(x.File.Format, "формат выпущенного файла", warnings, out _))
            .ToList();
        if (files.Count == 0) return;

        var existing = await db.GeneratedFiles.Select(x => x.Id).ToHashSetAsync(ct);
        var (c, u) = await UpsertAsync(files.Select(x => (x.File.Id, GeneratedFile.Restore(
            x.File.Id, x.Document.Id, Enum.Parse<OutputFormat>(x.File.Format), x.File.BlobPath,
            x.File.TemplateId, x.File.CreatedAt, x.File.UpdatedAt))), existing, ct);
        stats.Count("Выпущенные файлы", c, u);
    }

    private async Task RestoreDataSetFilesAsync(
        BackupDataSetFile[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existing = await db.DataSetFiles.Select(x => x.Id).ToHashSetAsync(ct);
        var carriers = await LoadScopeCarriersAsync(ct);

        var (ok, orphans) = Split(items, i =>
            Enum.TryParse<DataSetFormat>(i.Format, out _)
            && Enum.TryParse<CatalogScope>(i.Scope, out var sc)
            && ScopeCarrierExists(sc, i.ScopeId, carriers.Sets, carriers.Sections, carriers.Constructions));
        Warn(warnings, orphans.Count, "наборов данных", "их стройки, раздела или комплекта нет");

        var (c, u) = await UpsertAsync(ok.Select(i => (i.Id, DataSetFile.Restore(
            i.Id, i.Name, Enum.Parse<DataSetFormat>(i.Format), i.BlobPath,
            Enum.Parse<CatalogScope>(i.Scope), i.ScopeId, i.PreprocessingProfile, i.Grouping,
            i.InvoiceRawData, i.RecognitionProfiles, i.CreatedAt, i.UpdatedAt))), existing, ct);
        stats.Count("Наборы данных", c, u);
    }

    private async Task RestoreDataSetSourcesAsync(
        BackupDataSetSource[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existing = await db.DataSetSources.Select(x => x.Id).ToHashSetAsync(ct);
        var fileIds = await db.DataSetFiles.Select(x => x.Id).ToHashSetAsync(ct);

        var (ok, orphans) = Split(items, i => fileIds.Contains(i.FileId));
        Warn(warnings, orphans.Count, "источников данных", "их набора нет ни в копии, ни в системе");

        var (c, u) = await UpsertAsync(ok.Select(i => (i.Id, DataSetSource.Restore(
            i.Id, i.FileId, i.Name, i.SheetOrPath, i.ColumnExpressions, i.CachedSchema,
            i.CachedRowCount, i.CachedData, i.Tags, i.RowFilter, i.ComputedColumns, i.SortSpec,
            // Неизвестная причина устаревания — не повод терять источник: причина это подсказка
            // человеку, а данные в кэше от неё не зависят.
            i.StaleReason is not null && Enum.TryParse<DataSetStaleReason>(i.StaleReason, out var reason)
                ? reason : null,
            i.MaterializeTypeId, i.MaterializeMapping, i.MaterializeDiscriminator,
            i.MaterializeByIdColumn, i.CreatedAt, i.UpdatedAt))), existing, ct);
        stats.Count("Источники данных", c, u);
    }

    private async Task RestoreDataSetBindingsAsync(
        BackupDataSetBinding[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existing = await db.DataSetBindings.Select(x => x.Id).ToHashSetAsync(ct);
        var sourceIds = await db.DataSetSources.Select(x => x.Id).ToHashSetAsync(ct);
        var ownerIds = await db.DomainObjects.Select(x => x.Id).ToHashSetAsync(ct);

        var (ok, orphans) = Split(items, i => sourceIds.Contains(i.SourceId) && ownerIds.Contains(i.OwnerId));
        Warn(warnings, orphans.Count, "привязок наборов", "их источника или объекта-владельца нет");

        var (c, u) = await UpsertAsync(ok.Select(i => (i.Id, DataSetBinding.Restore(
            i.Id, i.OwnerId, i.SourceId, i.TargetFieldKey, i.Mapping, i.CreatedAt, i.UpdatedAt))),
            existing, ct);
        stats.Count("Привязки наборов", c, u);
    }

    private async Task RestoreReconciliationsAsync(
        BackupReconciliationDefinition[] items, RestoreStats stats, List<string> warnings,
        CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existing = await db.Reconciliations.Select(x => x.Id).ToHashSetAsync(ct);
        var carriers = await LoadScopeCarriersAsync(ct);

        var (ok, orphans) = Split(items, i =>
            Enum.TryParse<CatalogScope>(i.Scope, out var sc)
            && ScopeCarrierExists(sc, i.ScopeId, carriers.Sets, carriers.Sections, carriers.Constructions));
        Warn(warnings, orphans.Count, "сверок", "их стройки, раздела или комплекта нет");

        var (c, u) = await UpsertAsync(ok.Select(i => (i.Id, ReconciliationDefinition.Restore(
            i.Id, i.Name, Enum.Parse<CatalogScope>(i.Scope), i.ScopeId,
            JsonDocument.Parse(i.Spec.GetRawText()), i.CreatedAt, i.UpdatedAt))), existing, ct);
        stats.Count("Сверки", c, u);
    }

    /// <summary>Носители областей, какие есть в системе на этот момент.</summary>
    private async Task<(HashSet<Guid> Sets, HashSet<Guid> Sections, HashSet<Guid> Constructions)>
        LoadScopeCarriersAsync(CancellationToken ct) => (
            await db.DocumentSets.Select(x => x.Id).ToHashSetAsync(ct),
            await db.Sections.Select(x => x.Id).ToHashSetAsync(ct),
            await db.Constructions.Select(x => x.Id).ToHashSetAsync(ct));

    /// <summary>
    /// Связки «материал ↔ документ качества».
    ///
    /// Единственная из новых секций, которую нельзя раскладывать по одному лишь идентификатору:
    /// у таблицы есть УНИКАЛЬНЫЙ индекс по (уровень, носитель, ключ материала). Копия, снятая
    /// здесь, и связка, заведённая на целевой системе, описывают один и тот же материал разными
    /// строками — вставка по Id упёрлась бы в 23505, а он в этом коде означает не «пропустим одну
    /// строку», а откат ВСЕГО восстановления: администратор получил бы «Ошибка восстановления БД»
    /// и пустую систему. Поэтому сначала ищем по природному ключу и правим найденную строку.
    /// </summary>
    private async Task RestoreMaterialQualityLinksAsync(
        BackupMaterialQualityLink[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existing = await db.MaterialQualityLinks.AsNoTracking()
            .Select(x => new { x.Id, x.Scope, x.ScopeId, x.MaterialKey }).ToListAsync(ct);
        var byKey = existing.ToDictionary(
            x => (x.Scope, x.ScopeId, x.MaterialKey), x => x.Id);
        var byId = existing.Select(x => x.Id).ToHashSet();
        var qualityIds = await db.QualityDocuments.Select(x => x.Id).ToHashSetAsync(ct);

        var (ok, orphans) = Split(items, i => qualityIds.Contains(i.QualityDocumentId));
        Warn(warnings, orphans.Count, "связок с материалами", "их документа качества нет");

        int created = 0, updated = 0;
        foreach (var item in ok)
        {
            if (!TryParseEnum<CatalogScope>(item.Scope, "уровень связки с материалом", warnings, out var scope))
                continue;

            // Тот же материал в том же месте — правим ТУ строку, какой бы идентификатор у неё ни
            // был: здесь личность связки задаёт материал, а не Id.
            var targetId = byKey.TryGetValue((scope, item.ScopeId, item.MaterialKey), out var sameMaterial)
                ? sameMaterial
                : item.Id;
            var exists = targetId != item.Id || byId.Contains(item.Id);

            db.Entry(MaterialQualityLink.Restore(
                targetId, scope, item.ScopeId, item.MaterialKey, item.MaterialLabel,
                item.QualityDocumentId, item.CreatedAt, item.UpdatedAt)).State =
                exists ? EntityState.Modified : EntityState.Added;

            if (exists) updated++; else created++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        stats.Count("Связки с материалами", created, updated);
    }

    /// <summary>
    /// Разбор перечисления из копии: неизвестное значение пропускает ОДНУ запись с предупреждением,
    /// а не валит восстановление.
    ///
    /// Копию из более новой версии импорт принимает намеренно («часть данных могла быть
    /// пропущена»), и новый вариант перечисления там — обычное дело. <c>Enum.Parse</c> в этом
    /// случае бросает, транзакция откатывается целиком, и узнаёт об этом администратор после
    /// аварии — то есть ровно тогда, когда терять нечего.
    /// </summary>
    private static bool TryParseEnum<TEnum>(
        string value, string what, List<string> warnings, out TEnum parsed) where TEnum : struct, Enum
    {
        if (Enum.TryParse(value, out parsed)) return true;
        warnings.Add($"Пропущена запись: неизвестный {what} «{value}» — копия сделана более новой версией.");
        return false;
    }

    /// <summary>Разделяет записи на «можно восстановить» и «не к чему приложить».</summary>
    private static (List<T> Ok, List<T> Skipped) Split<T>(IEnumerable<T> items, Func<T, bool> canRestore)
    {
        List<T> ok = [], skipped = [];
        foreach (var item in items) (canRestore(item) ? ok : skipped).Add(item);
        return (ok, skipped);
    }

    /// <summary>
    /// Пропущенное — всегда вслух. Молчаливый пропуск строки, у которой не нашлось носителя, и есть
    /// тот самый случай «восстановилось, но не всё», ради которого issue #833 и заведён.
    /// </summary>
    private static void Warn(List<string> warnings, int count, string what, string why)
    {
        if (count == 0) return;
        warnings.Add($"Пропущено {what}: {count} — {why}.");
    }

    private sealed class RestoreStats
    {
        public int PrimitiveTypesCreated, PrimitiveTypesUpdated;
        public int EnumTypesCreated, EnumTypesUpdated;
        public int RecognitionProfilesCreated, RecognitionProfilesUpdated;
        public int DocumentTypesCreated, DocumentTypesUpdated;
        public int TemplatesCreated, TemplatesUpdated;
        public int TemplateAssetsCreated, TemplateAssetsUpdated;
        public bool TypstUserLibRestored;
        public int TypstUserLibFilesRestored;
        public int CatalogEntitiesCreated, CatalogEntitiesUpdated;
        public int CommonDataEntriesCreated, CommonDataEntriesUpdated;
        public int DataSetBindingTemplatesCreated, DataSetBindingTemplatesUpdated;
        public int ReconciliationAliasesCreated, ReconciliationAliasesUpdated;
        public int DataSetProcessingTemplatesCreated, DataSetProcessingTemplatesUpdated;
        public int QualityDocumentsCreated, QualityDocumentsUpdated;

        /// <summary>
        /// Проектные секции (issue #833) — счётчиками по имени, а не восемнадцатью полями подряд.
        /// Отчёт о восстановлении и так перечисляет два десятка чисел; следующая секция копии не
        /// должна означать правку в четырёх местах ради ещё одной пары.
        /// </summary>
        private readonly Dictionary<string, (int Created, int Updated)> _project = [];

        public void Count(string label, int created, int updated)
        {
            if (created == 0 && updated == 0) return;
            _project[label] = (created, updated);
        }

        public IReadOnlyList<RestoreSectionStat>? ProjectSections() => _project.Count == 0
            ? null
            : _project.Select(kv => new RestoreSectionStat(kv.Key, kv.Value.Created, kv.Value.Updated)).ToArray();
    }
}
