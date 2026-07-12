using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Documents;

public class DocumentTypeHandlers(
    IRepository<DocumentType> repo,
    IRepository<DocumentInstance> instanceRepo,
    IRepository<Template> templateRepo,
    IRepository<QualityDocument> qualityDocRepo,
    IRepository<CommonDataEntry> commonDataEntryRepo,
    IDataSetService dataSetService) :
    IRequestHandler<CreateDocumentTypeCommand, DocumentType>,
    IRequestHandler<UpdateDocumentTypeCommand, DocumentType>,
    IRequestHandler<UpdateDocumentTypeSchemaCommand, DocumentType>,
    IRequestHandler<SetDocumentTypeAbstractCommand, DocumentType>,
    IRequestHandler<SetDocumentTypeGroupCommand, DocumentType>,
    IRequestHandler<DeleteDocumentTypeCommand>,
    IRequestHandler<ListDocumentTypesQuery, IReadOnlyList<DocumentType>>,
    IRequestHandler<GetDocumentTypeQuery, DocumentType?>
{
    public async Task<DocumentType> Handle(CreateDocumentTypeCommand cmd, CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        EnsureUnique(all, cmd.Name, cmd.Code, excludeId: null);

        var dt = DocumentType.Create(cmd.Name.Trim(), cmd.Code.Trim(), cmd.Kind, cmd.ParentId, cmd.Schema, cmd.IsAbstract);
        await repo.AddAsync(dt, ct);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    public async Task<DocumentType> Handle(UpdateDocumentTypeCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"DocumentType {cmd.Id} not found");
        var all = await repo.GetAllAsync(ct);
        EnsureUnique(all, cmd.Name, cmd.Code, excludeId: cmd.Id);
        // Prevent cycles: parentId must not be a descendant of this type
        if (cmd.ParentId.HasValue && IsDescendant(cmd.ParentId.Value, cmd.Id, all))
            throw new InvalidOperationException("Нельзя установить дочерний тип в качестве родителя — возникнет цикл.");

        dt.Rename(cmd.Name.Trim(), cmd.Code.Trim());
        dt.SetParent(cmd.ParentId);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    // Код и имя типа документа должны быть уникальны (без учёта регистра и краёв).
    private static void EnsureUnique(IReadOnlyList<DocumentType> all, string name, string code, Guid? excludeId)
    {
        static string N(string s) => s.Trim().ToLowerInvariant();
        var nName = N(name);
        var nCode = N(code);
        foreach (var t in all)
        {
            if (excludeId.HasValue && t.Id == excludeId.Value) continue;
            if (N(t.Code) == nCode)
                throw new ArgumentException($"Тип документа с кодом «{code.Trim()}» уже существует.");
            if (N(t.Name) == nName)
                throw new ArgumentException($"Тип документа с именем «{name.Trim()}» уже существует.");
        }
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

    public async Task<DocumentType> Handle(SetDocumentTypeGroupCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"DocumentType {cmd.Id} not found");
        dt.SetGroup(cmd.Group);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    // issue #57: удаление типа не проверяло использование — документы/шаблоны/записи каталога,
    // созданные на его основе, оставались с "висячей" ссылкой (ни FK в БД, ни прикладной проверки не
    // было ни для одной из этих точек). Ниже — прикладная проверка (по образцу уже существовавшей
    // ParentId-проверки и DataSetSourceService.DeleteSourceAsync), а не настоящий FK в БД: единственный
    // реальный FK в модели — self-reference ParentId (дерево внутри одной таблицы), все остальные
    // межагрегатные ссылки в проекте уже сознательно "мягкие" (CommonDataEntry.CompositeTypeId,
    // DataSetSource.MaterializeTypeId и т.д.) — вводить здесь исключение было бы немотивированным
    // расхождением с конвенцией, а не усилением защиты.
    public async Task Handle(DeleteDocumentTypeCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        var all = await repo.GetAllAsync(ct);
        if (all.Any(x => x.ParentId == cmd.Id))
            throw new InvalidOperationException("Нельзя удалить тип, от которого наследуются другие типы.");

        if ((await instanceRepo.FindAsync(i => i.DocumentTypeId == cmd.Id, ct)).Count > 0)
            throw new InvalidOperationException("Нельзя удалить тип — по нему уже созданы документы.");

        if ((await templateRepo.FindAsync(t => t.DocumentTypeId == cmd.Id, ct)).Count > 0)
            throw new InvalidOperationException("Нельзя удалить тип — для него есть шаблоны.");

        if ((await qualityDocRepo.FindAsync(q => q.DocumentTypeId == cmd.Id, ct)).Count > 0)
            throw new InvalidOperationException("Нельзя удалить тип — есть документы качества этого типа.");

        if ((await commonDataEntryRepo.FindAsync(e => e.CompositeTypeId == cmd.Id, ct)).Count > 0)
            throw new InvalidOperationException("Нельзя удалить тип — есть записи каталога этого типа.");

        if ((await dataSetService.ListTemplatesAsync(cmd.Id, ct)).Count > 0)
            throw new InvalidOperationException("Нельзя удалить тип — для него есть шаблоны привязки наборов данных.");

        if (await dataSetService.AnySourceMaterializedAsTypeAsync(cmd.Id, ct))
            throw new InvalidOperationException("Нельзя удалить тип — на него материализован источник набора данных.");

        // Тип может использоваться как составной подтип внутри схемы ДРУГОГО типа (complex/array/
        // doc-ref/doc-array поле с typeId == cmd.Id) — сам себя (собственную схему) не проверяем,
        // иначе self-referential составной тип (напр. дерево) стал бы неудаляемым навсегда.
        var usedInSchemas = all.Where(t => t.Id != cmd.Id && DocumentTypeSchemaReader.ReferencesType(t.Schema, cmd.Id)).ToList();
        if (usedInSchemas.Count > 0)
            throw new InvalidOperationException(
                $"Нельзя удалить тип — используется как составной подтип в схеме: {string.Join(", ", usedInSchemas.Select(t => t.Name))}.");

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
    IRequestHandler<ReorderDocumentInstancesCommand, DocumentSet>,
    IRequestHandler<RenameDocumentInstanceCommand, DocumentInstance>,
    IRequestHandler<DeleteDocumentInstanceCommand>,
    IRequestHandler<UpdateRequisitesCommand, DocumentInstance>,
    IRequestHandler<UpdatePluginDataCommand, DocumentInstance>,
    IRequestHandler<GetDocumentInstanceQuery, DocumentInstance?>,
    IRequestHandler<SetDocumentTemplateCommand, DocumentInstance>,
    IRequestHandler<SetDocumentTemplatesCommand, DocumentInstance>,
    IRequestHandler<SetDocumentTemplateParamsCommand, DocumentInstance>
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
        // Новый документ — в конец комплекта (порядок сборки задаётся SortOrder).
        var maxOrder = set.Instances.Count == 0 ? -1 : set.Instances.Max(i => i.SortOrder);
        inst.SetSortOrder(maxOrder + 1);
        set.TouchUpdatedAt();
        await instRepo.AddAsync(inst, ct);
        await instRepo.SaveChangesAsync(ct);
        return inst;
    }

    public async Task<DocumentSet> Handle(ReorderDocumentInstancesCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.SetId, ct) ?? throw new KeyNotFoundException();
        // Присваиваем SortOrder по позиции в переданном списке; отсутствующие в списке документы
        // (напр. добавленные параллельно) — в конец, сохраняя их относительный порядок.
        var order = cmd.OrderedInstanceIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var next = cmd.OrderedInstanceIds.Count;
        foreach (var inst in set.Instances.OrderBy(i => i.SortOrder))
            inst.SetSortOrder(order.TryGetValue(inst.Id, out var pos) ? pos : next++);
        await setRepo.SaveChangesAsync(ct);
        return set;
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

    public async Task<DocumentInstance> Handle(SetDocumentTemplatesCommand cmd, CancellationToken ct)
    {
        var inst = await instRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new KeyNotFoundException();
        var blobs = inst.ResetToDraft(); // смена набора шаблонов меняет вывод — в черновик
        inst.SetTemplateIds(cmd.TemplateIds);
        instRepo.Update(inst);
        await instRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return inst;
    }

    public async Task<DocumentInstance> Handle(SetDocumentTemplateParamsCommand cmd, CancellationToken ct)
    {
        var inst = await instRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new KeyNotFoundException();
        var blobs = inst.ResetToDraft(); // параметры влияют на вывод — сбрасываем в черновик
        inst.SetTemplateParams(cmd.Params);
        instRepo.Update(inst);
        await instRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return inst;
    }
}

public class CommonDataHandlers(
    IRepository<CommonDataEntry> repo,
    IRepository<DocumentSet> setRepo,
    IRepository<Section> sectionRepo,
    IDataSetService dataSetService) :
    IRequestHandler<CreateCommonDataEntryCommand, CommonDataEntry>,
    IRequestHandler<UpdateCommonDataEntryCommand, CommonDataEntry>,
    IRequestHandler<DeleteCommonDataEntryCommand>,
    IRequestHandler<ListCommonDataEntriesQuery, IReadOnlyList<CommonDataEntry>>,
    IRequestHandler<ResolveCommonDataForSetQuery, IReadOnlyList<CommonDataEntryWithScope>>,
    IRequestHandler<ResolveCommonDataForScopeQuery, IReadOnlyList<CommonDataEntryWithScope>>
{
    public async Task<CommonDataEntry> Handle(CreateCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = CommonDataEntry.Create(cmd.DisplayName, cmd.CompositeTypeId, cmd.Data, cmd.Scope, cmd.ScopeId, cmd.Aliases);
        await repo.AddAsync(entry, ct);
        await repo.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<CommonDataEntry> Handle(UpdateCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        var previews = await dataSetService.PreviewBindingsAsync(null, cmd.Id, ct);
        var data = previews.Count == 0 ? cmd.Data : CommonDataBindingMerge.Merge(cmd.Data, previews);
        entry.Update(cmd.DisplayName, data, cmd.Aliases);
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

    public async Task<IReadOnlyList<CommonDataEntryWithScope>> Handle(
        ResolveCommonDataForScopeQuery q, CancellationToken ct)
    {
        // Разрешаем родительскую цепочку скопа: Set→Section→Construction→System (issue #82).
        // Неразрешённые уровни — Guid.Empty: ни одна запись со ScopeId==Empty не совпадёт.
        Guid setId = Guid.Empty, sectionId = Guid.Empty, constructionId = Guid.Empty;
        switch (q.Scope)
        {
            case CatalogScope.Set when q.ScopeId is { } sid:
                setId = sid;
                var set = await setRepo.GetByIdAsync(sid, ct);
                if (set is not null)
                {
                    sectionId = set.SectionId;
                    var sec = await sectionRepo.GetByIdAsync(set.SectionId, ct);
                    if (sec is not null) constructionId = sec.ConstructionId;
                }
                break;
            case CatalogScope.Section when q.ScopeId is { } secId:
                sectionId = secId;
                var section = await sectionRepo.GetByIdAsync(secId, ct);
                if (section is not null) constructionId = section.ConstructionId;
                break;
            case CatalogScope.Construction when q.ScopeId is { } cid:
                constructionId = cid;
                break;
            // System — родителей нет.
        }
        var typeId = q.CompositeTypeId;

        var relevant = await repo.FindAsync(e =>
            ((e.Scope == CatalogScope.Set          && e.ScopeId == setId) ||
             (e.Scope == CatalogScope.Section       && e.ScopeId == sectionId) ||
             (e.Scope == CatalogScope.Construction  && e.ScopeId == constructionId) ||
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
