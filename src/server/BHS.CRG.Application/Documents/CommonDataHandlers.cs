using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using MediatR;

namespace BHS.CRG.Application.Documents;

public class CommonDataHandlers(
    IRepository<DomainObject> repo,
    IRepository<DocumentType> typeRepo,
    IRepository<PrimitiveType> primitiveRepo,
    IRepository<DocumentSet> setRepo,
    IRepository<Section> sectionRepo,
    IRepository<Construction> constructionRepo,
    IRepository<QualityDocument> qualityDocRepo,
    IReferenceIndex refIndex,
    IDataSetResolver dataSetResolver,
    ILevelProfileService levelProfiles) :
    IRequestHandler<CreateCommonDataEntryCommand, DomainObject>,
    IRequestHandler<UpdateCommonDataEntryCommand, DomainObject>,
    IRequestHandler<DeleteCommonDataEntryCommand>,
    IRequestHandler<ListCommonDataEntriesQuery, IReadOnlyList<DomainObject>>,
    IRequestHandler<GetCommonDataEntryQuery, DomainObject?>,
    IRequestHandler<ResolveCommonDataForSetQuery, IReadOnlyList<CommonDataEntryWithScope>>,
    IRequestHandler<ResolveCommonDataForScopeQuery, IReadOnlyList<CommonDataEntryWithScope>>
{
    public async Task<DomainObject> Handle(CreateCommonDataEntryCommand cmd, CancellationToken ct)
    {
        // Запись общих данных — DomainObject БЕЗ документной фасеты (issue #84).
        var type = await typeRepo.GetByIdAsync(cmd.CompositeTypeId, ct)
            ?? throw new NotFoundException($"DocumentType {cmd.CompositeTypeId} not found");
        TypeStorageRules.EnsureCommonPathAllowed(type);
        // Охрана записи (issue #957): у создания «как лежит» — ничего, поэтому всё содержимое
        // вносится этой записью, и запертое поле нельзя заполнить даже впервые.
        await Schema.WriteGuard.EnsureAllowedAsync(
            null, cmd.Data, cmd.CompositeTypeId, typeRepo, primitiveRepo, ct);

        var entry = DomainObject.Create(cmd.CompositeTypeId, cmd.DisplayName, cmd.Data, cmd.Scope, cmd.ScopeId, cmd.Aliases);
        await repo.AddAsync(entry, ct);
        await repo.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<DomainObject> Handle(UpdateCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        // Резолв-путь (issue #99): @@ref → {$ref:catalog, entryId}, а не display-строка «🔗 …».
        // Scope — из расположения объекта. Нет матча → поле не пишется (резолвер пропускает).
        var resolved = await dataSetResolver.ResolveOwnerBindingsAsync(
            cmd.Id, entry.CompositeTypeId, entry.ScopeLevel, entry.ScopeId, null, ct);
        var data = resolved.Count == 0 ? cmd.Data : CommonDataBindingMerge.Merge(cmd.Data, resolved);
        // ⚠️ Охрана — ПОСЛЕ слияния с привязками, а не над телом запроса: иначе привязка набора
        // пронесла бы мимо охраны что угодно (issue #957).
        await Schema.WriteGuard.EnsureAllowedAsync(
            entry.Data, data, entry.CompositeTypeId, typeRepo, primitiveRepo, ct);
        entry.Update(cmd.DisplayName, data, cmd.Aliases);
        repo.Update(entry);
        await repo.SaveChangesAsync(ct);
        return entry;
    }

    public async Task Handle(DeleteCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        // issue #258: объект-профиль (на который ссылается FK контейнера) — синглтон, удалять нельзя.
        if ((await constructionRepo.FindAsync(c => c.ProfileObjectId == cmd.Id, ct)).Count > 0
            || (await sectionRepo.FindAsync(s => s.ProfileObjectId == cmd.Id, ct)).Count > 0
            || (await setRepo.FindAsync(s => s.ProfileObjectId == cmd.Id, ct)).Count > 0)
            throw new ConflictException("Это профиль уровня — его нельзя удалить. Он редактируется на странице «Общие данные» уровня.");
        // issue #71/#269: запись, на которую ссылаются другие объекты (базовый экземпляр "_baseRef"
        // или "$ref" в значениях полей), — тот же guard, что и для документа: иначе висячая ссылка.
        var referrers = await DomainObjectReferences.FindReferrersAsync(repo, qualityDocRepo, refIndex, cmd.Id, ct);
        if (referrers.Count > 0)
            throw new ConflictException(
                $"Нельзя удалить запись — на неё ссылаются другие объекты: {string.Join(", ", referrers.Select(r => r.Label))}.");
        repo.Remove(entry);
        await repo.SaveChangesAsync(ct);
    }

    public async Task<DomainObject?> Handle(GetCommonDataEntryQuery q, CancellationToken ct)
        => await repo.GetByIdAsync(q.Id, ct);

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
        var set = await setRepo.GetByIdAsync(q.SetId, ct) ?? throw new NotFoundException("DocumentSet not found");
        var section = await sectionRepo.GetByIdAsync(set.SectionId, ct) ?? throw new NotFoundException("Section not found");
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
