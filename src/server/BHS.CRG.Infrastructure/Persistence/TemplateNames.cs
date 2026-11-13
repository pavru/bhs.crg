using BHS.CRG.Application.Templates;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

/// <inheritdoc cref="ITemplateNames" />
public sealed class TemplateNames(AppDbContext db) : ITemplateNames
{
    public Task<bool> IsTakenAsync(Guid documentTypeId, string name, CancellationToken ct = default)
        => db.Templates.AnyAsync(t => t.DocumentTypeId == documentTypeId && t.Name == name, ct);

    public async Task<IReadOnlySet<string>> StartingWithAsync(Guid documentTypeId, string prefix, CancellationToken ct = default)
        => (await db.Templates
            .Where(t => t.DocumentTypeId == documentTypeId && t.Name.StartsWith(prefix))
            .Select(t => t.Name).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
}
