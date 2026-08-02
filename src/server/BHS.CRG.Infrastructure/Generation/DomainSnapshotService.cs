using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using MediatR;

namespace BHS.CRG.Infrastructure.Generation;

/// <inheritdoc />
public class DomainSnapshotService(
    IMediator mediator,
    IDomainObjectRepository objects,
    IRepository<DocumentType> types,
    IRepository<Section> sections,
    IRepository<Construction> constructions,
    IRepository<DomainObject> domainObjects,
    IEntityResolver entityResolver) : IDomainSnapshotService
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

        return new DocumentDetail(
            doc.Id, doc.DisplayName ?? "", doc.CompositeTypeId,
            type?.Code ?? "", type?.Name ?? "",
            doc.Facet?.Status.ToString() ?? "",
            set?.Id, set?.Name,
            requisites, resolveRefs);
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
        var ctx = await entityResolver.ResolveAsync(view, ct);
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
        Guid setId,
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

        var winners = new Dictionary<string, (Guid DocId, CatalogScope Scope, Guid? ScopeId)>();
        foreach (var (scope, scopeId) in levels)
        {
            if (scope != CatalogScope.System && scopeId is null) continue; // разорванная цепочка — уровня просто нет
            foreach (var l in await mediator.Send(new ListMaterialLinksQuery(scope, scopeId), ct))
                winners.TryAdd(l.MaterialKey, (l.QualityDocumentId, scope, scopeId));
        }
        if (winners.Count == 0) return empty;

        var docs = (await mediator.Send(new ListQualityDocumentsQuery(null, null, null), ct))
            .ToDictionary(d => d.Id);
        var typeMap = await TypeMapAsync(docs.Values.Select(d => d.DocumentTypeId), ct);

        var ordered = winners
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var doc = docs.GetValueOrDefault(kv.Value.DocId);
                var typeName = doc is null ? "" : typeMap.GetValueOrDefault(doc.DocumentTypeId)?.Name ?? "";
                return new MaterialQualityLinkInfo(
                    kv.Key, kv.Value.DocId, doc?.DisplayName ?? "", typeName,
                    kv.Value.Scope.ToString(), kv.Value.ScopeId);
            })
            .ToArray();

        return SnapshotPage<MaterialQualityLinkInfo>.Of(
            ordered, offset, limit, DomainSnapshotLimits.MaterialLinksMax);
    }

    public async Task<SnapshotPage<QualityDocumentSummary>> ListQualityDocumentsAsync(
        string? scope, Guid? scopeId, string? search,
        int offset = 0, int limit = DomainSnapshotLimits.QualityDocumentsDefault,
        CancellationToken ct = default)
    {
        CatalogScope? parsed = Enum.TryParse<CatalogScope>(scope, true, out var s) ? s : null;
        var docs = await mediator.Send(new ListQualityDocumentsQuery(parsed, scopeId, search), ct);
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
                d.Requisites?.RootElement.Clone() ?? EmptyObject))
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
    private async Task<Dictionary<Guid, DocumentType>> TypeMapAsync(IEnumerable<Guid> typeIds, CancellationToken ct)
    {
        var wanted = typeIds.Distinct().ToHashSet();
        if (wanted.Count == 0) return [];
        var all = await types.GetAllAsync(ct);
        return all.Where(t => wanted.Contains(t.Id)).ToDictionary(t => t.Id);
    }
}
