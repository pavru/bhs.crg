using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.QualityDocs;

public class QualityDocHandlers(
    IRepository<QualityDocument> repo,
    IRepository<MaterialQualityLink> linkRepo
) :
    IRequestHandler<CreateQualityDocumentCommand, QualityDocument>,
    IRequestHandler<UpdateQualityDocumentCommand, QualityDocument>,
    IRequestHandler<SetQualityDocScanCommand, QualityDocument>,
    IRequestHandler<DeleteQualityDocumentCommand>,
    IRequestHandler<GetQualityDocumentQuery, QualityDocument?>,
    IRequestHandler<ListQualityDocumentsQuery, IReadOnlyList<QualityDocument>>,
    IRequestHandler<SetMaterialLinksCommand, int>,
    IRequestHandler<RemoveMaterialLinkCommand>,
    IRequestHandler<ListMaterialLinksQuery, IReadOnlyList<MaterialLinkRow>>
{
    public async Task<QualityDocument> Handle(CreateQualityDocumentCommand cmd, CancellationToken ct)
    {
        var doc = QualityDocument.Create(cmd.DocumentTypeId, cmd.DisplayName, cmd.Requisites, cmd.Scope, cmd.ScopeId, cmd.Source);
        doc.SetScan(cmd.ScanBlobPath, cmd.ScanFileName, cmd.ScanMimeType);
        await repo.AddAsync(doc, ct);
        await repo.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<QualityDocument> Handle(UpdateQualityDocumentCommand cmd, CancellationToken ct)
    {
        var doc = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException($"QualityDocument {cmd.Id} not found");
        doc.Update(cmd.DocumentTypeId, cmd.DisplayName, cmd.Requisites);
        repo.Update(doc);
        await repo.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<QualityDocument> Handle(SetQualityDocScanCommand cmd, CancellationToken ct)
    {
        var doc = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException($"QualityDocument {cmd.Id} not found");
        doc.SetScan(cmd.ScanBlobPath, cmd.ScanFileName, cmd.ScanMimeType);
        repo.Update(doc);
        await repo.SaveChangesAsync(ct);
        return doc;
    }

    public async Task Handle(DeleteQualityDocumentCommand cmd, CancellationToken ct)
    {
        var doc = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException($"QualityDocument {cmd.Id} not found");
        // удаляем связи, ссылающиеся на документ
        var links = await linkRepo.FindAsync(l => l.QualityDocumentId == cmd.Id, ct);
        foreach (var l in links) linkRepo.Remove(l);
        repo.Remove(doc);
        await linkRepo.SaveChangesAsync(ct);
        await repo.SaveChangesAsync(ct);
    }

    public Task<QualityDocument?> Handle(GetQualityDocumentQuery q, CancellationToken ct)
        => repo.GetByIdAsync(q.Id, ct);

    public async Task<IReadOnlyList<QualityDocument>> Handle(ListQualityDocumentsQuery q, CancellationToken ct)
    {
        var scope = q.Scope;
        var scopeId = q.ScopeId;
        var items = await repo.FindAsync(d =>
            (!scope.HasValue || d.Scope == scope.Value) &&
            (!scopeId.HasValue || d.ScopeId == scopeId.Value), ct);

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            items = items.Where(d => d.DisplayName.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return items.OrderBy(d => d.DisplayName).ToList();
    }

    public async Task<int> Handle(SetMaterialLinksCommand cmd, CancellationToken ct)
    {
        var existing = await linkRepo.FindAsync(l => l.Scope == cmd.Scope && l.ScopeId == cmd.ScopeId, ct);
        // ToDictionary бросил бы на дубле ключа; с #554 дублей не бывает — уникальный индекс.
        var byKey = existing.ToDictionary(l => l.MaterialKey);
        var count = 0;
        foreach (var material in cmd.Materials)
        {
            var key = MatchKeyNormalizer.Normalize(material.Key);
            if (key.Length == 0) continue;
            if (byKey.TryGetValue(key, out var link))
            {
                link.Retarget(cmd.QualityDocumentId);
                // Метку обновляем, но пустой не затираем: перепривязка с экрана контроля идёт без
                // имени, и она не должна отнимать имя, добытое при первой привязке.
                link.DescribeMaterial(material.Label);
                linkRepo.Update(link);
            }
            else
            {
                await linkRepo.AddAsync(MaterialQualityLink.Create(
                    cmd.Scope, cmd.ScopeId, key, cmd.QualityDocumentId, material.Label), ct);
            }
            count++;
        }
        await linkRepo.SaveChangesAsync(ct);
        return count;
    }

    public async Task Handle(RemoveMaterialLinkCommand cmd, CancellationToken ct)
    {
        var link = await linkRepo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        linkRepo.Remove(link);
        await linkRepo.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<MaterialLinkRow>> Handle(ListMaterialLinksQuery q, CancellationToken ct)
    {
        // Область необязательна: без неё отдаём связки ВСЕХ областей (экран контроля смотрит поперёк).
        var links = q.Scope is { } scope
            ? await linkRepo.FindAsync(l => l.Scope == scope && l.ScopeId == q.ScopeId, ct)
            : await linkRepo.GetAllAsync(ct);
        if (links.Count == 0) return [];

        // Имя документа — вторым запросом по нужным id, а не обходом в памяти всей библиотеки.
        var docIds = links.Select(l => l.QualityDocumentId).Distinct().ToList();
        var docs = await repo.FindAsync(d => docIds.Contains(d.Id), ct);
        var byId = docs.ToDictionary(d => d.Id);

        return links
            .Select(l =>
            {
                byId.TryGetValue(l.QualityDocumentId, out var doc);
                return new MaterialLinkRow(
                    l.Id, l.MaterialKey, l.MaterialLabel, l.Scope, l.ScopeId, l.QualityDocumentId,
                    // Документа может не быть только у связки, пережившей ручную чистку БД: внешний
                    // ключ с каскадом (#554) такие больше не оставляет. Честнее пустого имени.
                    doc?.DisplayName ?? "(документ удалён)",
                    doc?.Requisites.RootElement.TryGetProperty("ТипДокумента", out var t) == true
                        ? t.GetString() : null,
                    l.CreatedAt, l.UpdatedAt);
            })
            .OrderBy(r => r.QualityDocumentName)
            .ThenBy(r => r.MaterialLabel ?? r.MaterialKey)
            .ToList();
    }
}
