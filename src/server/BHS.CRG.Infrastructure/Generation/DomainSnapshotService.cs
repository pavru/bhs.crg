using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Generation;

/// <inheritdoc />
public class DomainSnapshotService(
    IMediator mediator,
    IDomainObjectRepository objects,
    IRepository<DocumentType> types,
    IRepository<Section> sections,
    IRepository<Construction> constructions,
    IRepository<DomainObject> domainObjects,
    IEntityResolver entityResolver,
    AppDbContext db,
    IDataSetRowLoader rowLoader) : IDomainSnapshotService
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public async Task<SnapshotPage<ConstructionSummary>> ListConstructionsAsync(
        Guid userId,
        int offset = 0, int limit = DomainSnapshotLimits.NavigationDefault,
        CancellationToken ct = default)
    {
        var list = await mediator.Send(new ListConstructionsQuery(userId), ct);
        var setIds = list.SelectMany(c => c.Sections).SelectMany(s => s.DocumentSets).Select(ds => ds.Id).ToList();
        var counts = await objects.CountDocumentsInSetsAsync(setIds, ct);

        var all = list.Select(c => new ConstructionSummary(
            c.Id, c.Name,
            c.Sections.Count,
            c.Sections.Sum(s => s.DocumentSets.Count),
            c.Sections.SelectMany(s => s.DocumentSets).Sum(ds => counts.GetValueOrDefault(ds.Id)))).ToArray();

        return SnapshotPage<ConstructionSummary>.Of(all, offset, limit, DomainSnapshotLimits.NavigationMax);
    }

    public async Task<ConstructionDetail?> GetConstructionAsync(Guid constructionId, CancellationToken ct = default)
    {
        var c = await mediator.Send(new GetConstructionQuery(constructionId), ct);
        if (c is null) return null;

        var setIds = c.Sections.SelectMany(s => s.DocumentSets).Select(ds => ds.Id).ToList();
        var counts = await objects.CountDocumentsInSetsAsync(setIds, ct);

        return new ConstructionDetail(c.Id, c.Name,
            [.. c.Sections.Select(s => new SectionInfo(s.Id, s.Name,
                [.. s.DocumentSets.Select(ds => new DocumentSetInfo(ds.Id, ds.Name, counts.GetValueOrDefault(ds.Id)))]))]);
    }

    public async Task<DocumentSetDetail?> GetDocumentSetAsync(Guid setId, CancellationToken ct = default)
    {
        var set = await mediator.Send(new GetDocumentSetQuery(setId), ct);
        if (set is null) return null;

        var docs = await objects.GetSetDocumentsAsync(setId, tracked: false, ct);
        var typeMap = await TypeMapAsync(docs.Select(d => d.CompositeTypeId), ct);

        // Раздел и стройка — контекст, без которого агент не знает, ЧЕЙ это комплект: имена документов
        // между разделами повторяются, и находка без контекста непроверяема.
        var section = await sections.GetByIdAsync(set.SectionId, ct);
        var construction = section is null ? null
            : await constructions.GetByIdAsync(section.ConstructionId, ct);

        return new DocumentSetDetail(
            set.Id, set.Name,
            set.SectionId, section?.Name ?? "",
            construction?.Id ?? Guid.Empty, construction?.Name ?? "",
            [.. docs.OrderBy(d => d.SortOrder).Select(d => ToSummary(d, typeMap))]);
    }

    public async Task<DocumentDetail?> GetDocumentAsync(Guid documentId, bool resolveRefs = true,
        IReadOnlyCollection<string>? fields = null, bool expandDocumentRefs = false,
        CancellationToken ct = default)
    {
        var doc = await mediator.Send(new GetDocumentInstanceQuery(documentId), ct);
        if (doc is null) return null;

        var typeMap = await TypeMapAsync([doc.CompositeTypeId], ct);
        var type = typeMap.GetValueOrDefault(doc.CompositeTypeId);

        DocumentSet? set = null;
        if (doc.ScopeLevel == CatalogScope.Set && doc.ScopeId is { } sid)
            set = await mediator.Send(new GetDocumentSetQuery(sid), ct);

        var requisites = resolveRefs
            ? await ResolvedRequisitesAsync(doc, ct)
            : doc.Data.RootElement.Clone();

        // Свёртка повторов — только у развёрнутой формы: в форме хранения ссылки и так ссылки.
        IReadOnlyDictionary<string, JsonElement>? entities = null;
        if (resolveRefs)
        {
            var folded = RequisiteFolding.Fold(
                requisites, await DocumentNamesAsync(RequisiteFolding.DocumentIdsIn(requisites), ct),
                expandDocumentRefs);
            requisites = folded.Requisites;
            entities = folded.Entities;
        }

        var tableFields = await TableFieldsAsync(doc.Id, doc.CompositeTypeId, ct);

        var wanted = fields?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToList();
        if (wanted is not { Count: > 0 })
            return new DocumentDetail(
                doc.Id, doc.DisplayName ?? "", doc.CompositeTypeId,
                type?.Code ?? "", type?.Name ?? "",
                doc.Facet?.Status.ToString() ?? "",
                set?.Id, set?.Name,
                requisites, resolveRefs, tableFields, Entities: entities);

        // Проекция — ПОСЛЕ полного резолва: расчётное поле читает соседние, унаследованное приходит
        // от базового документа, и «считать только запрошенное» дало бы другое значение (#596).
        var known = DocumentTypeSchemaReader
            .EffectiveFields(doc.CompositeTypeId, await AllTypesAsync(ct))
            .Select(f => f.Key)
            .ToHashSet(StringComparer.Ordinal);

        var projected = Project(requisites, wanted);

        return new DocumentDetail(
            doc.Id, doc.DisplayName ?? "", doc.CompositeTypeId,
            type?.Code ?? "", type?.Name ?? "",
            doc.Facet?.Status.ToString() ?? "",
            set?.Id, set?.Name,
            projected, resolveRefs,
            [.. tableFields.Where(f => wanted.Contains(f.Key))],
            wanted,
            // Ключ не из схемы — почти наверняка опечатка, и умолчать о ней значит выдать её за
            // незаполненное поле.
            [.. wanted.Where(f => !known.Contains(f))],
            // Словарь тоже урезаем: карточка, на которую не осталось ссылок, — вес без адресата.
            entities is null ? null : ReferencedOnly(entities, projected));
    }

    /// <summary>Оставляет в словаре только карточки, на которые ссылается урезанный документ (#594).
    /// Ссылки внутри самих карточек считаются тоже — иначе вложенная организация исчезла бы из
    /// словаря, оставив в карточке подписанта висячую ссылку.</summary>
    private static IReadOnlyDictionary<string, JsonElement> ReferencedOnly(
        IReadOnlyDictionary<string, JsonElement> entities, JsonElement requisites)
    {
        var reachable = new HashSet<string>();
        var queue = new Queue<JsonElement>([requisites]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    if (node.TryGetProperty(RequisiteFolding.EntityRefKey, out var key)
                        && key.GetString() is { } id && reachable.Add(id)
                        && entities.TryGetValue(id, out var card))
                        queue.Enqueue(card);
                    foreach (var p in node.EnumerateObject()) queue.Enqueue(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray()) queue.Enqueue(item);
                    break;
            }
        }
        return entities.Where(e => reachable.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value);
    }

    /// <summary>Имена документов по идентификатору — одним запросом: голая ссылка без имени
    /// заставляет агента идти за ним отдельным вызовом, ради экономии которых свёртка и делается.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> DocumentNamesAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        var found = await domainObjects.FindAsync(o => ids.Contains(o.Id), ct);
        return found.Where(o => o.DisplayName is not null).ToDictionary(o => o.Id, o => o.DisplayName!);
    }

    /// <summary>Оставляет в реквизитах только запрошенные ключи верхнего уровня (issue #596).</summary>
    private static JsonElement Project(JsonElement requisites, IReadOnlyCollection<string> wanted)
    {
        if (requisites.ValueKind != JsonValueKind.Object) return requisites;

        var kept = requisites.EnumerateObject()
            .Where(p => wanted.Contains(p.Name))
            .ToDictionary(p => p.Name, p => p.Value);

        return JsonSerializer.SerializeToElement(kept);
    }

    /// <summary>
    /// Табличные поля документа с адресом их строк (issue #591).
    ///
    /// Строк здесь нет и не будет — их неограниченно много, а форма ответа не выражает усечение, — но
    /// САМО поле обязано быть видно: без него «таблицы нет» и «таблица придёт из набора» выглядят
    /// одинаково, и внешний анализ уже принял реестр из 151 позиции за пустой.
    ///
    /// Перечисляем поля СХЕМЫ, а не привязки: непривязанное табличное поле — тоже ответ, причём тот,
    /// ради которого всё и затевалось.
    /// </summary>
    private async Task<IReadOnlyList<DocumentTableField>> TableFieldsAsync(
        Guid documentId, Guid typeId, CancellationToken ct)
    {
        var tableFields = DocumentTypeSchemaReader.EffectiveFields(typeId, await AllTypesAsync(ct))
            .Where(f => DocumentTypeSchemaReader.IsMultiValued(f.Type))
            .ToList();
        if (tableFields.Count == 0) return [];

        var bindings = (await db.DataSetBindings
                .AsNoTracking()
                .Include(b => b.Source).ThenInclude(s => s.File)
                .Where(b => b.OwnerId == documentId && b.TargetFieldKey != null)
                .ToListAsync(ct))
            .GroupBy(b => b.TargetFieldKey!)
            .ToDictionary(g => g.Key, g => g.First());

        var result = new List<DocumentTableField>();
        foreach (var field in tableFields)
        {
            if (!bindings.TryGetValue(field.Key, out var binding))
            {
                result.Add(new DocumentTableField(field.Key, field.Title, false, null, null, null, null, null));
                continue;
            }

            // Число строк — ПОСЛЕ обработки источника: агенту важно, сколько попадёт в PDF, а не
            // сколько лежит в файле. Табличных полей у документа единицы, поэтому цена загрузки здесь
            // та же, что у одного get_rows.
            var rows = await rowLoader.LoadRowsAsync(binding.Source, ct);
            result.Add(new DocumentTableField(
                field.Key, field.Title, true,
                binding.SourceId, binding.Source.Name,
                binding.Source.FileId, binding.Source.File?.Name,
                rows.Count));
        }
        return result;
    }

    /// <summary>
    /// Та же цепочка резолва, что и у генерации PDF, — БЕЗ инъекции наборов данных и документов
    /// качества. Они вносят неограниченное число строк, а форма ответа не выражает <c>truncated</c>:
    /// вышла бы ровно та тихая неполнота, от которой защищает страничность строк источников.
    ///
    /// Наследование <c>_baseRef</c> здесь не менее важно самих ссылок: без него документ, унаследованный
    /// от базового, показывает лишь собственные переопределения — и внешний читатель отчитается о
    /// «незаполненных» полях, которые на самом деле заполнены.
    /// </summary>
    private async Task<JsonElement> ResolvedRequisitesAsync(DomainObject doc, CancellationToken ct)
    {
        var view = DocumentView.From(doc);
        // С провенансом (#594, #595): без идентификатора развёрнутой ссылки повторы не свернуть, а
        // копию чужого документа не отличить от собственных полей.
        var ctx = await entityResolver.ResolveAsync(view, keepRefProvenance: true, ct);
        await entityResolver.ApplyDefaultsAsync(ctx, view, ct);
        await entityResolver.ResolveEnumLabelsAsync(ctx, view, ct);
        await entityResolver.ResolveComputedFieldsAsync(ctx, view, [], ct);
        return JsonSerializer.SerializeToElement(ctx.Data);
    }

    public async Task<SnapshotPage<CatalogEntrySummary>> ListCatalogEntriesAsync(
        string? scope, Guid? scopeId, Guid? typeId, string? search,
        int offset = 0, int limit = DomainSnapshotLimits.CatalogEntriesDefault,
        CancellationToken ct = default)
    {
        CatalogScope? parsed = Enum.TryParse<CatalogScope>(scope, true, out var s) ? s : null;

        // Намеренно мимо ListCommonDataEntriesQuery: тот дёргает EnsureProfileAsync и СОЗДАЁТ
        // объект-профиль уровня. Чтение через MCP не имеет права писать в БД.
        var entries = await domainObjects.FindAsync(e => e.Facet == null
            && (!parsed.HasValue || e.ScopeLevel == parsed.Value)
            && (!scopeId.HasValue || e.ScopeId == scopeId.Value)
            && (!typeId.HasValue || e.CompositeTypeId == typeId.Value), ct);

        if (!string.IsNullOrWhiteSpace(search))
            entries = [.. entries.Where(e =>
                e.DisplayName is not null &&
                e.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))];

        var typeMap = await TypeMapAsync(entries.Select(e => e.CompositeTypeId), ct);
        // Сортировка ДО нарезки, с идентификатором как вторым ключом: иначе «страница 2» второго
        // вызова означала бы другие записи, а тёзки перекладывались бы между страницами.
        var ordered = entries
            .OrderBy(e => e.DisplayName)
            .ThenBy(e => e.Id)
            .Select(e =>
            {
                var t = typeMap.GetValueOrDefault(e.CompositeTypeId);
                return new CatalogEntrySummary(
                    e.Id, NameOf(e, t), e.CompositeTypeId,
                    t?.Code ?? "", t?.Name ?? "",
                    e.ScopeLevel.ToString(), e.ScopeId);
            })
            .ToArray();

        return SnapshotPage<CatalogEntrySummary>.Of(
            ordered, offset, limit, DomainSnapshotLimits.CatalogEntriesMax);
    }

    public async Task<CatalogEntryDetail?> GetCatalogEntryAsync(Guid entryId, CancellationToken ct = default)
    {
        var entry = await domainObjects.GetByIdAsync(entryId, ct);
        // Документ — не запись каталога: отдать его здесь значило бы дать второй, менее полный путь
        // к тому, что уже отдаёт get_document.
        if (entry is null || entry.Facet is not null) return null;

        var typeMap = await TypeMapAsync([entry.CompositeTypeId], ct);
        var type = typeMap.GetValueOrDefault(entry.CompositeTypeId);
        return new CatalogEntryDetail(
            entry.Id, NameOf(entry, type), entry.CompositeTypeId,
            type?.Code ?? "", type?.Name ?? "",
            entry.ScopeLevel.ToString(), entry.ScopeId,
            entry.Data.RootElement.Clone());
    }

    public async Task<DocumentTypeSchemaInfo?> GetDocumentTypeAsync(Guid typeId, CancellationToken ct = default)
    {
        var t = await mediator.Send(new GetDocumentTypeQuery(typeId), ct);
        return t is null ? null
            : new DocumentTypeSchemaInfo(t.Id, t.Code, t.Name, t.Kind.ToString(), t.ParentId, t.Schema.RootElement.Clone());
    }

    public async Task<SnapshotPage<MaterialQualityLinkInfo>> ListMaterialQualityLinksAsync(
        Guid setId, DateTimeOffset? changedSince = null,
        int offset = 0, int limit = DomainSnapshotLimits.MaterialLinksDefault,
        CancellationToken ct = default)
    {
        var empty = SnapshotPage<MaterialQualityLinkInfo>.Of(
            [], offset, limit, DomainSnapshotLimits.MaterialLinksMax);

        var set = await mediator.Send(new GetDocumentSetQuery(setId), ct);
        if (set is null) return empty;

        var section = await sections.GetByIdAsync(set.SectionId, ct);
        var constructionId = section?.ConstructionId;

        // Порядок = приоритет: первый победивший ключ остаётся. Тот же, что у QualityLinkResolver —
        // расхождение здесь означало бы, что агент видит не то, что попадёт в документ.
        var levels = new (CatalogScope Scope, Guid? ScopeId)[]
        {
            (CatalogScope.Set, setId),
            (CatalogScope.Section, set.SectionId),
            (CatalogScope.Construction, constructionId),
            (CatalogScope.System, null),
        };

        var winners = new Dictionary<string, (Guid DocId, CatalogScope Scope, Guid? ScopeId, DateTimeOffset UpdatedAt)>();
        foreach (var (scope, scopeId) in levels)
        {
            if (scope != CatalogScope.System && scopeId is null) continue; // разорванная цепочка — уровня просто нет
            foreach (var l in await mediator.Send(new ListMaterialLinksQuery(scope, scopeId), ct))
                winners.TryAdd(l.MaterialKey, (l.QualityDocumentId, scope, scopeId, l.UpdatedAt));
        }
        if (winners.Count == 0) return empty;

        var docs = (await mediator.Send(new ListQualityDocumentsQuery(null, null, null), ct))
            .ToDictionary(d => d.Id);
        var typeMap = await TypeMapAsync(docs.Values.Select(d => d.DocumentTypeId), ct);

        var ordered = winners
            // Отбор по времени — ПОСЛЕ схлопывания по приоритету: карта отдаётся действующая, и
            // фильтровать надо то, что действует, а не то, что лежит на каждом уровне (#598).
            .Where(kv => changedSince is not { } since || kv.Value.UpdatedAt > since)
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var doc = docs.GetValueOrDefault(kv.Value.DocId);
                var typeName = doc is null ? "" : typeMap.GetValueOrDefault(doc.DocumentTypeId)?.Name ?? "";
                return new MaterialQualityLinkInfo(
                    kv.Key, kv.Value.DocId, doc?.DisplayName ?? "", typeName,
                    kv.Value.Scope.ToString(), kv.Value.ScopeId, kv.Value.UpdatedAt);
            })
            .ToArray();

        return SnapshotPage<MaterialQualityLinkInfo>.Of(
            ordered, offset, limit, DomainSnapshotLimits.MaterialLinksMax);
    }

    public async Task<SnapshotPage<QualityDocumentSummary>> ListQualityDocumentsAsync(
        string? scope, Guid? scopeId, string? search, DateTimeOffset? changedSince = null,
        int offset = 0, int limit = DomainSnapshotLimits.QualityDocumentsDefault,
        CancellationToken ct = default)
    {
        CatalogScope? parsed = Enum.TryParse<CatalogScope>(scope, true, out var s) ? s : null;
        var docs = await mediator.Send(new ListQualityDocumentsQuery(parsed, scopeId, search), ct);
        if (changedSince is { } since) docs = [.. docs.Where(d => d.UpdatedAt > since)];
        var typeMap = await TypeMapAsync(docs.Select(d => d.DocumentTypeId), ct);

        // Имя, затем идентификатор: порядок запроса не обещан, а страничной выдаче нужен устойчивый —
        // иначе один и тот же документ попадёт на две страницы, а другой не попадёт ни на одну.
        var ordered = docs
            .OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Id)
            .Select(d => new QualityDocumentSummary(
                d.Id, d.DisplayName, d.DocumentTypeId,
                typeMap.GetValueOrDefault(d.DocumentTypeId)?.Name ?? "",
                d.Scope.ToString(), d.ScopeId, d.Source.ToString(),
                !string.IsNullOrEmpty(d.ScanBlobPath),
                d.Requisites?.RootElement.Clone() ?? EmptyObject, d.UpdatedAt))
            .ToArray();

        return SnapshotPage<QualityDocumentSummary>.Of(
            ordered, offset, limit, DomainSnapshotLimits.QualityDocumentsMax);
    }

    /// <summary>Имени может не быть вовсе — так заводятся профили уровней (issue #258). Безымянная
    /// строка в списке нечитаема, поэтому подставляем имя типа: оно и есть весь смысл такой записи.</summary>
    private static string NameOf(DomainObject e, DocumentType? type)
        => string.IsNullOrWhiteSpace(e.DisplayName) ? type?.Name ?? "" : e.DisplayName;

    private DocumentSummary ToSummary(DomainObject d, IReadOnlyDictionary<Guid, DocumentType> typeMap)
    {
        var t = typeMap.GetValueOrDefault(d.CompositeTypeId);
        return new DocumentSummary(d.Id, d.DisplayName ?? "", d.CompositeTypeId,
            t?.Code ?? "", t?.Name ?? "", d.Facet?.Status.ToString() ?? "");
    }

    /// <summary>Типы одним запросом: имя/код типа нужны почти в каждой форме, а запрос-на-документ
    /// дал бы N+1 на комплекте из десятков документов.</summary>
    /// <summary>Все типы по идентификатору — схема читается с учётом наследования, поэтому одного
    /// типа мало: цепочка предков нужна целиком.</summary>
    private async Task<Dictionary<Guid, DocumentType>> AllTypesAsync(CancellationToken ct)
        => (await types.GetAllAsync(ct)).ToDictionary(t => t.Id);

    private async Task<Dictionary<Guid, DocumentType>> TypeMapAsync(IEnumerable<Guid> typeIds, CancellationToken ct)
    {
        var wanted = typeIds.Distinct().ToHashSet();
        if (wanted.Count == 0) return [];
        var all = await types.GetAllAsync(ct);
        return all.Where(t => wanted.Contains(t.Id)).ToDictionary(t => t.Id);
    }
}
