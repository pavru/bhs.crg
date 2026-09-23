using System.Data;
using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Применение копии: оркестровка восстановления — часть <see cref="BackupService" />.
///
/// <para>Здесь только порядок и отчёт; сами таблицы восстанавливают методы в
/// <c>BackupService.Restore.cs</c>. Выделено из файла в 1790 строк (issue #1021): это операция с
/// наибольшей ценой ошибки — она пишет поверх живых данных, — и читать её вперемешку с выгрузкой
/// было нельзя.</para>
/// </summary>
public partial class BackupService
{
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
            // План — после комплектов (носитель) и после типов документов (на них ссылается).
            await RestoreDocumentSetPlansAsync(manifest.DocumentSetPlans ?? [], stats, warnings, ct);
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
            await RestoreActivityLogAsync(manifest.ActivityLog ?? [], stats, ct);
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
}
