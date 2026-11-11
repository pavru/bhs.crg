using BHS.CRG.Modules.Search;
using BHS.CRG.Api.Auth;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Search;

/// <summary>
/// Группы поиска ЯДРА — стройки и наборы данных (shell-05, задача 1.3; ТЗ CORE-34). Тем же контрактом
/// <see cref="IRecordSearch" />, что у модулей, но не через порт: ядро — не модуль, выключить его нельзя,
/// и регистрироваться ему как поставщику незачем (В-1 — Б: «группы ядра адрес ищет сам»). Одна форма у
/// всех групп — значит одна проверка прав, архива и предела в <see cref="RecordSearchService" />.
///
/// <para>Ни у стройки, ни у набора данных архивного состояния нет (<c>design.md</c>, Context), поэтому
/// обе группы объявлены <see cref="RecordSearchArchive.None" />.</para>
/// </summary>
public sealed class CoreRecordSearch(AppDbContext db) : IRecordSearch
{
    public const string Module = "core";

    public const string ConstructionsGroup = "constructions";
    public const string DataSetsGroup = "datasets";

    /// <summary>Код типа записи стройки — им клиент находит её экран (<c>RECORD_ROUTES</c>).</summary>
    public const string ConstructionRecordType = "Стройка";

    /// <summary>Код типа записи набора данных.</summary>
    public const string DataSetRecordType = "НаборДанных";

    public static readonly IReadOnlyList<RecordSearchGroup> Declared =
    [
        new(ConstructionsGroup, "Стройки", ConstructionRecordType, CorePermissions.ConstructionsRead,
            RecordSearchArchive.None),
        new(DataSetsGroup, "Наборы данных", DataSetRecordType, CorePermissions.DataSetsRead,
            RecordSearchArchive.None),
    ];

    public IReadOnlyList<RecordSearchGroup> Groups => Declared;

    public async Task<ContractAnswer<RecordSearchHits>> SearchAsync(
        string group, RecordSearchQuery query, ModuleReader reader, CancellationToken ct)
    {
        var declared = Find(group);
        if (ContractAnswer.Refusal<RecordSearchHits>(reader, declared.Requires) is { } refused) return refused;

        var found = group == ConstructionsGroup
            ? await ConstructionsAsync(Matching(db.Constructions.AsNoTracking(), query.Words), query.Take + 1, ct)
            : await DataSetsAsync(MatchingDataSets(query.Words), query.Take + 1, ct);
        return ContractAnswer.Shown(new RecordSearchHits([.. found.Take(query.Take)], found.Count > query.Take));
    }

    public async Task<ContractAnswer<IReadOnlyList<RecordSearchHit>>> CardsAsync(
        string group, IReadOnlyCollection<Guid> ids, ModuleReader reader, CancellationToken ct)
    {
        var declared = Find(group);
        if (ContractAnswer.Refusal<IReadOnlyList<RecordSearchHit>>(reader, declared.Requires) is { } refused)
            return refused;

        IReadOnlyList<RecordSearchHit> cards = group == ConstructionsGroup
            ? await ConstructionsAsync(db.Constructions.AsNoTracking().Where(c => ids.Contains(c.Id)), null, ct)
            : await DataSetsAsync(db.DataSetFiles.AsNoTracking().Where(f => ids.Contains(f.Id)), null, ct);
        return ContractAnswer.Shown(cards);
    }

    private static RecordSearchGroup Find(string group) =>
        Declared.FirstOrDefault(g => g.Code == group)
        ?? throw new ArgumentException($"У ядра нет группы поиска «{group}».", nameof(group));

