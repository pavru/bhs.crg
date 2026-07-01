using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using MediatR;

namespace BHS.CRG.Application.Catalog;

public class CatalogHandlers(IRepository<CatalogEntity> repo) :
    IRequestHandler<CreateCatalogEntityCommand, CatalogEntity>,
    IRequestHandler<UpdateCatalogEntityCommand, CatalogEntity>,
    IRequestHandler<DeleteCatalogEntityCommand>,
    IRequestHandler<GetCatalogEntityQuery, CatalogEntity?>,
    IRequestHandler<ListCatalogEntitiesQuery, IReadOnlyList<CatalogEntity>>
{
    public async Task<CatalogEntity> Handle(CreateCatalogEntityCommand cmd, CancellationToken ct)
    {
        var entity = CatalogEntity.Create(cmd.EntityType, cmd.DisplayName, cmd.Data, cmd.OwnerId);
        await repo.AddAsync(entity, ct);
        await repo.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<CatalogEntity> Handle(UpdateCatalogEntityCommand cmd, CancellationToken ct)
    {
        var entity = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"CatalogEntity {cmd.Id} not found");
        entity.Update(cmd.DisplayName, cmd.Data);
        repo.Update(entity);
        await repo.SaveChangesAsync(ct);
        return entity;
    }

    public async Task Handle(DeleteCatalogEntityCommand cmd, CancellationToken ct)
    {
        var entity = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"CatalogEntity {cmd.Id} not found");
        repo.Remove(entity);
        await repo.SaveChangesAsync(ct);
    }

    public Task<CatalogEntity?> Handle(GetCatalogEntityQuery query, CancellationToken ct)
        => repo.GetByIdAsync(query.Id, ct);

    public Task<IReadOnlyList<CatalogEntity>> Handle(ListCatalogEntitiesQuery query, CancellationToken ct)
    {
        var type = query.EntityType;
        var owner = query.OwnerId;
        return repo.FindAsync(e =>
            (type == null || e.EntityType == type) &&
            (owner == null || e.OwnerId == owner), ct);
    }
}

public class PrimitiveTypeHandlers(IRepository<PrimitiveType> repo) :
    IRequestHandler<ListPrimitiveTypesQuery, IReadOnlyList<PrimitiveType>>,
    IRequestHandler<CreatePrimitiveTypeCommand, PrimitiveType>,
    IRequestHandler<UpdatePrimitiveTypeCommand, PrimitiveType>,
    IRequestHandler<SetPrimitiveTypeGroupCommand, PrimitiveType>,
    IRequestHandler<DeletePrimitiveTypeCommand>
{
    public Task<IReadOnlyList<PrimitiveType>> Handle(ListPrimitiveTypesQuery _, CancellationToken ct)
        => repo.GetAllAsync(ct);

    public async Task<PrimitiveType> Handle(CreatePrimitiveTypeCommand cmd, CancellationToken ct)
    {
        await EnsureCodeUniqueAsync(cmd.Code, excludeId: null, ct);
        var pt = PrimitiveType.Create(cmd.Name, cmd.Code.Trim(), cmd.BaseType, cmd.Description, cmd.Constraints, cmd.AllowedTags);
        await repo.AddAsync(pt, ct);
        await repo.SaveChangesAsync(ct);
        return pt;
    }

    public async Task<PrimitiveType> Handle(UpdatePrimitiveTypeCommand cmd, CancellationToken ct)
    {
        var pt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"PrimitiveType {cmd.Id} not found");
        await EnsureCodeUniqueAsync(cmd.Code, excludeId: cmd.Id, ct);
        pt.Update(cmd.Name, cmd.Code.Trim(), cmd.Description, cmd.Constraints, cmd.AllowedTags);
        repo.Update(pt);
        await repo.SaveChangesAsync(ct);
        return pt;
    }

    // Код типа поля должен быть уникален (без учёта регистра и краёв).
    private async Task EnsureCodeUniqueAsync(string code, Guid? excludeId, CancellationToken ct)
    {
        var nCode = code.Trim().ToLowerInvariant();
        var all = await repo.GetAllAsync(ct);
        if (all.Any(t => (!excludeId.HasValue || t.Id != excludeId.Value) && t.Code.Trim().ToLowerInvariant() == nCode))
            throw new ArgumentException($"Тип поля с кодом «{code.Trim()}» уже существует.");
    }

    public async Task<PrimitiveType> Handle(SetPrimitiveTypeGroupCommand cmd, CancellationToken ct)
    {
        var pt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"PrimitiveType {cmd.Id} not found");
        pt.SetGroup(cmd.Group);
        repo.Update(pt);
        await repo.SaveChangesAsync(ct);
        return pt;
    }

    public async Task Handle(DeletePrimitiveTypeCommand cmd, CancellationToken ct)
    {
        var pt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"PrimitiveType {cmd.Id} not found");
        repo.Remove(pt);
        await repo.SaveChangesAsync(ct);
    }
}
