using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Objects;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Documents;

public class DocumentTypeHandlers(
    IRepository<DocumentType> repo,
    IRepository<DomainObject> objectRepo,
    IRepository<Template> templateRepo,
    IRepository<QualityDocument> qualityDocRepo,
    IDataSetService dataSetService) :
    IRequestHandler<CreateDocumentTypeCommand, DocumentType>,
    IRequestHandler<UpdateDocumentTypeCommand, DocumentType>,
    IRequestHandler<UpdateDocumentTypeSchemaCommand, DocumentType>,
    IRequestHandler<SetDocumentTypeAbstractCommand, DocumentType>,
    IRequestHandler<SetDocumentTypeAllowsProxyCommand, DocumentType>,
    IRequestHandler<SetDocumentTypeGroupCommand, DocumentType>,
    IRequestHandler<DeleteDocumentTypeCommand>,
    IRequestHandler<ListDocumentTypesQuery, IReadOnlyList<DocumentType>>,
    IRequestHandler<GetDocumentTypeQuery, DocumentType?>,
    IRequestHandler<GetDocumentTypeUsageQuery, DocumentTypeUsage>
{
    public async Task<DocumentType> Handle(CreateDocumentTypeCommand cmd, CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        EnsureUnique(all, cmd.Name, cmd.Code, excludeId: null);
        // Ограничения тэгов (issue #258): новый тип может сразу нести restricted-тэг (POST несёт схему).
        ValidateTagRestrictions(cmd.Schema, Guid.Empty, cmd.Name.Trim(), all);

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
        // Ограничения тэгов (issue #258): считаем носителей среди прочих типов + входящей схемы.
        var all = await repo.GetAllAsync(ct);
        ValidateTagRestrictions(cmd.Schema, dt.Id, dt.Name, all);
        dt.UpdateSchema(cmd.Schema);
        repo.Update(dt);
        await repo.SaveChangesAsync(ct);
        return dt;
    }

    // Бросает InvalidOperationException (маппится в 409) со списком занятых мест — issue #258.
    private static void ValidateTagRestrictions(JsonDocument schema, Guid savingId, string savingName,
        IReadOnlyList<DocumentType> all)
    {
        var violations = TagRestrictionValidator.Validate(schema, savingId, savingName, all);
        if (violations.Count > 0)
            throw new InvalidOperationException(string.Join(" ", violations.Select(v => v.Describe())));
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

    public async Task<DocumentType> Handle(SetDocumentTypeAllowsProxyCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"DocumentType {cmd.Id} not found");
        dt.SetAllowsProxy(cmd.AllowsProxy);
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

    // issue #57: удаление типа не проверяло использование. Проверки вынесены в ComputeUsageAsync —
    // общий источник для guard'а удаления И проактивного показа (issue #275), чтобы не разъехались.
    public async Task Handle(DeleteDocumentTypeCommand cmd, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        var all = await repo.GetAllAsync(ct);
        var usage = await ComputeUsageAsync(dt, all, ct);
        if (usage.InUse)
            throw new InvalidOperationException(
                "Нельзя удалить тип — используется. " + string.Join("; ", usage.Reasons.Select(FormatReason)) + ".");

        repo.Remove(dt);
        await repo.SaveChangesAsync(ct);
    }

    public async Task<DocumentTypeUsage> Handle(GetDocumentTypeUsageQuery q, CancellationToken ct)
    {
        var dt = await repo.GetByIdAsync(q.Id, ct) ?? throw new KeyNotFoundException();
        return await ComputeUsageAsync(dt, await repo.GetAllAsync(ct), ct);
    }

    private static string FormatReason(DocumentTypeUsageReason r) =>
        r.Names.Count > 0 ? $"{r.Label}: {string.Join(", ", r.Names)}"
        : r.Count > 0 ? $"{r.Label}: {r.Count}"
        : r.Label;

    // Все причины, из-за которых тип нельзя удалить (issue #57 + #258 + #269). После слияния (issue #84)
    // документы и записи общих данных — единый DomainObject.CompositeTypeId, поэтому проверка объектов одна.
    private async Task<DocumentTypeUsage> ComputeUsageAsync(DocumentType dt, IReadOnlyList<DocumentType> all, CancellationToken ct)
    {
        var reasons = new List<DocumentTypeUsageReason>();

        var children = all.Where(x => x.ParentId == dt.Id).ToList();
        if (children.Count > 0)
            reasons.Add(new("children", "Наследуются типы", children.Count, children.Select(c => c.Name).ToList()));

        // issue #258: тип-профиль уровня (несёт тэг profile-*) — снять тэг перед удалением.
        if (SchemaTags.SchemaHasTypeTag(dt.Schema, FunctionalTag.ProfileConstruction)
            || SchemaTags.SchemaHasTypeTag(dt.Schema, FunctionalTag.ProfileSection)
            || SchemaTags.SchemaHasTypeTag(dt.Schema, FunctionalTag.ProfileSet))
            reasons.Add(new("profile", "Назначен профилем уровня (снимите тэг «Профиль …»)", 0, []));

        var objects = await objectRepo.FindAsync(o => o.CompositeTypeId == dt.Id, ct);
        if (objects.Count > 0)
            reasons.Add(new("objects", "Созданы объекты (документы или записи общих данных)", objects.Count, []));

        var templates = await templateRepo.FindAsync(t => t.DocumentTypeId == dt.Id, ct);
        if (templates.Count > 0)
            reasons.Add(new("templates", "Шаблоны", templates.Count, []));

        var qdocs = await qualityDocRepo.FindAsync(qd => qd.DocumentTypeId == dt.Id, ct);
        if (qdocs.Count > 0)
            reasons.Add(new("quality", "Документы качества", qdocs.Count, []));

        var bindingTemplates = await dataSetService.ListTemplatesAsync(dt.Id, ct);
        if (bindingTemplates.Count > 0)
            reasons.Add(new("binding-templates", "Шаблоны привязки наборов данных", bindingTemplates.Count, []));

        if (await dataSetService.AnySourceMaterializedAsTypeAsync(dt.Id, ct))
            reasons.Add(new("materialized", "Материализован источник набора данных", 0, []));

        // Тип может использоваться как составной подтип в схеме ДРУГОГО типа (complex/array/doc-ref/
        // doc-array поле с typeId == dt.Id) — сам себя (собственную схему) не проверяем.
        var usedInSchemas = all.Where(t => t.Id != dt.Id && DocumentTypeSchemaReader.ReferencesType(t.Schema, dt.Id)).ToList();
        if (usedInSchemas.Count > 0)
            reasons.Add(new("subtype", "Используется как составной подтип в схеме", usedInSchemas.Count, usedInSchemas.Select(t => t.Name).ToList()));

        return new DocumentTypeUsage(reasons);
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
    IDomainObjectRepository objRepo,
    IRepository<DocumentType> docTypeRepo,
    IBlobStorage blobStorage) :
    IRequestHandler<CreateDocumentSetCommand, DocumentSet>,
    IRequestHandler<RenameDocumentSetCommand, DocumentSet>,
    IRequestHandler<DeleteDocumentSetCommand>,
    IRequestHandler<GetDocumentSetQuery, DocumentSet?>,
    IRequestHandler<ListAvailableInstancesQuery, IReadOnlyList<DomainObject>>,
    IRequestHandler<AddDocumentToSetCommand, DomainObject>,
    IRequestHandler<ReorderDocumentInstancesCommand, DocumentSet>,
    IRequestHandler<RenameDocumentInstanceCommand, DomainObject>,
    IRequestHandler<DeleteDocumentInstanceCommand>,
    IRequestHandler<DuplicateDocumentInstanceCommand, DomainObject>,
    IRequestHandler<CopyDocumentToSetCommand, CopyResult>,
    IRequestHandler<PreviewCopyDocumentQuery, IReadOnlyList<CopyWarning>>,
    IRequestHandler<UpdateRequisitesCommand, DomainObject>,
    IRequestHandler<UpdatePluginDataCommand, DomainObject>,
    IRequestHandler<GetDocumentInstanceQuery, DomainObject?>,
    IRequestHandler<SetDocumentTemplateCommand, DomainObject>,
    IRequestHandler<SetDocumentTemplatesCommand, DomainObject>,
    IRequestHandler<SetDocumentTemplateParamsCommand, DomainObject>
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
        // Объекты на оси (Set, этот Id) — документы и Set-скоуп общих данных — принадлежат комплекту:
        // FK-каскада на комплект нет (единая ось, полиморфный ScopeId), удаляем прикладно.
        var owned = await objRepo.FindAsync(o => o.ScopeLevel == CatalogScope.Set && o.ScopeId == cmd.Id, ct);
        foreach (var o in owned) objRepo.Remove(o); // фасета + generated_files каскадируются в БД
        setRepo.Remove(set);
        await setRepo.SaveChangesAsync(ct);
    }

    public Task<DocumentSet?> Handle(GetDocumentSetQuery q, CancellationToken ct)
        => setRepo.GetByIdAsync(q.Id, ct);

    public async Task<IReadOnlyList<DomainObject>> Handle(ListAvailableInstancesQuery q, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(q.SetId, ct) ?? throw new KeyNotFoundException();
        var section = await sectionRepo.GetByIdAsync(set.SectionId, ct) ?? throw new KeyNotFoundException();
        var constructionId = section.ConstructionId;

        var sectionIds = (await sectionRepo.FindAsync(s => s.ConstructionId == constructionId, ct))
            .Select(s => s.Id).ToHashSet();
        var setIds = (await setRepo.FindAsync(s => sectionIds.Contains(s.SectionId), ct))
            .Select(s => s.Id).ToList();

        return await objRepo.GetDocumentsInSetsAsync(setIds, ct);
    }

    public async Task<DomainObject> Handle(AddDocumentToSetCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.DocumentSetId, ct)
            ?? throw new KeyNotFoundException();
        var docs = await objRepo.GetSetDocumentsAsync(cmd.DocumentSetId, tracked: false, ct);
        // Новый документ — в конец комплекта (порядок сборки задаётся SortOrder).
        var maxOrder = docs.Count == 0 ? -1 : docs.Max(d => d.SortOrder);

        var obj = DomainObject.Create(cmd.DocumentTypeId, null, JsonDocument.Parse("{}"),
            CatalogScope.Set, cmd.DocumentSetId);
        obj.EnsureFacet();
        obj.SetSortOrder(maxOrder + 1);
        set.TouchUpdatedAt();
        setRepo.Update(set);
        await objRepo.AddAsync(obj, ct);
        await objRepo.SaveChangesAsync(ct);
        return obj;
    }

    public async Task<DocumentSet> Handle(ReorderDocumentInstancesCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.SetId, ct) ?? throw new KeyNotFoundException();
        var docs = await objRepo.GetSetDocumentsAsync(cmd.SetId, tracked: true, ct);
        // Присваиваем SortOrder по позиции в переданном списке; отсутствующие в списке документы
        // (напр. добавленные параллельно) — в конец, сохраняя их относительный порядок.
        var order = cmd.OrderedInstanceIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var next = cmd.OrderedInstanceIds.Count;
        foreach (var d in docs.OrderBy(d => d.SortOrder))
            d.SetSortOrder(order.TryGetValue(d.Id, out var pos) ? pos : next++);
        await objRepo.SaveChangesAsync(ct);
        return set;
    }

    public async Task<DomainObject> Handle(RenameDocumentInstanceCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        obj.Rename(cmd.Name);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        return obj;
    }

    public async Task Handle(DeleteDocumentInstanceCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        // issue #71/#269: удаление объекта, на который ссылаются (базовый экземпляр "_baseRef" или
        // "$ref" в значениях полей), оставило бы висячую ссылку — при генерации она молча
        // разворачивается в ничто (EntityResolver возвращает исходный узел / пропускает базу).
        var referrers = await DomainObjectReferences.FindReferrersAsync(objRepo, cmd.Id, ct);
        if (referrers.Count > 0)
            throw new InvalidOperationException(
                $"Нельзя удалить документ — на него ссылаются другие объекты: {string.Join(", ", referrers.Select(r => r.DisplayName ?? "без имени"))}.");
        objRepo.Remove(obj);
        await objRepo.SaveChangesAsync(ct);
    }

    // issue #283 (фаза B): дубль в ТОТ ЖЕ комплект. Ссылки/_baseRef валидны в том же scope —
    // сохраняем как есть (cross-set скраб — отдельные команды copy/move). Свежий черновик без PDF.
    public async Task<DomainObject> Handle(DuplicateDocumentInstanceCommand cmd, CancellationToken ct)
    {
        var source = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new KeyNotFoundException();
        if (!source.IsDocument) throw new InvalidOperationException("Дублировать можно только документ комплекта.");
        var setId = source.ScopeId!.Value;

        var docs = await objRepo.GetSetDocumentsAsync(setId, tracked: false, ct);
        var maxOrder = docs.Count == 0 ? -1 : docs.Max(d => d.SortOrder);

        // Deep-clone Data (независимый JsonDocument): _baseRef и $ref сохраняются — тот же комплект.
        var data = JsonDocument.Parse(source.Data.RootElement.GetRawText());
        var baseName = source.DisplayName
            ?? (await docTypeRepo.GetByIdAsync(source.CompositeTypeId, ct))?.Name
            ?? "документа";
        var clone = DomainObject.CloneAsDocument(source, setId, data, $"Копия {baseName}");
        clone.SetSortOrder(maxOrder + 1);

        await objRepo.AddAsync(clone, ct);
        await objRepo.SaveChangesAsync(ct);
        return clone;
    }

    // issue #283 (фаза C): копирование в ДРУГОЙ комплект. Оригинал остаётся (входящий guard не нужен —
    // referrer'ы всё ещё указывают на живой оригинал; guard только для move, фаза D).
    public async Task<CopyResult> Handle(CopyDocumentToSetCommand cmd, CancellationToken ct)
    {
        var (source, targetSet) = await LoadCopyEndpointsAsync(cmd.SourceId, cmd.TargetSetId, ct);
        var (data, warnings) = await BuildCopyPlanAsync(source, targetSet, cmd.Strategy, ct);

        var docs = await objRepo.GetSetDocumentsAsync(targetSet.Id, tracked: false, ct);
        var maxOrder = docs.Count == 0 ? -1 : docs.Max(d => d.SortOrder);
        var baseName = source.DisplayName ?? (await docTypeRepo.GetByIdAsync(source.CompositeTypeId, ct))?.Name ?? "документа";
        var clone = DomainObject.CloneAsDocument(source, targetSet.Id, data, baseName);
        clone.SetSortOrder(maxOrder + 1);

        targetSet.TouchUpdatedAt();
        setRepo.Update(targetSet);
        await objRepo.AddAsync(clone, ct);
        await objRepo.SaveChangesAsync(ct);
        return new CopyResult(clone, warnings);
    }

    public async Task<IReadOnlyList<CopyWarning>> Handle(PreviewCopyDocumentQuery q, CancellationToken ct)
    {
        var (source, targetSet) = await LoadCopyEndpointsAsync(q.SourceId, q.TargetSetId, ct);
        var (_, warnings) = await BuildCopyPlanAsync(source, targetSet, q.Strategy, ct);
        return warnings;
    }

    private async Task<(DomainObject source, DocumentSet targetSet)> LoadCopyEndpointsAsync(Guid sourceId, Guid targetSetId, CancellationToken ct)
    {
        var source = await objRepo.GetByIdAsync(sourceId, ct) ?? throw new KeyNotFoundException();
        if (!source.IsDocument) throw new InvalidOperationException("Копировать можно только документ комплекта.");
        var targetSet = await setRepo.GetByIdAsync(targetSetId, ct) ?? throw new KeyNotFoundException("Целевой комплект не найден.");
        return (source, targetSet);
    }

    /// Скраб исходящих ссылок (стратегия B) + сбор предупреждений. Data результата — независимый JsonDocument.
    private async Task<(JsonDocument Data, IReadOnlyList<CopyWarning> Warnings)> BuildCopyPlanAsync(
        DomainObject source, DocumentSet targetSet, CopyStrategy strategy, CancellationToken ct)
    {
        _ = strategy; // сейчас только SmartCleanup; Snapshot — фаза C2.
        var warnings = new List<CopyWarning>();

        // 1) flatten _baseRef — запекаем унаследованные значения (иначе same-set guard молча потеряет их).
        var (flattened, didFlatten) = await FlattenBaseAsync(source.Data.RootElement, new HashSet<Guid>(), ct);
        if (didFlatten)
            warnings.Add(new CopyWarning("baseref", "Базовый экземпляр запечён в значения", 1, []));

        // 2) стрип $ref:document/instance — same-set, в чужом комплекте = мусор.
        var (scrubbed, strippedFields) = RefScrubber.StripInstanceRefs(flattened);
        if (strippedFields.Count > 0)
            warnings.Add(new CopyWarning("doc-ref", "Удалены ссылки на документы комплекта", strippedFields.Count, strippedFields));

        // 3) $ref:catalog — оставляем, но проверяем разрешимость в scope целевого комплекта.
        var section = await sectionRepo.GetByIdAsync(targetSet.SectionId, ct);
        var unresolved = 0;
        foreach (var catId in RefReader.CollectRefIds(scrubbed).Distinct())
        {
            var obj = await objRepo.GetByIdAsync(catId, ct);
            if (obj is null || !InTargetSubtree(obj, targetSet, section?.ConstructionId)) unresolved++;
        }
        if (unresolved > 0)
            warnings.Add(new CopyWarning("catalog-unresolved", "Ссылки на каталог не разрешатся в новом расположении", unresolved, []));

        return (JsonDocument.Parse(scrubbed.GetRawText()), warnings);
    }

    // Рекурсивный flatten базового экземпляра: base-first merge, drop _baseRef; cycle-guard через visited.
    private async Task<(JsonElement Data, bool Flattened)> FlattenBaseAsync(JsonElement data, HashSet<Guid> visited, CancellationToken ct)
    {
        if (BaseRefReader.GetBaseRefId(data) is not { } baseId || !visited.Add(baseId)) return (data, false);
        var baseObj = await objRepo.GetByIdAsync(baseId, ct);
        if (baseObj is null) return (data, false); // висячая база — нечего запекать
        var (baseData, _) = await FlattenBaseAsync(baseObj.Data.RootElement, visited, ct);
        return (BaseRefReader.MergeObjects(baseData, data), true);
    }

    private static bool InTargetSubtree(DomainObject o, DocumentSet targetSet, Guid? targetConstructionId) => o.ScopeLevel switch
    {
        CatalogScope.System => true,
        CatalogScope.Construction => o.ScopeId == targetConstructionId,
        CatalogScope.Section => o.ScopeId == targetSet.SectionId,
        CatalogScope.Set => o.ScopeId == targetSet.Id,
        _ => false,
    };

    public async Task<DomainObject> Handle(UpdateRequisitesCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new KeyNotFoundException();
        var blobs = obj.ResetToDraft();
        obj.SetData(cmd.Requisites);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }

    public async Task<DomainObject> Handle(UpdatePluginDataCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new KeyNotFoundException();
        var blobs = obj.ResetToDraft();
        obj.UpdatePluginData(cmd.PluginData);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }

    public Task<DomainObject?> Handle(GetDocumentInstanceQuery q, CancellationToken ct)
        => objRepo.GetByIdAsync(q.Id, ct);

    public async Task<DomainObject> Handle(SetDocumentTemplateCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new KeyNotFoundException();
        var blobs = obj.ResetToDraft();
        obj.SetTemplate(cmd.TemplateId);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }

    public async Task<DomainObject> Handle(SetDocumentTemplatesCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new KeyNotFoundException();
        var blobs = obj.ResetToDraft(); // смена набора шаблонов меняет вывод — в черновик
        obj.SetTemplateIds(cmd.TemplateIds);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }

    public async Task<DomainObject> Handle(SetDocumentTemplateParamsCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new KeyNotFoundException();
        var blobs = obj.ResetToDraft(); // параметры влияют на вывод — сбрасываем в черновик
        obj.SetTemplateParams(cmd.Params);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }
}