    // Каждое слово — хоть в одном поле. «ё» в колонке приводится к «е» той же заменой, что у запроса,
    // регистр снимает ILIKE.
    private static IQueryable<Domain.Documents.Construction> Matching(
        IQueryable<Domain.Documents.Construction> constructions, IReadOnlyList<string> words)
    {
        foreach (var word in words)
        {
            var pattern = RecordSearchText.LikePattern(word);
            constructions = constructions.Where(c =>
                EF.Functions.ILike(c.Name.Replace("ё", "е").Replace("Ё", "Е"), pattern, @"\")
                || (c.ExternalCode != null
                    && EF.Functions.ILike(c.ExternalCode.Replace("ё", "е").Replace("Ё", "Е"), pattern, @"\")));
        }
        return constructions;
    }

    private IQueryable<Domain.DataSets.DataSetFile> MatchingDataSets(IReadOnlyList<string> words)
    {
        var files = db.DataSetFiles.AsNoTracking();
        foreach (var word in words)
        {
            var pattern = RecordSearchText.LikePattern(word);
            files = files.Where(f => EF.Functions.ILike(f.Name.Replace("ё", "е").Replace("Ё", "Е"), pattern, @"\"));
        }
        return files;
    }

    private static async Task<List<RecordSearchHit>> ConstructionsAsync(
        IQueryable<Domain.Documents.Construction> source, int? take, CancellationToken ct)
    {
        var ordered = source.OrderBy(c => c.Name).ThenBy(c => c.Id);
        var rows = await (take is { } n ? ordered.Take(n) : ordered)
            .Select(c => new { c.Id, c.Name, c.ExternalCode })
            .ToListAsync(ct);
        return [.. rows.Select(c => new RecordSearchHit(c.Id, c.Name, Detail: c.ExternalCode))];
    }

    private async Task<List<RecordSearchHit>> DataSetsAsync(
        IQueryable<Domain.DataSets.DataSetFile> source, int? take, CancellationToken ct)
    {
        var ordered = source.OrderBy(f => f.Name).ThenBy(f => f.Id);
        var rows = await (take is { } n ? ordered.Take(n) : ordered)
            .Select(f => new { f.Id, f.Name, f.Scope, f.ScopeId })
            .ToListAsync(ct);
        var places = await PlacesAsync([.. rows.Select(r => (r.Scope, r.ScopeId))], ct);
        return
        [
            .. rows.Select(r => new RecordSearchHit(r.Id, r.Name,
                Place: r.ScopeId is { } id && places.TryGetValue((r.Scope, id), out var place) ? place : null)),
        ];
    }

    /// <summary>
    /// Стройка уровня набора: своя у стройки, у раздела — его стройка, у комплекта — стройка его раздела.
    /// Системный набор места не имеет.
    /// </summary>
    private async Task<Dictionary<(CatalogScope, Guid), string>> PlacesAsync(
        IReadOnlyList<(CatalogScope Scope, Guid? Id)> scopes, CancellationToken ct)
    {
        List<Guid> Of(CatalogScope level) =>
            [.. scopes.Where(s => s.Scope == level && s.Id is not null).Select(s => s.Id!.Value).Distinct()];

        var places = new Dictionary<(CatalogScope, Guid), string>();
        var constructionIds = Of(CatalogScope.Construction);
        var sectionIds = Of(CatalogScope.Section);
        var setIds = Of(CatalogScope.Set);
        if (constructionIds.Count + sectionIds.Count + setIds.Count == 0) return places;

        foreach (var c in await db.Constructions.AsNoTracking().Where(c => constructionIds.Contains(c.Id))
                     .Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            places[(CatalogScope.Construction, c.Id)] = c.Name;

        foreach (var s in await db.Sections.AsNoTracking().Where(s => sectionIds.Contains(s.Id))
                     .Join(db.Constructions, s => s.ConstructionId, c => c.Id, (s, c) => new { s.Id, c.Name })
                     .ToListAsync(ct))
            places[(CatalogScope.Section, s.Id)] = s.Name;

        foreach (var d in await db.DocumentSets.AsNoTracking().Where(d => setIds.Contains(d.Id))
                     .Join(db.Sections, d => d.SectionId, s => s.Id, (d, s) => new { d.Id, s.ConstructionId })
                     .Join(db.Constructions, x => x.ConstructionId, c => c.Id, (x, c) => new { x.Id, c.Name })
                     .ToListAsync(ct))
            places[(CatalogScope.Set, d.Id)] = d.Name;

        return places;
    }
}
