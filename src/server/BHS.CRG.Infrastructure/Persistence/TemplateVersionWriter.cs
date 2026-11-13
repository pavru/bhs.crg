using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

/// <inheritdoc cref="ITemplateVersionWriter" />
public sealed class TemplateVersionWriter(AppDbContext db) : ITemplateVersionWriter
{
    public async Task SaveRetiredThenInsertAsync(
        IReadOnlyCollection<Template> created, string conflictMessage, CancellationToken ct = default)
    {
        // Своя транзакция — если вызывающий не открыл её сам: две записи обязаны лечь вместе, иначе
        // упавшая вставка оставила бы шаблон вовсе без действующей версии.
        await using var own = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            await db.SaveChangesAsync(ct);
            db.Templates.AddRange(created);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbFailure.IsUniqueViolationOf(
                   ex, TemplateConfiguration.OneActiveIndex, TemplateConfiguration.VersionIndex))
        {
            // Проиграли гонку: строку заняли между чтением и записью. Ловятся ИМЕННО эти индексы —
            // чужое столкновение, объявленное «шаблон изменили», увело бы разбирательство в сторону.
            foreach (var t in created) db.Entry(t).State = EntityState.Detached;
            throw new ConflictException(conflictMessage);
        }
        if (own is not null) await own.CommitAsync(ct);
    }
}
