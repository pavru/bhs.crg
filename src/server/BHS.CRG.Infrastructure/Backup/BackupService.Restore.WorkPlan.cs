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
    /// <b>Довод ревью PR #1056 («таблиц модулей в копии нет») с задачи A2b (issue #1073) больше не
    /// верен</b> — схемы модулей в копию входят, — а правило остаётся, и вот почему. Двойник по
    /// ключу бывает ровно в одном случае: восстановление в систему, которая уже живёт. А в живой
    /// системе на позицию уже ссылаются живые строки — и они указывают на живой идентификатор.
    /// Принеси мы свой, оборвались бы именно они, причём восстановить их было бы нечем: в копии их
    /// нет. Ссылки ИЗ КОПИИ при том же выборе теряются тоже, но про них хотя бы известно, что они
    /// были, — поэтому про них говорится вслух (см. ниже). В пустую систему (обычный случай:
    /// восстановление после потери) ключей-двойников нет, идентификаторы возвращаются теми же, и не
    /// теряется ничего.</para>
    ///
    /// <para>⚠️ Цена решения названа предупреждением: если сопоставление по ключу СЛУЧИЛОСЬ и копия
    /// несёт данные модулей, строка модуля, адресующая позицию идентификатором, после
    /// восстановления не ведёт никуда. Сама копия исправить это не может — какие колонки модуля
    /// держат идентификатор позиции, знает только модуль, — но молчать об этом нельзя: оборванная
    /// ссылка ничего не ломает сразу и проявится далеко от восстановления.</para>
    ///
    /// <para>⚠️ Сирот не вставляем, а пересчитываем в предупреждение: позиция без вида работы или без
    /// единицы измерения не означает ничего, а внешний ключ не дал бы её записать вовсе — и
    /// восстановление упало бы целиком из-за одной строки.</para>
    /// </summary>
    /// <param name="modulesInCopy">Копия несёт данные схем модулей (issue #1073). Меняет не
    /// поведение, а отчёт: без данных модулей сопоставление по ключу терять нечему.</param>
    private async Task RestoreWorkPlanItemsAsync(
        BackupWorkPlanItem[] items, bool modulesInCopy, RestoreStats stats, List<string> warnings,
        CancellationToken ct)
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

        int created = 0, updated = 0, remapped = 0;
        foreach (var item in ok)
        {
            var key = (item.WorkTypeId, item.ConstructionId, item.SectionId, item.UnitId);
            var targetId = byKey.TryGetValue(key, out var sameKey) ? sameKey : item.Id;
            var exists = targetId != item.Id || byId.Contains(item.Id);
            if (targetId != item.Id) remapped++;

            db.Entry(WorkPlanItem.Restore(
                targetId, item.WorkTypeId, item.ConstructionId, item.SectionId, item.UnitId,
                item.CreatedAt, item.UpdatedAt)).State = exists ? EntityState.Modified : EntityState.Added;

            if (exists) updated++; else created++;
        }

        if (remapped > 0 && modulesInCopy)
            warnings.Add(
                $"Позиций перечня работ сопоставлено с уже существующими по естественному ключу: {remapped}. " +
                "Их идентификаторы из копии отброшены в пользу живых (ревью PR #1056), а копия несёт " +
                "данные модулей: если строки модуля адресуют позицию идентификатором, эти ссылки " +
                "после восстановления никуда не ведут. Проверьте их вручную — исправить их копия не " +
                "может, какие колонки модуля держат позицию, знает только сам модуль.");

        await db.SaveChangesAsync(ct);
        stats.Count("Позиции перечня работ", created, updated);
    }
}
