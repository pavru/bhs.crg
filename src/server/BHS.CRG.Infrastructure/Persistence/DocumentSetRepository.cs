using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

public class DocumentSetRepository(AppDbContext db) : Repository<DocumentSet>(db)
{
    public override Task<DocumentSet?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Db.Set<DocumentSet>()
            .Include(s => s.Instances.OrderBy(i => i.SortOrder))
            .ThenInclude(i => i.GeneratedFiles)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    public override async Task<IReadOnlyList<DocumentSet>> GetAllAsync(CancellationToken ct = default)
        => await Db.Set<DocumentSet>()
            .Include(s => s.Instances.OrderBy(i => i.SortOrder))
            .ThenInclude(i => i.GeneratedFiles)
            .ToListAsync(ct);
}
