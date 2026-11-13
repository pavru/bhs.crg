using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Templates;

namespace BHS.CRG.Infrastructure.Persistence;

/// <inheritdoc cref="ITemplateVersionWriter" />
public sealed class TemplateVersionWriter(AppDbContext db) : ITemplateVersionWriter
{
    public async Task SaveRetiredThenInsertAsync(IReadOnlyCollection<Template> created, CancellationToken ct = default)
    {
        // Своя транзакция — если вызывающий не открыл её сам: две записи обязаны лечь вместе, иначе
        // упавшая вставка оставила бы шаблон вовсе без действующей версии.
        await using var own = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        await db.SaveChangesAsync(ct);
        db.Templates.AddRange(created);
        await db.SaveChangesAsync(ct);
        if (own is not null) await own.CommitAsync(ct);
    }
}