public class CommonDataHandlers(
    IRepository<DomainObject> repo,
    IRepository<DocumentSet> setRepo,
    IRepository<Section> sectionRepo,
    IRepository<Construction> constructionRepo,
    IDataSetResolver dataSetResolver,
    ILevelProfileService levelProfiles) :
    IRequestHandler<CreateCommonDataEntryCommand, DomainObject>,
    IRequestHandler<UpdateCommonDataEntryCommand, DomainObject>,
    IRequestHandler<DeleteCommonDataEntryCommand>,
    IRequestHandler<ListCommonDataEntriesQuery, IReadOnlyList<DomainObject>>,
    IRequestHandler<ResolveCommonDataForSetQuery, IReadOnlyList<CommonDataEntryWithScope>>,
    IRequestHandler<ResolveCommonDataForScopeQuery, IReadOnlyList<CommonDataEntryWithScope>>
{
    public async Task<DomainObject> Handle(CreateCommonDataEntryCommand cmd, CancellationToken ct)
    {
        // Запись общих данных — DomainObject БЕЗ документной фасеты (issue #84).
        var entry = DomainObject.Create(cmd.CompositeTypeId, cmd.DisplayName, cmd.Data, cmd.Scope, cmd.ScopeId, cmd.Aliases);
        await repo.AddAsync(entry, ct);
        await repo.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<DomainObject> Handle(UpdateCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        // Резолв-путь (issue #99): @@ref → {$ref:catalog, entryId}, а не display-строка «🔗 …».
        // Scope — из расположения объекта. Нет матча → поле не пишется (резолвер пропускает).
        var resolved = await dataSetResolver.ResolveOwnerBindingsAsync(
            cmd.Id, entry.CompositeTypeId, entry.ScopeLevel, entry.ScopeId, null, ct);
        var data = resolved.Count == 0 ? cmd.Data : CommonDataBindingMerge.Merge(cmd.Data, resolved);
        entry.Update(cmd.DisplayName, data, cmd.Aliases);
        repo.Update(entry);
        await repo.SaveChangesAsync(ct);
        return entry;
    }

    public async Task Handle(DeleteCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        // issue #258: объект-профиль (на который ссылается FK контейнера) — синглтон, удалять нельзя.
        if ((await constructionRepo.FindAsync(c => c.ProfileObjectId == cmd.Id, ct)).Count > 0
            || (await sectionRepo.FindAsync(s => s.ProfileObjectId == cmd.Id, ct)).Count > 0
            || (await setRepo.FindAsync(s => s.ProfileObjectId == cmd.Id, ct)).Count > 0)
            throw new InvalidOperationException("Это профиль уровня — его нельзя удалить. Он редактируется на странице «Общие данные» уровня.");
        // issue #71/#269: запись, на которую ссылаются другие объекты (базовый экземпляр "_baseRef"
        // или "$ref" в значениях полей), — тот же guard, что и для документа: иначе висячая ссылка.
        var referrers = await DomainObjectReferences.FindReferrersAsync(repo, cmd.Id, ct);
        if (referrers.Count > 0)
            throw new InvalidOperationException(
                $"Нельзя удалить запись — на неё ссылаются другие объекты: {string.Join(", ", referrers.Select(r => r.DisplayName ?? "без имени"))}.");
        repo.Remove(entry);
        await repo.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DomainObject>> Handle(ListCommonDataEntriesQuery q, CancellationToken ct)
    {
        var scope = q.Scope;
        var scopeId = q.ScopeId;
        var typeId = q.CompositeTypeId;
        // Ленивое создание профиля уровня (issue #258): при открытии общих данных контейнерного уровня
        // гарантируем объект-профиль (если профиль-тип сконфигурирован) — он попадёт в список ниже.
        if (scope is { } s && s != CatalogScope.System && scopeId is { } sid)
            await levelProfiles.EnsureProfileAsync(s, sid, ct);
        // Только общие данные (без документной фасеты).
        return await repo.FindAsync(e => e.Facet == null &&
            (!scope.HasValue || e.ScopeLevel == scope.Value) &&
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

        var relevant = await repo.FindAsync(e => e.Facet == null &&
            ((e.ScopeLevel == CatalogScope.Set          && e.ScopeId == setId) ||
             (e.ScopeLevel == CatalogScope.Section       && e.ScopeId == sectionId) ||
             (e.ScopeLevel == CatalogScope.Construction  && e.ScopeId == constructionId) ||
             e.ScopeLevel == CatalogScope.System) &&
            (!typeId.HasValue || e.CompositeTypeId == typeId.Value), ct);

        return Project(relevant);
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

        var relevant = await repo.FindAsync(e => e.Facet == null &&
            ((e.ScopeLevel == CatalogScope.Set          && e.ScopeId == setId) ||
             (e.ScopeLevel == CatalogScope.Section       && e.ScopeId == sectionId) ||
             (e.ScopeLevel == CatalogScope.Construction  && e.ScopeId == constructionId) ||
             e.ScopeLevel == CatalogScope.System) &&
            (!typeId.HasValue || e.CompositeTypeId == typeId.Value), ct);

        return Project(relevant);
    }

    private static List<CommonDataEntryWithScope> Project(IReadOnlyList<DomainObject> entries) =>
        entries
            .Select(e => new CommonDataEntryWithScope(
                e.Id, e.DisplayName ?? "", e.CompositeTypeId, e.Data,
                e.ScopeLevel, e.ScopeId, (int)e.ScopeLevel,
                e.CreatedAt, e.UpdatedAt))
            .OrderBy(e => e.Priority)
            .ThenBy(e => e.DisplayName)
            .ToList();
}
