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

    public async Task<(Stream ZipStream, string FileName)> ExportAsync(CancellationToken ct = default)
    {
        var manifest = await BuildManifestAsync(ct);

        // Архив собирается на ДИСКЕ, а не в памяти. Пока копия несла только ассеты шаблонов, это
        // были единицы мегабайт и MemoryStream ничего не стоил. С библиотекой качества (issue #687)
        // размер задаётся числом сертификатов и растёт годами: MemoryStream удваивает буфер, то есть
        // на пике держит около двух объёмов архива в куче больших объектов, и упирается в
        // int.MaxValue — причём отказ пришёл бы ровно тогда, когда копия нужнее всего.
        //
        // DeleteOnClose: файл исчезает, как только поток закроют. Закрывает его отправка ответа
        // (Results.File освобождает поток) — отдельной уборки не нужно, и она не потеряется при
        // разрыве соединения.
        var ms = new FileStream(
            Path.Combine(Path.GetTempPath(), $"crg-backup-{Guid.NewGuid():N}.zip"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Write manifest.json
            var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Fastest);
            await using (var w = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(w, manifest, JsonOptions, ct);

            // Write binary blobs
            var blobPaths = ExtractBlobPaths(manifest);
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
                }
            }
        }

        ms.Position = 0;
        var fileName = $"crg-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip";
        return (ms, fileName);
    }

    // ── Оценка размера ────────────────────────────────────────────────────────

    /// <summary>
    /// Сколько будет весить копия, снятая прямо сейчас, — не снимая её (issue #711).
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
    public async Task<BackupSizeEstimate> EstimateSizeAsync(long limitBytes, CancellationToken ct = default)
    {
        var manifest = await BuildManifestAsync(ct);

        var counter = new CountingStream();
        await using (var deflate = new DeflateStream(counter, CompressionLevel.Fastest, leaveOpen: true))
            await JsonSerializer.SerializeAsync(deflate, manifest, JsonOptions, ct);
        var manifestBytes = counter.Written;

        long blobBytes = 0;
        var missing = 0;
        var paths = ExtractBlobPaths(manifest);
        var overhead = EntryOverhead("manifest.json");
        foreach (var path in paths)
        {
            overhead += EntryOverhead($"blobs/{path}");
            var size = await blob.GetSizeAsync(path, ct);
            // Недоступный блоб экспорт пропускает с предупреждением — оценка считает его так же.
            if (size is null) missing++;
            else blobBytes += size.Value;
        }

        return new BackupSizeEstimate(
            TotalBytes: manifestBytes + blobBytes + overhead,
            ManifestBytes: manifestBytes,
            BlobBytes: blobBytes,
            BlobCount: paths.Count,
            MissingBlobCount: missing,
            LimitBytes: limitBytes);
    }


    private async Task<BackupManifest> BuildManifestAsync(CancellationToken ct)
    {
        var docTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
        var templates = await db.Templates.AsNoTracking().ToListAsync(ct);
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
                q.CreatedAt, q.UpdatedAt)).ToArray());
    }

    // ── Import ────────────────────────────────────────────────────────────────

    public async Task<RestoreReport> ImportAsync(Stream zipStream, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new ConflictException("Файл не является резервной копией BHS.CRG (отсутствует manifest.json).");

        BackupManifest manifest;
        using (var ms = new MemoryStream())
        {
            await using (var es = manifestEntry.Open())
                await es.CopyToAsync(ms, ct);
            ms.Position = 0;
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(ms, JsonOptions, ct)
                       ?? throw new ConflictException("Не удалось прочитать manifest.json.");
        }

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
            await RestoreCommonDataEntriesAsync(manifest.CommonDataEntries, stats, warnings, ct);
            // После типов документов: шаблон маппинга висит на типе и без него бессмыслен.
            await RestoreDataSetBindingTemplatesAsync(manifest.DataSetBindingTemplates ?? [], stats, warnings, ct);
            // Зависимостей нет вовсе — место в порядке произвольно.
            await RestoreReconciliationAliasesAsync(manifest.ReconciliationAliases ?? [], stats, warnings, ct);
            await RestoreDataSetProcessingTemplatesAsync(manifest.DataSetProcessingTemplates ?? [], stats, ct);
            // После типов документов: подтип сертификата — обычный тип, и без него документ качества
            // не показать.
            await RestoreQualityDocumentsAsync(
                manifest.QualityDocuments ?? [], restoredBlobPaths, stats, warnings, ct);
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
                stats.QualityDocumentsCreated, stats.QualityDocumentsUpdated);
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
        RestoreStats stats, List<string> warnings, CancellationToken ct)
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

        // Связки с материалами (MaterialQualityLink) в копию не входят: они адресуют материалы
        // комплектов, а комплектов здесь нет. Сказать об этом надо — иначе восстановивший увидит
        // полную библиотеку и решит, что вернулась и проделанная работа по привязке.
        //
        // Но ТОЛЬКО если привязок в системе нет вовсе. Восстановление ничего не удаляет, и на самом
        // обычном пути — админ накатывает конфигурационную копию на рабочую систему, чтобы вернуть
        // шаблон, — все связки остаются на месте. Безусловное «библиотека вернулась непривязанной»
        // объявило бы там потерянной работу, которая цела, и позвало бы делать её заново. Тем же
        // рассуждением проверяет себя предупреждение о ссылках на документы двумя методами выше.
        if (!await db.MaterialQualityLinks.AnyAsync(ct))
            warnings.Add(
                "Документы качества: связки с материалами копией не переносятся — они относятся к " +
                "комплектам, которых в копии нет. Библиотека восстановлена непривязанной.");
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
    }
}
