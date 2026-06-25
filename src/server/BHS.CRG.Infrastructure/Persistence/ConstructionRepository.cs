using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

public class ConstructionRepository(AppDbContext db) : Repository<Construction>(db)
{
    public override Task<Construction?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Db.Set<Construction>()
            .Include(c => c.Sections)
            .ThenInclude(s => s.DocumentSets)
            .ThenInclude(ds => ds.Instances)
            .ThenInclude(i => i.GeneratedFiles)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

    public override async Task<IReadOnlyList<Construction>> GetAllAsync(CancellationToken ct = default)
        => await Db.Set<Construction>()
            .Include(c => c.Sections)
            .ThenInclude(s => s.DocumentSets)
            .ToListAsync(ct);
}
