using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Schema;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

public partial class BackupService
{
    /// <summary>
    /// Копия, снятая ДО появления ролей справочников, ролей не несёт (issue #1101) — а код находит
    /// организации и номенклатуру уже только по ним. Разверни мы такую копию как есть, установка
    /// поднялась бы зелёной и без справочников: каждый счёт — без поставщика, и ни одного отказа при
    /// восстановлении.
    ///
    /// <para>Применяется то же правило, что в миграции <c>DirectoryRoles</c>, — одной функцией
    /// (<see cref="DirectoryRoleDefaults" />), по типам, какими они СТАЛИ после восстановления: копию
    /// разворачивают и поверх живой системы, и роль, которую там уже несёт другой тип, занята.</para>
    ///
    /// <para>Копия, снятая после переноса, говорит об этом сама (<see cref="BackupManifest.KnowsDirectoryRoles" />),
    /// и её типы не трогаются: снятая роль в ней — решение администратора, а не пробел.</para>
    /// </summary>
    private async Task AssignDirectoryRolesAsync(BackupManifest manifest, List<string> warnings, CancellationToken ct)
    {
        if (manifest.KnowsDirectoryRoles == true) return;

        var all = await db.DocumentTypes.ToListAsync(ct);
        var changed = DirectoryRoleDefaults.Apply(all);
        foreach (var (type, schema) in changed) type.UpdateSchema(schema);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        if (changed.Count > 0)
            warnings.Add(
                "Копия снята версией, не знавшей ролей справочников. Роли и тэги полей назначены по прежним " +
                $"именам типов: {string.Join(", ", changed.Select(c => $"«{c.Type.Name}»"))}. Проверьте их в " +
                "редакторе типов.");
    }
}
