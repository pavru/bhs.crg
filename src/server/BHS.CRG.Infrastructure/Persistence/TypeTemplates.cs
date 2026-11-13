using BHS.CRG.Application.Templates;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

/// <inheritdoc cref="ITypeTemplates" />
public sealed class TypeTemplates(AppDbContext db) : ITypeTemplates
{
    public async Task<IReadOnlyDictionary<Guid, bool>> ActiveByIdAsync(Guid documentTypeId, CancellationToken ct = default)
        => await db.Templates.AsNoTracking()
            .Where(t => t.DocumentTypeId == documentTypeId)
            .ToDictionaryAsync(t => t.Id, t => t.IsActive, ct);
}
