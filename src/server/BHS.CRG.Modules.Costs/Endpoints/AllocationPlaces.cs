using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Статья вне строек в выборе цели разноски и в справочнике (задача F3, issue #1087).</summary>
public sealed record CostsArticle(Guid Id, string Name);

/// <summary>
/// Куда разносятся затраты: стройки с разделами и статьи вне строек (ТЗ COST-10, COST-10.1). Одним значением,
/// потому что проверка цели, её название в ответе и в журнале и признак «цель потеряна» спрашивают оба
/// списка сразу — порознь их пришлось бы тащить через каждый вызов парой.
/// </summary>
public sealed record AllocationPlaces(IReadOnlyList<ModuleConstruction> Sites, IReadOnlyList<CostsArticle> Articles)
{
    /// <summary>Для счёта без разноски: ни строек, ни статей ему не нужно.</summary>
    public static readonly AllocationPlaces None = new([], []);

    public ModuleConstruction? Site(Guid? id) => id is { } site ? Sites.FirstOrDefault(s => s.Id == site) : null;

    public CostsArticle? Article(Guid? id) => id is { } article ? Articles.FirstOrDefault(a => a.Id == article) : null;
}

/// <summary>Читает <see cref="AllocationPlaces" /> из ядра: стройки — одним портом, статьи — другим.</summary>
public sealed class AllocationPlacesSource(IModuleConstructions sites, IModuleCatalog catalog)
{
    public async Task<AllocationPlaces> LoadAsync(CancellationToken ct) =>
        new(await sites.ListAsync(ct), await ArticlesAsync(catalog, ct) ?? []);

    /// <summary>
    /// Статьи по названию; <c>null</c> — типа статей нет (проекция его пропустила). Для разноски это то же,
    /// что «статей нет»: часть на статью тогда читается потерянной, а новая — отвергается.
    /// </summary>
    public static async Task<IReadOnlyList<CostsArticle>?> ArticlesAsync(IModuleCatalog catalog, CancellationToken ct) =>
        await catalog.ListAsync(CostsRecordTypes.ArticleCode, ct) is { } entries
            ? [.. entries.Select(e => new CostsArticle(e.Id, e.DisplayName))]
            : null;
}
