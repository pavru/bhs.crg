using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Reconciliation;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Domain.Templates;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Восстановление таблиц по одной — часть <see cref="BackupService" />.
///
/// <para>26 методов-ровесников, по одному на сущность, плюс общие для них помощники
/// (<c>UpsertAsync</c>, топосорт типов, проверка носителя скопа). Файл немаленький, и это
/// осознанно: это СПИСОК однородных операций, а не переплетение — каждая коротка, читается сама по
/// себе, и порядок их вызова задан в <c>BackupService.Import.cs</c>.</para>
/// </summary>
public partial class BackupService
{
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
            // Копия, снятая ДО появления владельца, владельца не несёт. Подставлять умолчание
            // нельзя: «ядро» отдало бы ядру всю исполнительную документацию, «id» — отобрало бы у
            // ядра справочники. Применяется то же правило, что и в миграции, — по коду типа.
            var module = string.IsNullOrWhiteSpace(item.Module)
                ? CoreOwnedTypes.OwnerFor(item.Code)
                : item.Module.Trim();
            var storage = Enum.TryParse<TypeStorage>(item.Storage, out var parsedStorage)
                ? parsedStorage : TypeStorage.SharedObject;
            var visibility = Enum.TryParse<TypeVisibility>(item.Visibility, out var parsedVisibility)
                ? parsedVisibility : TypeVisibility.Shared;
            var channels = item.ReadChannels ?? [];
            if (TypeReadChannels.Unknown(channels) is { Count: > 0 } unknownChannels)
                warnings.Add($"Тип документа «{item.Name}»: неизвестные каналы чтения — " +
                             string.Join(", ", unknownChannels) + "; перенесены как есть.");

            // Уровень правки из копии, снятой до его появления, — «открытый»: замков тогда не
            // было, и придумывать их задним числом значило бы запереть схему, которую никто не
            // запирал.
            var editLevel = Enum.TryParse<SchemaEditLevel>(item.EditLevel, out var parsedLevel)
                ? parsedLevel : SchemaEditLevel.Open;

            var entity = DocumentType.Restore(
                item.Id, item.Name, item.Code, kind, item.ParentId,
                JsonDocument.Parse(item.Schema.GetRawText()),
                JsonDocument.Parse(item.PluginBindings.GetRawText()),
                item.IsAbstract, item.CreatedAt, item.UpdatedAt, item.Group, item.AllowsProxy,
                module, storage, visibility, channels, editLevel);
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

    /// <summary>
    /// Журнал действий из копии (ТЗ CORE-28).
    ///
    /// Только ДОПИСЫВАНИЕ и только новых идентификаторов: обновления здесь нет и быть не может —
    /// запись журнала неизменяема, и upsert поверх собственной истории экземпляра стёр бы её.
    /// Поэтому и счётчик один: «добавлено».
    ///
    /// Идёт через службу журнала, а не через набор напрямую: прямой доступ есть только у неё
    /// (сторож <c>ActivityLogInventoryTests</c>).
    /// </summary>
    /// <summary>
    /// Настройки экземпляра (issue #960). Upsert по ключу: копия восстанавливается и поверх живой
    /// системы, и ключ, которого в копии нет, трогать нельзя — его задали здесь, а не там.
    /// </summary>
    private async Task RestoreAppSettingsAsync(
        BackupAppSetting[] items, RestoreStats stats, CancellationToken ct)
    {
        if (items.Length == 0) return;

        var existing = await db.AppSettings.ToDictionaryAsync(a => a.Key, StringComparer.Ordinal, ct);
        int created = 0, updated = 0;
        foreach (var item in items)
        {
            if (existing.TryGetValue(item.Key, out var row)) { row.SetValue(item.Value); updated++; }
            else { db.AppSettings.Add(BHS.CRG.Domain.Settings.AppSetting.Create(item.Key, item.Value)); created++; }
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        stats.Count("Настройки системы", created, updated);
    }

    private async Task RestoreActivityLogAsync(
        BackupActivityRecord[] items, RestoreStats stats, CancellationToken ct)
    {
        if (items.Length == 0) return;

        var added = await journal.ImportAsync(
            [.. items.Select(i => BHS.CRG.Domain.Activity.ActivityRecord.Create(
                i.Action, i.ActorId, i.ActorName, i.TargetId, i.TargetLabel, i.Before, i.After,
                id: i.Id, occurredAt: i.OccurredAt))],
            ct);

        db.ChangeTracker.Clear();
        stats.Count("Журнал действий", added, 0);
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
            i.Id, i.Name, i.CreatedByUserId, i.ProfileObjectId, i.CreatedAt, i.UpdatedAt,
            i.TimeZoneId, i.ExternalSystem, i.ExternalCode))), existing, ct);
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
    /// Строки плана комплектов (issue #796).
    ///
    /// Строка без своего типа документа НЕ восстанавливается: план на несуществующий тип — позиция,
    /// которую нечем закрыть, и процент готовности навсегда упирался бы в потолок ниже ста без
    /// видимой причины. Лучше предупредить при восстановлении, чем оставить необъяснимую цифру.
    ///
    /// Раскладывается по ПРИРОДНОМУ ключу (комплект, тип), а не по Id — как и связки материалов
    /// выше, и по той же причине, только острее: правка плана удаляет строки и заводит новые со
    /// СВЕЖИМИ идентификаторами, значит после любой правки Id в копии заведомо устарел, а пара
    /// (комплект, тип) осталась прежней. Вставка по Id упёрлась бы в уникальный индекс, а 23505
    /// здесь означает не «пропустим строку», а откат ВСЕГО восстановления: администратор получил бы
    /// «Ошибка восстановления БД» и пустую систему из-за одной строки плана.
    /// </summary>
    private async Task RestoreDocumentSetPlansAsync(
        BackupDocumentSetPlan[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;
        var existing = await db.DocumentSetPlans.AsNoTracking()
            .Select(x => new { x.Id, x.DocumentSetId, x.DocumentTypeId }).ToListAsync(ct);
        var byKey = existing.ToDictionary(x => (x.DocumentSetId, x.DocumentTypeId), x => x.Id);
        var byId = existing.Select(x => x.Id).ToHashSet();

        var sets = await db.DocumentSets.Select(x => x.Id).ToHashSetAsync(ct);
        var types = await db.DocumentTypes.Select(x => x.Id).ToHashSetAsync(ct);

        var (ok, orphans) = Split(items, i => sets.Contains(i.DocumentSetId) && types.Contains(i.DocumentTypeId));
        Warn(warnings, orphans.Count, "строк плана", "их комплекта или типа документа нет ни в копии, ни в системе");

        int created = 0, updated = 0;
        foreach (var item in ok)
        {
            var targetId = byKey.TryGetValue((item.DocumentSetId, item.DocumentTypeId), out var samePair)
                ? samePair
                : item.Id;
            var exists = targetId != item.Id || byId.Contains(item.Id);

            db.Entry(DocumentSetPlanItem.Restore(
                targetId, item.DocumentSetId, item.DocumentTypeId, item.PlannedCount,
                item.CreatedAt, item.UpdatedAt)).State = exists ? EntityState.Modified : EntityState.Added;

            if (exists) updated++; else created++;
        }

        await db.SaveChangesAsync(ct);
        stats.Count("Строки плана", created, updated);
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
}
