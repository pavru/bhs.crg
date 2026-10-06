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
    IDomainObjectRepository objects,
    IRepository<DocumentType> typeRepo,
    IRepository<PrimitiveType> primitiveRepo,
    IRepository<DocumentSet> setRepo,
    IRepository<Section> sectionRepo,
    IRepository<Construction> constructionRepo,
    IRepository<QualityDocument> qualityDocRepo,
    IRepository<WorkPlanItem> planRepo,
    IReferenceIndex refIndex,
    IRecordHolders holders,
    IDataSetResolver dataSetResolver,
    ILevelProfileService levelProfiles,
    BHS.CRG.Application.Resolution.IObjectResolver objectResolver) :
    IRequestHandler<CreateCommonDataEntryCommand, DomainObject>,
    IRequestHandler<UpdateCommonDataEntryCommand, DomainObject>,
    IRequestHandler<DeleteCommonDataEntryCommand>,
    IRequestHandler<ListCommonDataEntriesQuery, IReadOnlyList<DomainObject>>,
    IRequestHandler<GetCommonDataEntryQuery, DomainObject?>,
    IRequestHandler<SearchCommonDataForChoiceQuery, ChoiceCandidates>,
    IRequestHandler<CommonDataRefsByIdsQuery, IReadOnlyList<CommonDataRef>>,
    IRequestHandler<ArchivedAmongQuery, IReadOnlyList<Guid>>,
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
            null, cmd.Data, cmd.CompositeTypeId, typeRepo, primitiveRepo, objects, ct,
            await RefsStandingInAsync(cmd.RefsStandIn, ct));
        // После охраны: «есть в архиве» говорят про запись, которую иначе создали бы (ревью PR #1229).
        if (!cmd.CreateAnyway) await EnsureNoArchivedTwinAsync(cmd, ct);

        var entry = DomainObject.Create(cmd.CompositeTypeId, cmd.DisplayName, cmd.Data, cmd.Scope, cmd.ScopeId, cmd.Aliases);
        await repo.AddAsync(entry, ct);
        await repo.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>
    /// Ссылки, стоящие в СОХРАНЁННЫХ данных названного объекта (issue #1185, ревью PR #1230). Вынос
    /// значения в общие данные переносит их в новую запись, и новыми они от этого не становятся.
    /// Читается из базы, а не из запроса: «стояла» решает то, что лежит. Объекта нет (форма ещё не
    /// сохранена, документ качества) — стоявших нет, и правило работает как у обычного создания.
    /// </summary>
    private async Task<IReadOnlySet<Guid>?> RefsStandingInAsync(Guid? ownerId, CancellationToken ct) =>
        ownerId is { } id && await repo.GetByIdAsync(id, ct) is { } owner
            ? CatalogRefs.IdsIn(owner.Data.RootElement) : null;

    /// <summary>
    /// «Есть в архиве» (issue #1185). Только по КЛЮЧУ ИДЕНТИЧНОСТИ и только когда действующей с этим
    /// ключом нет: резолвер ставит действующие первыми, поэтому архивное совпадение и значит
    /// «действующей нет». По одному названию не отказываем — действующих тёзок ядро не запрещает, и
    /// запрет одних архивных был бы непоследователен.
    ///
    /// <para>⚠️ Именно «свежий» вопрос: обычный резолвер помнит кандидатов всё время жизни области, и
    /// спроси мы его здесь — запись, созданную следом, он в этой области уже не нашёл бы.</para>
    ///
    /// <para>Это подсказка человеку, а не ограничение целостности: между вопросом и записью замка
    /// нет, и два одновременных создания пройдут оба. Действующих дублей ядро не запрещает и без
    /// гонки, так что стеречь здесь нечего. Ключ читается из собственных данных записи — как и у
    /// лежащих: поля, унаследованные от основы, в ключ не входят ни с той, ни с другой стороны.</para>
    /// </summary>
    private async Task EnsureNoArchivedTwinAsync(CreateCommonDataEntryCommand cmd, CancellationToken ct)
    {
        if (await objectResolver.ResolveFreshAsync(
                BHS.CRG.Application.Resolution.ObjectMatchRequest.ByIdentityOf(cmd.CompositeTypeId, cmd.Data.RootElement),
                cmd.Scope, cmd.ScopeId, ct) is not { Archived: true } twin) return;
        var archived = await repo.GetByIdAsync(twin.Id, ct);
        throw new ArchivedTwinException(twin.Id, archived?.DisplayName ?? "",
            (archived?.ScopeLevel ?? cmd.Scope).ToString());
    }

    public async Task<DomainObject> Handle(UpdateCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        await EnsureCommonPathAsync(entry, CommonPathAction.Update, ct);
        // Версия сверяется ДВАЖДЫ (issue #1214): здесь — чтобы устаревшая правка получила отказ, не
        // дожидаясь чтения наборов и охраны, — и ещё раз при записи, под блокировкой строки.
        RecordSeen.Ensure(entry.Version, cmd.Seen);
        // Резолв-путь (issue #99): @@ref → {$ref:catalog, entryId}, а не display-строка «🔗 …».
        // Scope — из расположения объекта. Нет матча → поле не пишется (резолвер пропускает).
        // Стоявшие ссылки — из сохранённых данных, а не из тела запроса: «уже стояла» решает то,
        // что лежит в записи (issue #1185). Это правило ПРИВЯЗКИ; ссылку, присланную прямо в теле,
        // проверяет охрана записи ниже — тем же сравнением с лежащим.
        var resolved = await dataSetResolver.ResolveOwnerBindingsAsync(
            cmd.Id, entry.CompositeTypeId, entry.ScopeLevel, entry.ScopeId,
            CatalogRefs.IdsIn(entry.Data.RootElement), cmd.Access, null, ct);
        var data = resolved.Count == 0 ? cmd.Data : CommonDataBindingMerge.Merge(cmd.Data, resolved);
        // ⚠️ Охрана — ПОСЛЕ слияния с привязками, а не над телом запроса: иначе привязка набора
        // пронесла бы мимо охраны что угодно (issue #957).
        await Schema.WriteGuard.EnsureAllowedAsync(
            entry.Data, data, entry.CompositeTypeId, typeRepo, primitiveRepo, objects, ct);
        entry.Update(cmd.DisplayName, data, cmd.Aliases);
        repo.Update(entry);
        await objects.SaveSeenAsync(entry, cmd.Seen, ct);
        return entry;
    }

    /// <summary>
    /// Тот же запрет, что у создания (issue #1215), — и раньше СВЕРКИ версии и вопроса держателям:
    /// причина «этим адресом запись не ведётся» главнее любой следующей, и человек, получивший
    /// вместо неё «запись тем временем изменили», пошёл бы перечитывать то, что править всё равно
    /// нельзя. Типа нет — запрещать нечем: его способ хранения спросить не у кого.
    ///
    /// <para>Запрос БЕЗ версии адрес отвергает ещё раньше, до обработчика (ревью PR #1233): это
    /// отказ о форме запроса, а не о записи, и клиент, который версию называет, его не увидит.</para>
    ///
    /// <para>Тип читается отдельным запросом, а охрана записи ниже прочтёт его ещё раз. Оставлено:
    /// чтение по ключу, а общий «тип, прочитанный однажды» потребовал бы менять подпись охраны у
    /// всех её мест ради одной строки.</para>
    /// </summary>
    private async Task EnsureCommonPathAsync(DomainObject entry, CommonPathAction action, CancellationToken ct)
    {
        if (await typeRepo.GetByIdAsync(entry.CompositeTypeId, ct) is { } type)
            TypeStorageRules.EnsureCommonPathAllowed(type, action);
    }

    public async Task Handle(DeleteCommonDataEntryCommand cmd, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        await EnsureCommonPathAsync(entry, CommonPathAction.Delete, ct);
        // issue #258: объект-профиль (на который ссылается FK контейнера) — синглтон, удалять нельзя.
        if ((await constructionRepo.FindAsync(c => c.ProfileObjectId == cmd.Id, ct)).Count > 0
            || (await sectionRepo.FindAsync(s => s.ProfileObjectId == cmd.Id, ct)).Count > 0
            || (await setRepo.FindAsync(s => s.ProfileObjectId == cmd.Id, ct)).Count > 0)
            throw new ConflictException("Это профиль уровня — его нельзя удалить. Он редактируется на странице «Общие данные» уровня.");
        // issue #964: запись, на которую ссылается ПЕРЕЧЕНЬ РАБОТ — вид работы или единица
        // измерения позиции (ТЗ CORE-10). Её удаление запрещает внешний ключ, и без этой проверки
        // человек получил бы внутреннюю ошибку сервера вместо отказа: отказ, переодетый в поломку,
        // читается как «система сломалась», а не «так нельзя».
        var inPlan = (await planRepo.FindAsync(p => p.WorkTypeId == cmd.Id || p.UnitId == cmd.Id, ct)).Count;
        if (inPlan > 0)
            throw new ConflictException(
                $"Нельзя удалить запись — на неё ссылается перечень работ, позиций: {inPlan}. " +
                "Позиция перечня — это вид работы, стройка, раздел и единица измерения; без этой " +
                "записи она перестала бы что-либо означать, а план, факт и акты модулей ссылаются " +
                "на неё. Убрать позицию перечня из интерфейса пока нельзя — её заводит и убирает " +
                "код, — поэтому сообщите о находке: запись останется на месте до тех пор.");

        // issue #71/#269: запись, на которую ссылаются другие объекты (базовый экземпляр "_baseRef"
        // или "$ref" в значениях полей), — тот же guard, что и для документа: иначе висячая ссылка.
        var referrers = await DomainObjectReferences.FindReferrersAsync(repo, qualityDocRepo, refIndex, cmd.Id, ct);
        if (referrers.Count > 0)
            throw new ConflictException(
                $"Нельзя удалить запись — на неё ссылаются другие объекты: {string.Join(", ", referrers.Select(r => r.Label))}.");
        // issue #1094: и данные модулей. Индекс ссылок выше видит только таблицы ядра — позиция
        // номенклатуры, стоящая в строке счёта, для него свободна (issue #1168).
        (await holders.FindAsync([cmd.Id], ct)).EnsureNone("запись");
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
        // Только общие данные (без документной фасеты). Выбор архивные записи скрывает.
        var live = q.For.HidesArchive();
        return await repo.FindAsync(e => e.Facet == null &&
            (!live || e.ArchivedAt == null) &&
            (!scope.HasValue || e.ScopeLevel == scope.Value) &&
            (!scopeId.HasValue || e.ScopeId == scopeId.Value) &&
            (!typeId.HasValue || e.CompositeTypeId == typeId.Value), ct);
    }

    /// <summary>
    /// Ссылки на записи справочника (issue #1078): выбор позиции из справочника, у которого записей
    /// много. Ленивое создание профиля уровня здесь НЕ трогается — оно про открытие общих данных
    /// уровня, а этот запрос отбирает по виду и названию и об уровнях не спрашивает.
    /// </summary>
    public async Task<ChoiceCandidates> Handle(SearchCommonDataForChoiceQuery q, CancellationToken ct)
        => await objects.SearchForChoiceAsync(q.TypeIds, q.Search, q.Limit, ct);

    public async Task<IReadOnlyList<CommonDataRef>> Handle(CommonDataRefsByIdsQuery q, CancellationToken ct)
        => await objects.RefsByIdsAsync(q.TypeIds, q.Ids, ct);

    public async Task<IReadOnlyList<Guid>> Handle(ArchivedAmongQuery q, CancellationToken ct)
        => [.. (await objects.ArchivedAmongAsync(q.Ids, ct)).Select(a => a.Id)];

    public async Task<IReadOnlyList<CommonDataEntryWithScope>> Handle(
        ResolveCommonDataForSetQuery q, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(q.SetId, ct) ?? throw new NotFoundException("DocumentSet not found");
        var section = await sectionRepo.GetByIdAsync(set.SectionId, ct) ?? throw new NotFoundException("Section not found");
        var constructionId = section.ConstructionId;
        var setId = q.SetId;
        var sectionId = set.SectionId;
        var typeId = q.CompositeTypeId;
        var live = q.For.HidesArchive();

        var relevant = await repo.FindAsync(e => e.Facet == null &&
            (!live || e.ArchivedAt == null) &&
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
        var live = q.For.HidesArchive();
        var archivedOnly = q.ArchivedOnly;

        var relevant = await repo.FindAsync(e => e.Facet == null &&
            (!live || e.ArchivedAt == null) &&
            (!archivedOnly || e.ArchivedAt != null) &&
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
                e.CreatedAt, e.UpdatedAt, e.IsArchived, e.Version))
            .OrderBy(e => e.Priority)
            .ThenBy(e => e.DisplayName)
            .ToList();
}
