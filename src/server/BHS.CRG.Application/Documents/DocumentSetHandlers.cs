using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using MediatR;

namespace BHS.CRG.Application.Documents;

public class DocumentSetHandlers(
    IRepository<DocumentSet> setRepo,
    IRepository<Section> sectionRepo,
    IDomainObjectRepository objRepo,
    IRepository<DocumentType> docTypeRepo,
    IRepository<PrimitiveType> primitiveRepo,
    IRepository<QualityDocument> qualityDocRepo,
    IReferenceIndex refIndex,
    IBlobStorage blobStorage,
    IScopeSubtree scopeSubtree,
    IScopeCascade cascade) :
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
    IRequestHandler<MoveDocumentToSetCommand, CopyResult>,
    IRequestHandler<PreviewMoveDocumentQuery, MovePreview>,
    IRequestHandler<UpdateRequisitesCommand, DomainObject>,
    IRequestHandler<UpdatePluginDataCommand, DomainObject>,
    IRequestHandler<GetDocumentInstanceQuery, DomainObject?>,
    IRequestHandler<SetDocumentTemplateCommand, DomainObject>,
    IRequestHandler<SetDocumentTemplatesCommand, DomainObject>,
    IRequestHandler<SetDocumentTemplateParamsCommand, DomainObject>
{
    public async Task<DocumentSet> Handle(CreateDocumentSetCommand cmd, CancellationToken ct)
    {
        // Раздел приходит ТЕЛОМ запроса (issue #960), а не сегментом адреса, и потому может не
        // прийти вовсе. Два разных отказа, а не один: пустой ключ — это «поле забыли» (400),
        // непустой и ненайденный — «такого раздела нет» (404). Без проверки оба доезжали до
        // внешнего ключа в базе и возвращались пятисоткой: ApiErrorMapping не разбирает
        // DbUpdateException и прячет её целиком, то есть ошибка вызывающего выглядела бы поломкой
        // сервера (ревью PR #1045).
        if (cmd.SectionId == Guid.Empty)
            throw new InvalidRequestException("Не указан раздел, в котором заводится комплект.");
        _ = await sectionRepo.GetByIdAsync(cmd.SectionId, ct)
            ?? throw new NotFoundException("Раздел не найден.");

        var set = DocumentSet.Create(cmd.SectionId, cmd.Name);
        await setRepo.AddAsync(set, ct);
        await setRepo.SaveChangesAsync(ct);
        return set;
    }

    public async Task<DocumentSet> Handle(RenameDocumentSetCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        set.Rename(cmd.Name);
        setRepo.Update(set);
        await setRepo.SaveChangesAsync(ct);
        return set;
    }

    public async Task Handle(DeleteDocumentSetCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        // Всё, что висит на оси (Set, этот Id) — документы, Set-скоуп общих данных, документы
        // качества уровня комплекта и связки материалов, — принадлежит комплекту: FK-каскада на
        // комплект нет (единая ось, полиморфный ScopeId), удаляем прикладно.
        // issue #739: и тот же guard, что у поштучного удаления, — иначе каскад обходит его с фланга.
        var plan = await cascade.PlanAsync(CatalogScope.Set, cmd.Id, ct);
        cascade.EnsureDeletable(plan, "комплект");
        cascade.Remove(plan);
        setRepo.Remove(set);
        await setRepo.SaveChangesAsync(ct);
    }

    public Task<DocumentSet?> Handle(GetDocumentSetQuery q, CancellationToken ct)
        => setRepo.GetByIdAsync(q.Id, ct);

    public async Task<IReadOnlyList<DomainObject>> Handle(ListAvailableInstancesQuery q, CancellationToken ct)
    {
        // Доступны документы всей стройки: сначала поднимаемся от комплекта к ней, потом спускаемся
        // обратно ко всем её комплектам. Спуск — общий (issue #625), своей копии здесь больше нет.
        var set = await setRepo.GetByIdAsync(q.SetId, ct) ?? throw new NotFoundException();
        var section = await sectionRepo.GetByIdAsync(set.SectionId, ct) ?? throw new NotFoundException();

        var setIds = await scopeSubtree.SetIdsUnderAsync(CatalogScope.Construction, section.ConstructionId, ct);
        return await objRepo.GetDocumentsInSetsAsync(setIds, ct);
    }

    public async Task<DomainObject> Handle(AddDocumentToSetCommand cmd, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(cmd.DocumentSetId, ct)
            ?? throw new NotFoundException();
        var docs = await objRepo.GetSetDocumentsAsync(cmd.DocumentSetId, tracked: false, ct);
        // Новый документ — в конец комплекта (порядок сборки задаётся SortOrder).
        var maxOrder = docs.Count == 0 ? -1 : docs.Max(d => d.SortOrder);

        var type = await docTypeRepo.GetByIdAsync(cmd.DocumentTypeId, ct)
            ?? throw new NotFoundException($"DocumentType {cmd.DocumentTypeId} not found");
        TypeStorageRules.EnsureCommonPathAllowed(type);

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
        var set = await setRepo.GetByIdAsync(cmd.SetId, ct) ?? throw new NotFoundException();
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
        var obj = await objRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        obj.Rename(cmd.Name);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        return obj;
    }

    public async Task Handle(DeleteDocumentInstanceCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        // issue #71/#269: удаление объекта, на который ссылаются (базовый экземпляр "_baseRef" или
        // "$ref" в значениях полей), оставило бы висячую ссылку — при генерации она молча
        // разворачивается в ничто (EntityResolver возвращает исходный узел / пропускает базу).
        var referrers = await DomainObjectReferences.FindReferrersAsync(objRepo, qualityDocRepo, refIndex, cmd.Id, ct);
        if (referrers.Count > 0)
            throw new ConflictException(
                $"Нельзя удалить документ — на него ссылаются другие объекты: {string.Join(", ", referrers.Select(r => r.Label))}.");
        objRepo.Remove(obj);
        await objRepo.SaveChangesAsync(ct);
    }

    // issue #283 (фаза B): дубль в ТОТ ЖЕ комплект. Ссылки/_baseRef валидны в том же scope —
    // сохраняем как есть (cross-set скраб — отдельные команды copy/move). Свежий черновик без PDF.
    public async Task<DomainObject> Handle(DuplicateDocumentInstanceCommand cmd, CancellationToken ct)
    {
        var source = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();
        if (!source.IsDocument) throw new ConflictException("Дублировать можно только документ комплекта.");
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

    // issue #283 (фаза D): перенос в другой комплект. Входящий guard (как удаление #269): если на
    // документ ссылаются — блокируем (в исходном комплекте ссылка повиснет). Тот же скраб исходящих,
    // что и copy; PDF сбрасываются (контекст резолва сменился).
    public async Task<CopyResult> Handle(MoveDocumentToSetCommand cmd, CancellationToken ct)
    {
        var (source, targetSet) = await LoadCopyEndpointsAsync(cmd.SourceId, cmd.TargetSetId, ct);
        var srcSetId = source.ScopeId!.Value;
        if (srcSetId == targetSet.Id) throw new ConflictException("Документ уже в этом комплекте.");

        var referrers = await DomainObjectReferences.FindReferrersAsync(objRepo, qualityDocRepo, refIndex, source.Id, ct);
        if (referrers.Count > 0)
            throw new ConflictException(
                $"Нельзя перенести документ — на него ссылаются другие объекты: {string.Join(", ", referrers.Select(r => r.Label))}.");

        var (data, warnings) = await BuildCopyPlanAsync(source, targetSet, cmd.Strategy, ct);
        var docs = await objRepo.GetSetDocumentsAsync(targetSet.Id, tracked: false, ct);
        var maxOrder = docs.Count == 0 ? -1 : docs.Max(d => d.SortOrder);

        var blobs = source.ResetToDraft(); // собранный вывод обоих комплектов устаревает
        source.SetData(data);
        source.MoveToSet(targetSet.Id);
        source.SetSortOrder(maxOrder + 1);

        targetSet.TouchUpdatedAt();
        setRepo.Update(targetSet);
        if (await setRepo.GetByIdAsync(srcSetId, ct) is { } srcSet) { srcSet.TouchUpdatedAt(); setRepo.Update(srcSet); }
        objRepo.Update(source);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return new CopyResult(source, warnings);
    }

    public async Task<MovePreview> Handle(PreviewMoveDocumentQuery q, CancellationToken ct)
    {
        var (source, targetSet) = await LoadCopyEndpointsAsync(q.SourceId, q.TargetSetId, ct);
        var referrers = await DomainObjectReferences.FindReferrersAsync(objRepo, qualityDocRepo, refIndex, source.Id, ct);
        var (_, warnings) = await BuildCopyPlanAsync(source, targetSet, q.Strategy, ct);
        return new MovePreview(warnings, referrers.Select(r => r.Label).ToList());
    }

    private async Task<(DomainObject source, DocumentSet targetSet)> LoadCopyEndpointsAsync(Guid sourceId, Guid targetSetId, CancellationToken ct)
    {
        var source = await objRepo.GetByIdAsync(sourceId, ct) ?? throw new NotFoundException();
        if (!source.IsDocument) throw new ConflictException("Копировать можно только документ комплекта.");
        var targetSet = await setRepo.GetByIdAsync(targetSetId, ct) ?? throw new NotFoundException("Целевой комплект не найден.");
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

        var section = await sectionRepo.GetByIdAsync(targetSet.SectionId, ct);

        // 2) стрип $ref:document/instance — same-set, в чужом комплекте = мусор. Кроме ссылок на
        //    документы качества, видимые из ЦЕЛЕВОГО комплекта (issue #733): у instance-ссылки два
        //    домена, и второй живёт по цепочке областей, а не по комплекту, — стерев его, мы
        //    выбросили бы рабочие данные и назвали бы их «ссылками на документы комплекта».
        var keepIds = await VisibleQualityRefsAsync(flattened, targetSet, section?.ConstructionId, ct);
        var (scrubbed, strippedFields) = RefScrubber.StripInstanceRefs(flattened, keepIds);
        if (strippedFields.Count > 0)
            warnings.Add(new CopyWarning("doc-ref", "Удалены ссылки на документы комплекта", strippedFields.Count, strippedFields));

        // 3) $ref:catalog — оставляем, но проверяем разрешимость в scope целевого комплекта.
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

    /// <summary>
    /// Идентификаторы документов качества, на которые ссылается <paramref name="data"/> и которые
    /// ОСТАНУТСЯ видимыми из целевого комплекта (issue #733) — их ссылки скраб не трогает.
    ///
    /// <para>Видимость считается тем же правилом, что у остальной библиотеки и у резолвера: System
    /// всегда, иначе совпадение по стройке/разделу/комплекту цели переноса. Сертификат уровня System
    /// переживает перенос куда угодно, сертификат чужой стройки — стирается вместе с остальными
    /// неразрешимыми ссылками, и это верно: в новом расположении он бы не развернулся.</para>
    /// </summary>
    private async Task<IReadOnlySet<Guid>> VisibleQualityRefsAsync(
        JsonElement data, DocumentSet targetSet, Guid? targetConstructionId, CancellationToken ct)
    {
        var ids = RefReader.CollectRefIds(data).Distinct().ToHashSet();
        if (ids.Count == 0) return new HashSet<Guid>();

        var docs = await qualityDocRepo.FindAsync(d => ids.Contains(d.Id), ct);
        return docs.Where(d => d.Scope switch
            {
                CatalogScope.System => true,
                CatalogScope.Construction => d.ScopeId == targetConstructionId,
                CatalogScope.Section => d.ScopeId == targetSet.SectionId,
                CatalogScope.Set => d.ScopeId == targetSet.Id,
                _ => false,
            })
            .Select(d => d.Id)
            .ToHashSet();
    }

    public async Task<DomainObject> Handle(UpdateRequisitesCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();
        // Охрана записи (issue #957) — ДО сброса в черновик: отказ, который «почти сохранил»
        // (снёс выпущенный PDF и не записал данные), хуже отсутствия отказа.
        await Schema.WriteGuard.EnsureAllowedAsync(
            obj.Data, cmd.Requisites, obj.CompositeTypeId, docTypeRepo, primitiveRepo, ct);
        var blobs = obj.ResetToDraft();
        obj.SetData(cmd.Requisites);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }

    public async Task<DomainObject> Handle(UpdatePluginDataCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();
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
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();
        var blobs = obj.ResetToDraft();
        obj.SetTemplate(cmd.TemplateId);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }

    public async Task<DomainObject> Handle(SetDocumentTemplatesCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();
        var blobs = obj.ResetToDraft(); // смена набора шаблонов меняет вывод — в черновик
        obj.SetTemplateIds(cmd.TemplateIds);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }

    public async Task<DomainObject> Handle(SetDocumentTemplateParamsCommand cmd, CancellationToken ct)
    {
        var obj = await objRepo.GetByIdAsync(cmd.InstanceId, ct) ?? throw new NotFoundException();
        var blobs = obj.ResetToDraft(); // параметры влияют на вывод — сбрасываем в черновик
        obj.SetTemplateParams(cmd.Params);
        objRepo.Update(obj);
        await objRepo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        return obj;
    }
}
