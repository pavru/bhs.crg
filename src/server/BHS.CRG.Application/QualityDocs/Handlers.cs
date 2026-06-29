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
    IRequestHandler<ListMaterialLinksQuery, IReadOnlyList<MaterialQualityLink>>
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
        var byKey = existing.ToDictionary(l => l.MaterialKey);
        var count = 0;
        foreach (var rawKey in cmd.MaterialKeys)
        {
            var key = MaterialKeyNormalizer.Normalize(rawKey);
            if (key.Length == 0) continue;
            if (byKey.TryGetValue(key, out var link))
            {
                link.Retarget(cmd.QualityDocumentId);
                linkRepo.Update(link);
            }
            else
            {
                await linkRepo.AddAsync(MaterialQualityLink.Create(cmd.Scope, cmd.ScopeId, key, cmd.QualityDocumentId), ct);
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

    public Task<IReadOnlyList<MaterialQualityLink>> Handle(ListMaterialLinksQuery q, CancellationToken ct)
        => linkRepo.FindAsync(l => l.Scope == q.Scope && l.ScopeId == q.ScopeId, ct);
}
