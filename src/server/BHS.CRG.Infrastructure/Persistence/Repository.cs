using System.Linq.Expressions;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

public class Repository<T> : IRepository<T> where T : Entity
{
    protected readonly AppDbContext Db;
    private readonly DbSet<T> _set;

    public Repository(AppDbContext db)
    {
        Db = db;
        _set = db.Set<T>();
    }

    public virtual Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => _set.FirstOrDefaultAsync(e => e.Id == id, ct);

    public virtual async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default)
        => await _set.ToListAsync(ct);

    public virtual async Task<IReadOnlyList<T>> FindAsync(
        Expression<Func<T, bool>> predicate, CancellationToken ct = default)
        => await _set.AsNoTracking().Where(predicate).ToListAsync(ct);

    public async Task AddAsync(T entity, CancellationToken ct = default)
        => await _set.AddAsync(entity, ct);

    public void Update(T entity) => _set.Update(entity);

    public void Remove(T entity) => _set.Remove(entity);

    public Task SaveChangesAsync(CancellationToken ct = default)
        => Db.SaveChangesAsync(ct);
}
