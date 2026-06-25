using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Documents;

public class DocumentTypeHandlers(IRepository<DocumentType> repo) :
    IRequestHandler<CreateDocumentTypeCommand, DocumentType>,
    IRequestHandler<UpdateDocumentTypeCommand, DocumentType>,
    IRequestHandler<UpdateDocumentTypeSchemaCommand, DocumentType>,
    IRequestHandler<SetDocumentTypeAbstractCommand, DocumentType>,
    IRequestHandler<DeleteDocumentTypeCommand>,
    IRequestHandler<ListDocumentTypesQuery, IReadOnlyList<DocumentType>>,
    IRequestHandler<GetDocumentTypeQuery, DocumentType?>
{
    public async Task<DocumentType> Handle(CreateDocumentTypeCommand cmd, CancellationToken ct)
    {
        var dt = DocumentType.Create(cmd.Name, cmd.Code, cmd.Kind, cmd.ParentId, cmd.Schema, cmd.IsAbstract);
        await repo.AddAsync(dt, ct);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    public async Task<DocumentType> Handle(UpdateDocumentTypeCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"DocumentType {cmd.Id} not found");
        // Prevent cycles: parentId must not be a descendant of this type
        if (cmd.ParentId.HasValue)
        {
            var all = await repo.GetAllAsync(ct);
            if (IsDescendant(cmd.ParentId.Value, cmd.Id, all))
                throw new InvalidOperationException("Нельзя установить дочерний тип в качестве родителя — возникнет цикл.");
        }
        dt.Rename(cmd.Name, cmd.Code);
        dt.SetParent(cmd.ParentId);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    private static bool IsDescendant(Guid candidateId, Guid ancestorId, IReadOnlyList<DocumentType> all)
    {
        var visited = new HashSet<Guid>();
        var current = candidateId;
        while (true)
        {
            if (current == ancestorId) return true;
            if (!visited.Add(current)) return false;
            var parent = all.FirstOrDefault(x => x.Id == current)?.ParentId;
            if (parent is null) return false;
            current = parent.Value;
        }
    }

    public async Task<DocumentType> Handle(UpdateDocumentTypeSchemaCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"DocumentType {cmd.Id} not found");
        dt.UpdateSchema(cmd.Schema);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    public async Task<DocumentType> Handle(SetDocumentTypeAbstractCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"DocumentType {cmd.Id} not found");
        dt.SetAbstract(cmd.IsAbstract);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    public async Task Handle(DeleteDocumentTypeCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        var all = await repo.GetAllAsync(ct);
        if (all.Any(x => x.ParentId == cmd.Id))
            throw new InvalidOperationException("Нельзя удалить тип, от которого наследуются другие типы.");
        repo.Remove(dt);
        await repo.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DocumentType>> Handle(ListDocumentTypesQuery q, CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        return q.Kind is null ? all : all.Where(x => x.Kind == q.Kind).ToList();
    }

    public Task<DocumentType?> Handle(GetDocumentTypeQuery q, CancellationToken ct)
        => repo.GetByIdAsync(q.Id, ct);
}

public class ConstructionHandlers(
    IRepository<Construction> constructionRepo,
    IRepository<Section> sectionRepo) :
    IRequestHandler<CreateConstructionCommand, Construction>,
    IRequestHandler<RenameConstructionCommand, Construction>,
    IRequestHandler<DeleteConstructionCommand>,
    IRequestHandler<GetConstructionQuery, Construction?>,
    IRequestHandler<ListConstructionsQuery, IReadOnlyList<Construction>>,
    IRequestHandler<CreateSectionCommand, Section>,
    IRequestHandler<RenameSectionCommand, Section>,
    IRequestHandler<DeleteSectionCommand>
{
    public async Task<Construction> Handle(CreateConstructionCommand cmd, CancellationToken ct)
    {
        var c = Construction.Create(cmd.Name, cmd.UserId);
        await constructionRepo.AddAsync(c, ct);
        await constructionRepo.SaveChangesAsync(ct);
        return c;
    }

    public async Task<Construction> Handle(RenameConstructionCommand cmd, CancellationToken ct)
    {
        var c = await constructionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        c.Rename(cmd.Name);
        constructionRepo.Update(c);
        await constructionRepo.SaveChangesAsync(ct);
        return c;
    }

    public async Task Handle(DeleteConstructionCommand cmd, CancellationToken ct)
    {
        var c = await constructionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        constructionRepo.Remove(c);
        await constructionRepo.SaveChangesAsync(ct);
    }

    public Task<Construction?> Handle(GetConstructionQuery q, CancellationToken ct)
        => constructionRepo.GetByIdAsync(q.Id, ct);

    public Task<IReadOnlyList<Construction>> Handle(ListConstructionsQuery q, CancellationToken ct)
        => constructionRepo.GetAllAsync(ct);

    public async Task<Section> Handle(CreateSectionCommand cmd, CancellationToken ct)
    {
        _ = await constructionRepo.GetByIdAsync(cmd.ConstructionId, ct)
            ?? throw new KeyNotFoundException("Construction not found");
        var section = Section.Create(cmd.ConstructionId, cmd.Name);
        await sectionRepo.AddAsync(section, ct);
        await sectionRepo.SaveChangesAsync(ct);
        return section;
    }

    public async Task<Section> Handle(RenameSectionCommand cmd, CancellationToken ct)
    {
        var s = await sectionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        s.Rename(cmd.Name);
        sectionRepo.Update(s);
        await sectionRepo.SaveChangesAsync(ct);
        return s;
    }

    public async Task Handle(DeleteSectionCommand cmd, CancellationToken ct)
    {
        var s = await sectionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        sectionRepo.Remove(s);
        await sectionRepo.SaveChangesAsync(ct);
    }
}

public class DocumentSetHandlers(
    IRepository<DocumentSet> setRepo,
    IRepository<Section> sectionRepo,
    IRepository<DocumentInstance> instRepo,
    IBlobStorage blobStorage) :
    IRequestHandler<CreateDocumentSetCommand, DocumentSet>,
    IRequestHandler<RenameDocumentSetCommand, DocumentSet>,
    IRequestHandler<DeleteDocumentSetCommand>,
    IRequestHandler<GetDocumentSetQuery, DocumentSet?>,
    IRequestHandler<ListAvailableInstancesQuery, IReadOnlyList<DocumentInstance>>,
    IRequestHandler<AddDocumentToSetCommand, DocumentInstance>,
    IRequestHandler<RenameDocumentInstanceCommand, DocumentInstance>,
    IRequestHandler<DeleteDocumentInstanceCommand>,
    IRequestHandler<UpdateRequisitesCommand, DocumentInstance>,
    IRequestHandler<UpdateEntityRefsCommand, DocumentInstance>,
    IRequestHandler<UpdatePluginDataCommand, DocumentInstance>,
    IRequestHandler<GetDocumentInstanceQuery, DocumentInstance?>,
    IRequestHandler<SetDocumentTemplateCommand, DocumentInstance>
{
    public async Task<DocumentSet> Handle(CreateDocumentSetCommand cmd, CancellationToken ct)
    {
        var set = DocumentSet.Create(cmd.SectionId, cmd.Name);
        await setRepo.AddAsync(set, ct);
        await setRepo.SaveChangesAsync(ct);
        return set;
    }

    public async Task<DocumentSet> Handle(RenameDocumentSetCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        set.Rename(cmd.Name);
        setRepo.Update(set);
        await setRepo.SaveChangesAsync(ct);
        return set;
    }

    public async Task Handle(DeleteDocumentSetCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        setRepo.Remove(set);
        await setRepo.SaveChangesAsync(ct);
    }

    public Task<DocumentSet?> Handle(GetDocumentSetQuery q, CancellationToken ct)
        => setRepo.GetByIdAsync(q.Id, ct);

    public async Task<IReadOnlyList<DocumentInstance>> Handle(ListAvailableInstancesQuery q, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(q.SetId, ct) ?? throw new KeyNotFoundException();
        var section = await sectionRepo.GetByIdAsync(set.SectionId, ct) ?? throw new KeyNotFoundException();
        var constructionId = section.ConstructionId;

        var sectionIds = (await sectionRepo.FindAsync(s => s.ConstructionId == constructionId, ct))
            .Select(s => s.Id)
            .ToHashSet();

        var setIds = (await setRepo.FindAsync(s => sectionIds.Contains(s.SectionId), ct))
            .Select(s => s.Id)
            .ToHashSet();

        return await instRepo.FindAsync(i => setIds.Contains(i.DocumentSetId), ct);
    }

    public async Task<DocumentInstance> Handle(AddDocumentToSetCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.DocumentSetId, ct)
            ?? throw new KeyNotFoundException();
        var inst = DocumentInstance.Create(cmd.DocumentSetId, cmd.DocumentTypeId);
        set.TouchUpdatedAt();
        await instRepo.AddAsync(inst, ct);
        await instRepo.SaveChangesAsync(ct);
        return inst;
    }

    public async Task<DocumentInstance> Handle(RenameDocumentInstanceCommand cmd, CancellationToken ct)
    {
        var inst = await instRepo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        inst.Rename(cmd.Name);
        instRepo.Update(inst);
        await instRepo.SaveChangesAsync(ct);
        return inst;
    }

    public async Task Handle(DeleteDocumentInstanceCommand cmd, CancellationToken ct)
    {
        var inst = await instRepo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        instRepo.Remove(inst);
        await instRepo.SaveChangesAsync(ct);
    }

    public async Task<DocumentInstance> Handle(UpdateRequisitesCommand cmd, CancellationToken ct)
    {
        var inst = await instRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new KeyNotFoundException();
        var blobs = inst.ResetToDraft();
        inst.UpdateRequisites(cmd.Requisites);
        instRepo.Update(inst);
        await instRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return inst;
    }

    public async Task<DocumentInstance> Handle(UpdateEntityRefsCommand cmd, CancellationToken ct)
    {
        var inst = await instRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new KeyNotFoundException();
        var blobs = inst.ResetToDraft();
        inst.UpdateEntityRefs(cmd.EntityRefs);
        instRepo.Update(inst);
        await instRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return inst;
    }

    public async Task<DocumentInstance> Handle(UpdatePluginDataCommand cmd, CancellationToken ct)
    {
        var inst = await instRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new KeyNotFoundException();
        var blobs = inst.ResetToDraft();
        inst.UpdatePluginData(cmd.PluginData);
        instRepo.Update(inst);
        await instRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return inst;
    }

    public Task<DocumentInstance?> Handle(GetDocumentInstanceQuery q, CancellationToken ct)
        => instRepo.GetByIdAsync(q.Id, ct);

    public async Task<DocumentInstance> Handle(SetDocumentTemplateCommand cmd, CancellationToken ct)
    {
        var inst = await instRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new KeyNotFoundException();
        var blobs = inst.ResetToDraft();
        inst.SetTemplate(cmd.TemplateId);
        instRepo.Update(inst);
        await instRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return inst;
    }
}

public class CommonDataHandlers(
    IRepository<CommonDataEntry> repo,
    IRepository<DocumentSet> setRepo,
    IRepository<Section> sectionRepo) :
    IRequestHandler<CreateCommonDataEntryCommand, CommonDataEntry>,
    IRequestHandler<UpdateCommonDataEntryCommand, CommonDataEntry>,
    IRequestHandler<DeleteCommonDataEntryCommand>,
    IRequestHandler<ListCommonDataEntriesQuery, IReadOnlyList<CommonDataEntry>>,
    IRequestHandler<ResolveCommonDataForSetQuery, IReadOnlyList<CommonDataEntryWithScope>>
{
    public async Task<CommonDataEntry> Handle(CreateCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = CommonDataEntry.Create(cmd.DisplayName, cmd.CompositeTypeId, cmd.Data, cmd.Scope, cmd.ScopeId);
        await repo.AddAsync(entry, ct);
        await repo.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<CommonDataEntry> Handle(UpdateCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        entry.Update(cmd.DisplayName, cmd.Data);
        repo.Update(entry);
        await repo.SaveChangesAsync(ct);
        return entry;
    }

    public async Task Handle(DeleteCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        repo.Remove(entry);
        await repo.SaveChangesAsync(ct);
    }

    public Task<IReadOnlyList<CommonDataEntry>> Handle(ListCommonDataEntriesQuery q, CancellationToken ct)
    {
        var scope = q.Scope;
        var scopeId = q.ScopeId;
        var typeId = q.CompositeTypeId;
        return repo.FindAsync(e =>
            (!scope.HasValue || e.Scope == scope.Value) &&
            (!scopeId.HasValue || e.ScopeId == scopeId.Value) &&
            (!typeId.HasValue || e.CompositeTypeId == typeId.Value), ct);
    }

    public async Task<IReadOnlyList<CommonDataEntryWithScope>> Handle(
        ResolveCommonDataForSetQuery q, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(q.SetId, ct) ?? throw new KeyNotFoundException("DocumentSet not found");
        var section = await sectionRepo.GetByIdAsync(set.SectionId, ct) ?? throw new KeyNotFoundException("Section not found");
        var constructionId = section.ConstructionId;
        var setId = q.SetId;
        var sectionId = set.SectionId;
        var typeId = q.CompositeTypeId;

        var relevant = await repo.FindAsync(e =>
            ((e.Scope == CatalogScope.Set         && e.ScopeId == setId) ||
             (e.Scope == CatalogScope.Section     && e.ScopeId == sectionId) ||
             (e.Scope == CatalogScope.Construction && e.ScopeId == constructionId) ||
             e.Scope == CatalogScope.System) &&
            (!typeId.HasValue || e.CompositeTypeId == typeId.Value), ct);

        return relevant
            .Select(e => new CommonDataEntryWithScope(
                e.Id, e.DisplayName, e.CompositeTypeId, e.Data,
                e.Scope, e.ScopeId, (int)e.Scope,
                e.CreatedAt, e.UpdatedAt))
            .OrderBy(e => e.Priority)
            .ThenBy(e => e.DisplayName)
            .ToList();
    }
}
