using BHS.CRG.Application.Common;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
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

public class EnumTypeHandlers(IRepository<EnumType> repo, IRepository<DocumentType> docTypeRepo) :
    IRequestHandler<ListEnumTypesQuery, IReadOnlyList<EnumType>>,
    IRequestHandler<CreateEnumTypeCommand, EnumType>,
    IRequestHandler<UpdateEnumTypeCommand, EnumType>,
    IRequestHandler<SetEnumTypeGroupCommand, EnumType>,
    IRequestHandler<DeleteEnumTypeCommand>
{
    public Task<IReadOnlyList<EnumType>> Handle(ListEnumTypesQuery _, CancellationToken ct)
        => repo.GetAllAsync(ct);

    public async Task<EnumType> Handle(CreateEnumTypeCommand cmd, CancellationToken ct)
    {
        await EnsureCodeUniqueAsync(cmd.Code, excludeId: null, ct);
        var et = EnumType.Create(cmd.Name, cmd.Code.Trim(), cmd.Description, cmd.Values);
        await repo.AddAsync(et, ct);
        await repo.SaveChangesAsync(ct);
        return et;
    }

    public async Task<EnumType> Handle(UpdateEnumTypeCommand cmd, CancellationToken ct)
    {
        var et = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"EnumType {cmd.Id} not found");
        await EnsureCodeUniqueAsync(cmd.Code, excludeId: cmd.Id, ct);
        et.Update(cmd.Name, cmd.Code.Trim(), cmd.Description, cmd.Values);
        repo.Update(et);
        await repo.SaveChangesAsync(ct);
        return et;
    }

    // Код типа перечисления должен быть уникален (без учёта регистра и краёв).
    private async Task EnsureCodeUniqueAsync(string code, Guid? excludeId, CancellationToken ct)
    {
        var nCode = code.Trim().ToLowerInvariant();
        var all = await repo.GetAllAsync(ct);
        if (all.Any(t => (!excludeId.HasValue || t.Id != excludeId.Value) && t.Code.Trim().ToLowerInvariant() == nCode))
            throw new ArgumentException($"Тип перечисления с кодом «{code.Trim()}» уже существует.");
    }

    public async Task<EnumType> Handle(SetEnumTypeGroupCommand cmd, CancellationToken ct)
    {
        var et = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"EnumType {cmd.Id} not found");
        et.SetGroup(cmd.Group);
        repo.Update(et);
        await repo.SaveChangesAsync(ct);
        return et;
    }

    // issue #59 (по прецеденту issue #57 — та же класса бага у DocumentType): удаление типа
    // перечисления, используемого в схеме какого-либо типа документа, оставляло бы enum-поле с
    // висячим typeId — резолв вариантов молча пропадал бы. Проверяем перед удалением.
    public async Task Handle(DeleteEnumTypeCommand cmd, CancellationToken ct)
    {
        var et = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"EnumType {cmd.Id} not found");
        var allDocTypes = await docTypeRepo.GetAllAsync(ct);
        var usedIn = allDocTypes.Where(t => DocumentTypeSchemaReader.ReferencesEnumType(t.Schema, cmd.Id)).ToList();
        if (usedIn.Count > 0)
            throw new InvalidOperationException(
                $"Нельзя удалить тип перечисления — используется в схеме: {string.Join(", ", usedIn.Select(t => t.Name))}.");
        repo.Remove(et);
        await repo.SaveChangesAsync(ct);
    }
}
