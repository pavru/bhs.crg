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
        var old = manifest.KnowsDirectoryRoles != true;
        var all = await db.DocumentTypes.ToListAsync(ct);

        if (old)
        {
            var changed = DirectoryRoleDefaults.Apply(all);
            foreach (var (type, schema) in changed) type.UpdateSchema(schema);
            await db.SaveChangesAsync(ct);

            if (changed.Count > 0)
                warnings.Add(
                    "Копия снята версией, не знавшей ролей справочников. Роли и тэги полей назначены по прежним " +
                    $"именам типов: {string.Join(", ", changed.Select(c => $"«{c.Type.Name}»"))}. Проверьте их в " +
                    "редакторе типов.");
        }

        // Итог — СЛОВАМИ, и тогда, когда правило ничего не назначило (ревью PR #1317). Молчание
        // здесь и было бы тем самым «поднялась зелёной и без справочников»: копию разворачивают и
        // поверх живой системы, и роль после этого могут нести два типа — либо ни один.
        foreach (var rule in DirectoryRoleDefaults.Rules)
        {
            var role = RoleResolution.Role(rule.Role, all);
            if (role.Root is not null) continue;

            var bearers = string.Join(", ", role.Bearers.Select(t => $"«{t.Name}»"));
            if (role.Bearers.Count > 1)
                warnings.Add(
                    $"Роль справочника «{rule.Role}» после восстановления несут несколько типов: {bearers}. " +
                    "Справочник должен быть один — пока их несколько, документы модулей его не находят. " +
                    "Снимите тэг типа с лишних в редакторе типов.");
            else if (role.Bearers.Count == 1)
                warnings.Add(
                    $"Роль справочника «{rule.Role}» стоит на типе {bearers}, а справочником он быть не может " +
                    "(справочник — составной тип в общих данных). Документы модулей такой справочник не " +
                    "находят. Поставьте тэг типа нужному типу в редакторе типов.");
            else if (old)
                warnings.Add(
                    $"Роль справочника «{rule.Role}» не назначена ни одному типу: типа с кодом «{rule.Code}» " +
                    "в системе нет. Если установка ведёт счета и накладные, поставьте тэг типа нужному " +
                    "типу в редакторе типов — без этого документы модулей справочник не находят.");
        }

        db.ChangeTracker.Clear();
    }
}
