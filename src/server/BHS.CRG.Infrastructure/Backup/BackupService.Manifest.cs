using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Reconciliation;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Снимок живой базы в паспорт копии — часть <see cref="BackupService" />.
///
/// <para>Это шаг ВЫГРУЗКИ, а не применения: <c>ReadManifestAsync</c> читает таблицы и складывает
/// их в <c>BackupManifest</c>, который потом ложится в архив. Разбор ВХОДНОГО архива живёт в
/// <c>BackupService.Import.cs</c>, и путать их нельзя — правка здесь меняет состав НОВЫХ копий,
/// а не проверку загруженных.</para>
///
/// <para>⚠️ Имя метода обманчиво: «Read» здесь про чтение базы, а не про чтение копии. На этом я и
/// ошибся в первой редакции доккомментария (ревью PR #1025), поверив имени вместо кода.</para>
///
/// <para>Зовётся только из <c>BuildManifestAsync</c>, который оборачивает вызов в транзакцию
/// <c>RepeatableRead</c>: снимок обязан быть целостным, иначе части копии окажутся из разных
/// моментов времени.</para>
/// </summary>
public partial class BackupService
{
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

        // Журнал действий целиком: он дописывается редко (правки ролей и схем), и «последние N»
        // означало бы копию, которая тем короче помнит, чем дольше ею пользуются.
        var activity = await journal.ExportAsync(ct);
        var appSettings = await db.AppSettings.AsNoTracking().OrderBy(a => a.Key).ToListAsync(ct);

        // Проектные данные (issue #833) читаются ТОЛЬКО для полной копии: конфигурационная
        // остаётся ровно тем, чем была, и весит столько же. Порядок чтения не важен - снимок один.
        var full = scope == BackupScope.Full;
        var constructions = full ? await db.Constructions.AsNoTracking().ToListAsync(ct) : [];
        var sections = full ? await db.Sections.AsNoTracking().ToListAsync(ct) : [];
        var sets = full ? await db.DocumentSets.AsNoTracking().ToListAsync(ct) : [];
        var setPlans = full ? await db.DocumentSetPlans.AsNoTracking().ToListAsync(ct) : [];
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
                dt.CreatedAt, dt.UpdatedAt, dt.Group, dt.AllowsProxy,
                dt.Module, dt.Storage.ToString(), dt.Visibility.ToString(), [.. dt.ReadChannels],
                dt.EditLevel.ToString())).ToArray(),
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
                c.Id, c.Name, c.CreatedByUserId, c.ProfileObjectId, c.CreatedAt, c.UpdatedAt,
                c.TimeZoneId, c.ExternalSystem, c.ExternalCode)).ToArray() : null,
            Sections: full ? sections.Select(x => new BackupSection(
                x.Id, x.ConstructionId, x.Name, x.ProfileObjectId, x.CreatedAt, x.UpdatedAt)).ToArray() : null,
            DocumentSets: full ? sets.Select(x => new BackupDocumentSet(
                x.Id, x.SectionId, x.Name, x.ProfileObjectId, x.CreatedAt, x.UpdatedAt)).ToArray() : null,
            DocumentSetPlans: full ? setPlans.Select(x => new BackupDocumentSetPlan(
                x.Id, x.DocumentSetId, x.DocumentTypeId, x.PlannedCount, x.CreatedAt, x.UpdatedAt)).ToArray() : null,
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
                l.QualityDocumentId, l.CreatedAt, l.UpdatedAt)).ToArray() : null,
            // Журнал действий — в любой копии, включая конфигурационную (ТЗ CORE-28). Читается
            // через службу, а не из набора: прямой доступ к журналу есть только у неё.
            ActivityLog: activity.Select(r => new BackupActivityRecord(
                r.Id, r.OccurredAt, r.Action, r.ActorId, r.ActorName,
                r.TargetId, r.TargetLabel, r.Before, r.After)).ToArray(),
            AppSettings: appSettings.Select(a => new BackupAppSetting(a.Key, a.Value, a.UpdatedAt)).ToArray());
    }
}
