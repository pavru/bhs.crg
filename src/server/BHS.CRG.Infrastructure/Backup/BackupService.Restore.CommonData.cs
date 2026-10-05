using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Восстановление записей общих данных — вынесено из <c>BackupService.Restore.cs</c> (issue #1185):
/// с признаком архива тот перерос свой уровень, а этот шаг — самый длинный и самый обособленный.
/// </summary>
public partial class BackupService
{
    private async Task RestoreCommonDataEntriesAsync(
        BackupCommonDataEntry[] items, bool knowsArchive, RestoreStats stats, List<string> warnings,
        CancellationToken ct)
    {
        // Общие данные восстанавливаем как DomainObject без документной фасеты (issue #84).
        // Признак архива существующих записей (issue #1185): обычное сохранение колонку не пишет,
        // поэтому их состояния собираются сюда и ставятся службой архива после записи.
        var archiveStates = new Dictionary<Guid, DateTimeOffset?>();
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
                scope, item.ScopeId, item.CreatedAt, item.UpdatedAt, item.Aliases,
                // Новой записи признак уходит вставкой. Копия о нём не знает — запись действующая:
                // до появления архива других и не было.
                knowsArchive ? item.ArchivedAt : null);
            db.Entry(entity).State = existingIds.Contains(item.Id) ? EntityState.Modified : EntityState.Added;
            if (existingIds.Contains(item.Id)) archiveStates[item.Id] = item.ArchivedAt;
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

        // Существующим записям признак ставит копия — если она о нём знает. Не знает — текущий
        // остаётся как есть: «поля не было» не значит «записи не в архиве», и применить такой null
        // значило бы вернуть в выбор всё, что из него убрали после снятия копии.
        if (knowsArchive)
            await archive.RestoreAsync(archiveStates, ct);

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
}
