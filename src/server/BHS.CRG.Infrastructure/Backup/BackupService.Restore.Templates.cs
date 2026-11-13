using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Templates;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Шаблоны при восстановлении — часть <see cref="BackupService" /> (id-template-version-integrity, D7).
///
/// <para>Копия, снятая до уникальных индексов версий, может нести две действующие версии одного
/// шаблона или два номера v3. Влитая как есть, она упала бы на индексе, и администратор увидел бы
/// «An error occurred while saving the entity changes» — отказ, переодетый в непонятное. Поэтому группы
/// (тип документа, название) — существующие строки вместе со строками копии — приводятся к правилу
/// разовой починки (<see cref="TemplateVersionRepair" />) до записи, а каждое изменение называется в
/// отчёте.</para>
///
/// <para><b>Порядок записи.</b> Частичный индекс проверяется на каждом операторе, а порядок команд
/// внутри одного сохранения EF не гарантирует (D4). Поэтому существующие строки затронутых групп
/// сначала «паркуются» — гаснут и получают отрицательный номер (уникальный, ведь в базе номера уже
/// уникальны), — и только затем пишутся итоговые значения и вставки: ни одна промежуточная команда
/// с итоговыми строками не столкнётся.</para>
/// </summary>
public partial class BackupService
{
    private async Task RestoreTemplatesAsync(
        BackupTemplate[] items, RestoreStats stats, List<string> warnings, CancellationToken ct)
    {
        var validDocTypeIds = await db.DocumentTypes.Select(e => e.Id).ToHashSetAsync(ct);
        var fromCopy = new List<Template>();
        foreach (var item in items)
        {
            if (!validDocTypeIds.Contains(item.DocumentTypeId))
            {
                warnings.Add($"Шаблон «{item.Name}» v{item.Version}: тип документа {item.DocumentTypeId} не найден, пропущен.");
                continue;
            }
            fromCopy.Add(Template.Restore(
                item.Id, item.DocumentTypeId, item.Name, item.Content, item.Version,
                item.IsActive, item.IsDefault,
                item.CreatedAt, item.UpdatedAt, item.Parameters, item.Comment));
        }
        if (fromCopy.Count == 0) return;

        // Затронутые группы — группы строк копии и прежние группы тех же строк здесь.
        var here = await db.Templates.AsNoTracking()
            .Select(t => new { t.Id, t.DocumentTypeId, t.Name }).ToListAsync(ct);
        var existingIds = here.Select(h => h.Id).ToHashSet();
        var copyIds = fromCopy.Select(t => t.Id).ToHashSet();
        var groups = fromCopy.Select(t => (t.DocumentTypeId, t.Name))
            .Concat(here.Where(h => copyIds.Contains(h.Id)).Select(h => (h.DocumentTypeId, h.Name)))
            .ToHashSet();
        var involved = here.Where(h => copyIds.Contains(h.Id) || groups.Contains((h.DocumentTypeId, h.Name)))
            .Select(h => h.Id).ToList();
        var hereOnlyIds = involved.Where(id => !copyIds.Contains(id)).ToList();
        var hereOnly = await db.Templates.AsNoTracking().Where(t => hereOnlyIds.Contains(t.Id)).ToListAsync(ct);

        var all = fromCopy.Concat(hereOnly).ToList();
        foreach (var change in TemplateVersionRepair.Apply(all))
            warnings.Add(change.Describe());

        // Парковка — до итоговой записи (см. описание класса).
        if (involved.Count > 0)
            await db.Templates.Where(t => involved.Contains(t.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.IsActive, false)
                    .SetProperty(t => t.Version, t => -t.Version), ct);

        foreach (var entity in all)
            db.Entry(entity).State = existingIds.Contains(entity.Id) ? EntityState.Modified : EntityState.Added;
        foreach (var entity in fromCopy)
            if (existingIds.Contains(entity.Id)) stats.TemplatesUpdated++; else stats.TemplatesCreated++;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Куда пересаживать закрепления документов копии: погашенная версия шаблона — к его действующей
    /// (решение 0.4). Строится по уже восстановленным шаблонам, поэтому учитывает и приведение выше.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, Guid>> TemplateRepinMapAsync(CancellationToken ct)
    {
        var rows = await db.Templates.AsNoTracking()
            .Select(t => new { t.Id, t.DocumentTypeId, t.Name, t.IsActive }).ToListAsync(ct);
        return TemplateVersionRepair.ActiveByRetired(rows.Select(r => (r.Id, r.DocumentTypeId, r.Name, r.IsActive)));
    }

    private static void WarnRepinned(List<string> warnings, int count)
    {
        if (count > 0)
            warnings.Add($"Документы комплектов: у {count} выбранные погашенные версии шаблонов заменены " +
                         "действующими версиями тех же шаблонов.");
    }
}
