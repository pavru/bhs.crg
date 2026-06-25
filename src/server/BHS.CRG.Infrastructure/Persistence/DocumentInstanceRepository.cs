using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

public class DocumentInstanceRepository(AppDbContext db) : Repository<DocumentInstance>(db)
{
    public override Task<DocumentInstance?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Db.Set<DocumentInstance>()
            .Include(i => i.GeneratedFiles)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
}
