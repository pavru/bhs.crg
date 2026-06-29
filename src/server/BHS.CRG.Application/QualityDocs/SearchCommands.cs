using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.QualityDocs;

public record SearchQualityDocsQuery(string Query) : IRequest<IReadOnlyList<SearchCandidate>>;

public class SearchQualityDocsHandler(IQualityDocSearch search)
    : IRequestHandler<SearchQualityDocsQuery, IReadOnlyList<SearchCandidate>>
{
    public Task<IReadOnlyList<SearchCandidate>> Handle(SearchQualityDocsQuery q, CancellationToken ct)
        => string.IsNullOrWhiteSpace(q.Query) ? Task.FromResult<IReadOnlyList<SearchCandidate>>([]) : search.SearchAsync(q.Query, ct);
}

/// <summary>
/// Импортирует найденный по ссылке файл (скан) в библиотеку как новый документ качества.
/// Реквизиты не извлекаются — это делается распознаванием позже (пользователь подтверждает).
/// </summary>
public record ImportQualityDocFromUrlCommand(
    string Url, string Title, Guid DocumentTypeId, CatalogScope Scope, Guid? ScopeId) : IRequest<QualityDocument>;

public class ImportQualityDocFromUrlHandler(
    IFileUrlFetcher fetcher, IBlobStorage blob, IRepository<QualityDocument> repo
) : IRequestHandler<ImportQualityDocFromUrlCommand, QualityDocument>
{
    public async Task<QualityDocument> Handle(ImportQualityDocFromUrlCommand cmd, CancellationToken ct)
    {
        // Дедуп: тот же URL уже импортирован в эту область — возвращаем существующий документ.
        var existing = await repo.FindAsync(
            d => d.SourceUrl == cmd.Url && d.Scope == cmd.Scope && d.ScopeId == cmd.ScopeId, ct);
        if (existing.Count > 0) return existing[0];

        var file = await fetcher.FetchAsync(cmd.Url, ct);
        await using var ms = new MemoryStream(file.Bytes);
        var blobPath = await blob.UploadAsync(file.FileName, ms, file.MimeType, ct);

        var name = string.IsNullOrWhiteSpace(cmd.Title) ? file.FileName : cmd.Title.Trim();
        var doc = QualityDocument.Create(cmd.DocumentTypeId, name, System.Text.Json.JsonDocument.Parse("{}"),
            cmd.Scope, cmd.ScopeId, QualityDocSource.Web, sourceUrl: cmd.Url);
        doc.SetScan(blobPath, file.FileName, file.MimeType);
        await repo.AddAsync(doc, ct);
        await repo.SaveChangesAsync(ct);
        return doc;
    }
}
