using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Common;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// ПОБЕДИВШИЕ связки «материал → документ качества» на заданном уровне: по цепочке вверх, где узкий
/// уровень выигрывает у широкого (Set=1 … System=5). Ровно то, что подставится при генерации.
///
/// Алгоритм жил в двух копиях — у резолвера связок и у среза для внешнего агента, — и обе несли
/// комментарий «не расходиться» (issue #624). Расхождение здесь означало бы худший вид ошибки:
/// агент и экран показывают один сертификат, а в документ попадает другой, и заметить это можно
/// только по готовому PDF.
/// </summary>
public static class MaterialQualityChain
{
    /// <summary>
    /// Победившая связка материала. Уровень и время правки нужны не всем вызывающим, но берутся
    /// из той же строки — второй запрос за ними был бы запросом за уже прочитанным.
    /// </summary>
    public readonly record struct Winner(
        string MaterialKey, string? MaterialLabel, Guid QualityDocumentId,
        CatalogScope Scope, Guid? ScopeId, DateTimeOffset UpdatedAt);

    /// <summary>
    /// Победители по ключу материала для места (<paramref name="scope"/>, <paramref name="scopeId"/>).
    /// Общесистемные связки видны отовсюду; уровень без объекта области (кроме System) в цепочку не
    /// входит — «разорванная цепочка» это отсутствие уровня, а не повод отказать.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, Winner>> WinnersAsync(
        AppDbContext db, CatalogScope scope, Guid? scopeId, CancellationToken ct = default)
    {
        var chain = await ScopeChains.LoadForScopeAsync(db, scope, scopeId, ct);

        var links = await db.MaterialQualityLinks.AsNoTracking()
            .Where(l => l.Scope == CatalogScope.System
                        || (l.Scope == CatalogScope.Set && l.ScopeId == chain.SetId)
                        || (l.Scope == CatalogScope.Section && l.ScopeId == chain.SectionId)
                        || (l.Scope == CatalogScope.Construction && l.ScopeId == chain.ConstructionId))
            .ToListAsync(ct);

        var winners = new Dictionary<string, Winner>();
        foreach (var l in links.OrderBy(l => (int)l.Scope)) // первый — самый узкий уровень
            winners.TryAdd(l.MaterialKey,
                new Winner(l.MaterialKey, l.MaterialLabel, l.QualityDocumentId, l.Scope, l.ScopeId, l.UpdatedAt));
        return winners;
    }

    /// <summary>Победители для комплекта — самый частый случай (генерация идёт по комплекту).</summary>
    public static Task<IReadOnlyDictionary<string, Winner>> WinnersForSetAsync(
        AppDbContext db, Guid setId, CancellationToken ct = default)
        => WinnersAsync(db, CatalogScope.Set, setId, ct);
}
