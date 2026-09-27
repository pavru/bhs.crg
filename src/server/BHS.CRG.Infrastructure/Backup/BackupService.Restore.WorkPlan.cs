using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Восстановление перечня работ (ТЗ CORE-10, issue #964).
///
/// <para>Своим файлом, а не строками в <c>BackupService.Restore.cs</c>: тот стоит в храповике
/// размера на своём уровне, и дописать в него — значит поднять порог складу, который и без того
/// читают целиком ради одной правки. Часть класса при этом та же.</para>
/// </summary>
public partial class BackupService
{
    /// <summary>
    /// Позиции перечня. Восстанавливаются ПОСЛЕ строек, разделов и общих данных: позиция ссылается
    /// на стройку, раздел, запись классификатора и единицу измерения — всё это её носители.
    ///
    /// <para><b>Ключ важнее идентификатора.</b> Если позиция с таким же ключом в системе уже есть,
    /// обновляем ЕЁ, а не заводим вторую: уникальность ключа запретила бы вставку, и восстановление
    /// упало бы там, где должно было доложить. Тот же приём, что у строк плана комплекта.</para>
    ///
    /// <para>⚠️ Идентификатор из копии при этом ОТБРАСЫВАЕТСЯ, и это осознанно (ревью PR #1056).
    /// Таблиц модулей в копии нет — восстановление их не трогает, — значит ссылки на позицию держат
    /// живые строки живой системы, и они указывают на живой идентификатор. Принеси мы свой,
    /// оборвались бы именно они: ради ссылок, которых в копии и не было. В пустую систему (обычный
    /// случай: восстановление после потери) ключей-двойников нет, и идентификаторы возвращаются
    /// теми же.</para>
    ///
    /// <para>⚠️ Сирот не вставляем, а пересчитываем в предупреждение: позиция без вида работы или без
    /// единицы измерения не означает ничего, а внешний ключ не дал бы её записать вовсе — и
    /// восстановление упало бы целиком из-за одной строки.</para>
    /// </summary>
    private async Task RestoreWorkPlanItemsAsync(
        BackupWorkPlanItem[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;

        var existing = await db.WorkPlanItems.AsNoTracking()
            .Select(x => new { x.Id, x.WorkTypeId, x.ConstructionId, x.SectionId, x.UnitId })
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(
            x => (x.WorkTypeId, x.ConstructionId, x.SectionId, x.UnitId), x => x.Id);
        var byId = existing.Select(x => x.Id).ToHashSet();

        var constructions = await db.Constructions.Select(x => x.Id).ToHashSetAsync(ct);
        var sections = await db.Sections.Select(x => x.Id).ToHashSetAsync(ct);
        var objects = await db.DomainObjects.Select(x => x.Id).ToHashSetAsync(ct);

        var (ok, orphans) = Split(items, i =>
            constructions.Contains(i.ConstructionId)
            && (i.SectionId is null || sections.Contains(i.SectionId.Value))
            && objects.Contains(i.WorkTypeId)
            && objects.Contains(i.UnitId));
        Warn(warnings, orphans.Count, "позиций перечня работ",
            "их стройки, раздела, вида работы или единицы измерения нет ни в копии, ни в системе");

        int created = 0, updated = 0;
        foreach (var item in ok)
        {
            var key = (item.WorkTypeId, item.ConstructionId, item.SectionId, item.UnitId);
            var targetId = byKey.TryGetValue(key, out var sameKey) ? sameKey : item.Id;
            var exists = targetId != item.Id || byId.Contains(item.Id);

            db.Entry(WorkPlanItem.Restore(
                targetId, item.WorkTypeId, item.ConstructionId, item.SectionId, item.UnitId,
                item.CreatedAt, item.UpdatedAt)).State = exists ? EntityState.Modified : EntityState.Added;

            if (exists) updated++; else created++;
        }

        await db.SaveChangesAsync(ct);
        stats.Count("Позиции перечня работ", created, updated);
    }
}
