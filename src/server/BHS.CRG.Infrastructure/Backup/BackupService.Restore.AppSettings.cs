using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Settings;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Восстановление настроек ЭКЗЕМПЛЯРА системы (ТЗ CORE-25.3, issue #960): часовой пояс компании и
/// то, что появится рядом.
///
/// <para>Своим файлом, а не строками в <c>BackupService.Restore.cs</c>: тот уже признан неразрывным
/// складом на семьсот строк и стоит в базовом уровне храповика. Дописывать в склад значит двигать
/// его уровень каждой правкой — а правило заведено ровно против этого (ревью PR #1046).</para>
/// </summary>
public partial class BackupService
{
    /// <summary>
    /// Настройки экземпляра (issue #960). Upsert по ключу: копия восстанавливается и поверх живой
    /// системы, и ключ, которого в копии нет, трогать нельзя — его задали здесь, а не там.
    /// </summary>
    private async Task RestoreAppSettingsAsync(
        BackupAppSetting[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;

        // Ключ из копии проверяется КАТАЛОГОМ, как и на адресе (ТЗ CORE-25.4): копия приходит от
        // другой сборки, и в ней бывает ключ будущей версии или опечатка. Записать его молча
        // значило бы завести настройку, которую здесь никто не читает, — она выглядела бы
        // действующей. Слишком длинное значение отсекаем по той же причине (ревью PR #1046).
        var (ok, rejected) = Split(items, i =>
            BHS.CRG.Application.Settings.AppSettingKeys.IsKnown(i.Key)
            && i.Value.Length <= BHS.CRG.Application.Settings.AppSettingKeys.MaxValueLength);
        Warn(warnings, rejected.Count, "настроек системы",
            "их ключ эта версия не объявляет или значение длиннее допустимого");

        var existing = await db.AppSettings.ToDictionaryAsync(a => a.Key, StringComparer.Ordinal, ct);
        int created = 0, updated = 0;
        foreach (var item in ok)
        {
            if (existing.TryGetValue(item.Key, out var row)) { row.SetValue(item.Value); updated++; }
            else { db.AppSettings.Add(BHS.CRG.Domain.Settings.AppSetting.Create(item.Key, item.Value)); created++; }
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        stats.Count("Настройки системы", created, updated);
    }
}
