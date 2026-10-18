using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Storage;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

public partial class BackupService
{
    /// <summary>
    /// Профили печати из копии (issue #1454). В ЛЮБОЙ копии: это настройка компании, как типы и
    /// шаблоны.
    ///
    /// <para><b>Системный профиль — один, и копия его не плодит.</b> Его идентификатор — константа кода,
    /// поэтому системный из копии другой установки приходит под тем же идентификатором, что здесь, и
    /// ложится параметрами на здешний. Имя у него не меняется: оно константа тоже.</para>
    ///
    /// <para>Свои профили — upsert по идентификатору. Профиль, чьё имя здесь занято другим, пропускается
    /// с предупреждением: два «Узких» в одном меню человек не различит.</para>
    ///
    /// <para>⚠️ Записи видов в копию не входят: после восстановления виды строятся заново по системному
    /// профилю — выбранный когда-то для файла профиль не помнится.</para>
    /// </summary>
    private async Task RestorePrintProfilesAsync(
        BackupPrintProfile[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        if (items.Length == 0) return;

        var existing = await db.RenditionLayoutProfiles.ToListAsync(ct);
        var byId = existing.ToDictionary(p => p.Id);
        int created = 0, updated = 0, invalid = 0, named = 0;

        foreach (var item in items)
        {
            if (ReadProfile(item) is not { } input)
            {
                invalid++;
                continue;
            }

            if (item.IsSystem || item.Id == RenditionLayoutProfile.SystemId)
            {
                var system = existing.FirstOrDefault(p => p.IsSystem);
                var fresh = system is null;
                if (system is null)
                {
                    // Сидер на старте его создаёт всегда; нет — значит, его не было и в этой копии
                    // восстановления, и брать его неоткуда, кроме как отсюда.
                    system = RenditionLayoutProfile.System();
                    db.RenditionLayoutProfiles.Add(system);
                    existing.Add(system);
                    created++;
                }
                if (Apply(system, RenditionLayoutProfile.SystemName, input) && !fresh) updated++;
                continue;
            }

            var lower = input.Name.ToLowerInvariant();
            if (existing.Any(p => p.Id != item.Id && p.Name.ToLowerInvariant() == lower))
            {
                named++;
                continue;
            }

            if (byId.TryGetValue(item.Id, out var row))
            {
                if (Apply(row, input.Name, input)) updated++;
                continue;
            }

            var restored = RenditionLayoutProfile.Restore(
                item.Id, input.Name, input.Paper, input.Orientation, input.Fit, input.Margins, item.CreatedAt, item.UpdatedAt);
            db.RenditionLayoutProfiles.Add(restored);
            existing.Add(restored);
            byId[restored.Id] = restored;
            created++;
        }

        Warn(warnings, invalid, "профилей печати", "параметры эта версия не принимает");
        Warn(warnings, named, "профилей печати", "профиль с таким названием здесь уже есть");
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        stats.Count("Профили печати", created, updated);

        // Совпадающий профиль не трогаем: иначе повторное восстановление той же копии сдвигало бы
        // время правки каждого профиля без единого изменения.
        static bool Apply(RenditionLayoutProfile row, string name, PrintProfileInput input)
        {
            if (row.Name == name && row.Paper == input.Paper && row.Orientation == input.Orientation
                && row.Fit == input.Fit && row.Margins == input.Margins)
                return false;
            row.Update(name, input.Paper, input.Orientation, input.Fit, input.Margins);
            return true;
        }

        // Значения проверяются теми же правилами, что на адресе: копия приходит от другой версии или
        // правленой руками, и негодный профиль обязан стать названным пропуском, а не исключением,
        // которое откатит восстановление целиком.
        static PrintProfileInput? ReadProfile(BackupPrintProfile item)
        {
            if (!Enum.TryParse<PaperFormat>(item.Paper, out var paper)
                || !Enum.TryParse<PageOrientation>(item.Orientation, out var orientation)
                || !Enum.TryParse<PageFit>(item.Fit, out var fit))
                return null;
            var margins = new PageMarginsMm(item.MarginTop, item.MarginBottom, item.MarginLeft, item.MarginRight);
            try
            {
                // Проверка — созданием: правило одно, и живёт оно в домене.
                var probe = RenditionLayoutProfile.Create(item.Name, paper, orientation, fit, margins);
                return new PrintProfileInput(probe.Name, paper, orientation, fit, margins);
            }
            catch (InvalidRequestException)
            {
                return null;
            }
        }
    }

    private sealed record PrintProfileInput(string Name, PaperFormat Paper, PageOrientation Orientation, PageFit Fit, PageMarginsMm Margins);
}
