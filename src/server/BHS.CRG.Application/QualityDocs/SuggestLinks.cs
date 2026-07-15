using System.Globalization;
using System.Text.RegularExpressions;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.QualityDocs;

public record SuggestMaterial(string Key, string Name);
public record LinkSuggestion(string MaterialKey, string MaterialName, Guid QualityDocumentId, string DocDisplayName, double Score);

/// <summary>
/// Предлагает связи «материал → документ качества» для ещё не связанных материалов,
/// основываясь на истории связей (покрытие документа = ключи уже привязанных к нему материалов)
/// и сходстве наименований. Ничего не сохраняет — пользователь подтверждает.
/// </summary>
public record SuggestLinksQuery(Guid SetId, IReadOnlyList<SuggestMaterial> Materials) : IRequest<IReadOnlyList<LinkSuggestion>>;

public class SuggestLinksHandler(
    IRepository<DocumentSet> setRepo,
    IRepository<Section> sectionRepo,
    IRepository<MaterialQualityLink> linkRepo,
    IRepository<QualityDocument> docRepo
) : IRequestHandler<SuggestLinksQuery, IReadOnlyList<LinkSuggestion>>
{
    private const double Threshold = 0.6;

    public async Task<IReadOnlyList<LinkSuggestion>> Handle(SuggestLinksQuery q, CancellationToken ct)
    {
        var set = await setRepo.GetByIdAsync(q.SetId, ct);
        if (set is null) return [];
        var section = await sectionRepo.GetByIdAsync(set.SectionId, ct);
        var constructionId = section?.ConstructionId ?? Guid.Empty;
        var sectionId = set.SectionId;
        var setId = q.SetId;

        var links = await linkRepo.FindAsync(l =>
            (l.Scope == CatalogScope.Set && l.ScopeId == setId) ||
            (l.Scope == CatalogScope.Section && l.ScopeId == sectionId) ||
            (l.Scope == CatalogScope.Construction && l.ScopeId == constructionId) ||
            l.Scope == CatalogScope.System, ct);
        if (links.Count == 0) return [];

        // покрытие документа = токены ключей привязанных к нему материалов
        var coverage = new Dictionary<Guid, HashSet<string>>();
        var linkedKeys = new HashSet<string>();
        foreach (var l in links)
        {
            linkedKeys.Add(l.MaterialKey);
            if (!coverage.TryGetValue(l.QualityDocumentId, out var set2))
                coverage[l.QualityDocumentId] = set2 = [];
            foreach (var tk in Tokenize(l.MaterialKey)) set2.Add(tk);
        }

        var docIds = coverage.Keys.ToList();
        var docs = await docRepo.FindAsync(d => docIds.Contains(d.Id), ct);
        var docName = docs.ToDictionary(d => d.Id, d => d.DisplayName);

        var result = new List<LinkSuggestion>();
        foreach (var m in q.Materials)
        {
            var key = MatchKeyNormalizer.Normalize(m.Key);
            if (key.Length == 0 || linkedKeys.Contains(key)) continue; // уже связан или пуст

            var matTokens = Tokenize(key);
            if (matTokens.Count == 0) continue;

            Guid bestDoc = Guid.Empty;
            double bestScore = 0;
            foreach (var (docId, cov) in coverage)
            {
                var overlap = matTokens.Count(t => cov.Contains(t));
                var score = (double)overlap / matTokens.Count;
                if (score > bestScore) { bestScore = score; bestDoc = docId; }
            }

            if (bestScore >= Threshold && docName.TryGetValue(bestDoc, out var name))
                result.Add(new LinkSuggestion(key, m.Name, bestDoc, name, Math.Round(bestScore, 2)));
        }

        return result.OrderByDescending(r => r.Score).ToList();
    }

    private static readonly Regex Splitter = new(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

    private static HashSet<string> Tokenize(string s)
    {
        var tokens = Splitter.Split(s.ToLower(CultureInfo.InvariantCulture))
            .Where(t => t.Length >= 2);
        return [.. tokens];
    }
}
