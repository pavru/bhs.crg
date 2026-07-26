using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Documents;
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
    IRepository<Construction> constructions) : IDomainSnapshotService
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public async Task<IReadOnlyList<ConstructionSummary>> ListConstructionsAsync(
        Guid userId, CancellationToken ct = default)
    {
        var list = await mediator.Send(new ListConstructionsQuery(userId), ct);
        var setIds = list.SelectMany(c => c.Sections).SelectMany(s => s.DocumentSets).Select(ds => ds.Id).ToList();
        var counts = await objects.CountDocumentsInSetsAsync(setIds, ct);

        return [.. list.Select(c => new ConstructionSummary(
            c.Id, c.Name,
            c.Sections.Count,
            c.Sections.Sum(s => s.DocumentSets.Count),
            c.Sections.SelectMany(s => s.DocumentSets).Sum(ds => counts.GetValueOrDefault(ds.Id))))];
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

    public async Task<DocumentDetail?> GetDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        var doc = await mediator.Send(new GetDocumentInstanceQuery(documentId), ct);
        if (doc is null) return null;

        var typeMap = await TypeMapAsync([doc.CompositeTypeId], ct);
        var type = typeMap.GetValueOrDefault(doc.CompositeTypeId);

        DocumentSet? set = null;
        if (doc.ScopeLevel == CatalogScope.Set && doc.ScopeId is { } sid)
            set = await mediator.Send(new GetDocumentSetQuery(sid), ct);

        return new DocumentDetail(
            doc.Id, doc.DisplayName ?? "", doc.CompositeTypeId,
            type?.Code ?? "", type?.Name ?? "",
            doc.Facet?.Status.ToString() ?? "",
            set?.Id, set?.Name,
            doc.Data.RootElement.Clone());
    }

    public async Task<DocumentTypeSchemaInfo?> GetDocumentTypeAsync(Guid typeId, CancellationToken ct = default)
    {
        var t = await mediator.Send(new GetDocumentTypeQuery(typeId), ct);
        return t is null ? null
            : new DocumentTypeSchemaInfo(t.Id, t.Code, t.Name, t.Kind.ToString(), t.ParentId, t.Schema.RootElement.Clone());
    }

    public async Task<IReadOnlyList<QualityDocumentSummary>> ListQualityDocumentsAsync(
        string? scope, Guid? scopeId, string? search, CancellationToken ct = default)
    {
        CatalogScope? parsed = Enum.TryParse<CatalogScope>(scope, true, out var s) ? s : null;
        var docs = await mediator.Send(new ListQualityDocumentsQuery(parsed, scopeId, search), ct);
        var typeMap = await TypeMapAsync(docs.Select(d => d.DocumentTypeId), ct);

        return [.. docs.Select(d => new QualityDocumentSummary(
            d.Id, d.DisplayName, d.DocumentTypeId,
            typeMap.GetValueOrDefault(d.DocumentTypeId)?.Name ?? "",
            d.Scope.ToString(), d.ScopeId, d.Source.ToString(),
            !string.IsNullOrEmpty(d.ScanBlobPath),
            d.Requisites?.RootElement.Clone() ?? EmptyObject))];
    }

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
