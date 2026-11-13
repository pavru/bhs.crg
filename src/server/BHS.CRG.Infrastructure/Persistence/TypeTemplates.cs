using BHS.CRG.Application.Templates;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

/// <inheritdoc cref="ITypeTemplates" />
public sealed class TypeTemplates(AppDbContext db) : ITypeTemplates
{
    public async Task<IReadOnlyDictionary<Guid, bool>> ActiveByIdAsync(Guid documentTypeId, CancellationToken ct = default)
        => await db.Templates.AsNoTracking()
            .Where(t => t.DocumentTypeId == documentTypeId)
            // Select обязателен: ToDictionaryAsync сам не проецирует и читал бы строку целиком, с Typst-текстом.
            .Select(t => new { t.Id, t.IsActive })
            .ToDictionaryAsync(t => t.Id, t => t.IsActive, ct);
}
