using BHS.CRG.Domain.Objects;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Ссылки восстанавливаемых документов на шаблоны — часть <see cref="BackupService" />.
///
/// <para>Два правила, оба — по уже восстановленным шаблонам: выбор погашенной версии пересаживается
/// к действующей (id-template-version-integrity, решение 0.4), а ссылки на шаблоны, которых у типа документа нет,
/// убираются (issue #1960). Вторые несут все копии рабочей базы, снятые до разовой чистки: влитые
/// дословно, они снова жили бы там, куда запись их уже не пускает. Отказать здесь некому — поэтому
/// ссылка убирается, а их число называется в отчёте.</para>
/// </summary>
public partial class BackupService
{
    private async Task<RestoredTemplateRefs> TemplateRefsAsync(CancellationToken ct)
    {
        var rows = await db.Templates.AsNoTracking().Select(t => new { t.DocumentTypeId, t.Id }).ToListAsync(ct);
        var byType = rows.GroupBy(t => t.DocumentTypeId)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<Guid>)g.Select(t => t.Id).ToHashSet());
        return new(await TemplateRepinMapAsync(ct), byType);
    }

    /// <param name="known">Шаблоны ПО ТИПУ документа: ссылка на шаблон чужого типа такая же висячая —
    /// генерация берёт кандидатов только своего типа.</param>
    private sealed class RestoredTemplateRefs(
        IReadOnlyDictionary<Guid, Guid> repin, IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> known)
    {
        private static readonly IReadOnlySet<Guid> None = new HashSet<Guid>();

        private int _repinned, _forgotten;

        public IReadOnlyDictionary<Guid, Guid> Repin => repin;

        public void Settle(DomainObject doc)
        {
            if (doc.RepinTemplates(repin, selectionOnly: true)) _repinned++;
            _forgotten += doc.ForgetUnknownTemplates(known.GetValueOrDefault(doc.CompositeTypeId) ?? None);
        }

        public void Warn(List<string> warnings)
        {
            WarnRepinned(warnings, _repinned);
            BackupService.Warn(warnings, _forgotten, "ссылок документов на шаблоны",
                "таких шаблонов нет ни в копии, ни в системе");
        }
    }
}
