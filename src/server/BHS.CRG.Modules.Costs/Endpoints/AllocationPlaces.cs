using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Статья вне строек в выборе цели разноски и в справочнике (задача F3, issue #1087).</summary>
public sealed record CostsArticle(Guid Id, string Name);

/// <summary>
/// Куда разносятся затраты: стройки с разделами и статьи вне строек (ТЗ COST-10, COST-10.1). Одним значением,
/// потому что проверка цели, её название в ответе и в журнале и признак «цель потеряна» спрашивают оба
/// списка сразу — порознь их пришлось бы тащить через каждый вызов парой.
/// </summary>
/// <param name="ArticlesKnown">Справочник статей прочитан. <c>false</c> — типа статей в установке нет:
/// «статей нет» это не значит, и часть на статью тогда НЕ потеряна — о ней просто нечего сказать (issue
/// #1184). Сведи мы одно к другому, потерянной выглядела бы каждая часть на статью разом.</param>
public sealed record AllocationPlaces(
    IReadOnlyList<ModuleConstruction> Sites, IReadOnlyList<CostsArticle> Articles, bool ArticlesKnown = true)
{
    /// <summary>Для счёта без разноски: ни строек, ни статей ему не нужно.</summary>
    public static readonly AllocationPlaces None = new([], []);

    /// <summary>
    /// Цели, которые у счёта УЖЕ записаны: запись принимает их, даже если цели больше нет (ТЗ CORE-34.4,
    /// issue #1184). Потерянная ссылка не стирается и не запирает счёт — иначе старая потеря в одной
    /// части не давала бы поправить сумму в другой. Новая ссылка в пустоту отвергается по-прежнему.
    /// </summary>
    public IReadOnlySet<AllocationTarget> Kept { get; init; } = new HashSet<AllocationTarget>();

    /// <summary>
    /// Записи, которые в ядре ЕСТЬ, — по обратному опросу; <c>null</c> — не спрашивали. Нужны, чтобы
    /// отличить удалённую статью от записи, переведённой в другой вид: в списке статей нет обеих, а
    /// потеряна только первая. Счётчик потерянных ссылок вторую не считает — не должна и пометка
    /// (ревью PR #1211).
    /// </summary>
    public IReadOnlySet<Guid>? Existing { get; init; }

    public AllocationPlaces Keeping(IEnumerable<InvoiceAllocation> stored) => this with
    {
        Kept = stored.Select(p => new AllocationTarget(p.ConstructionId, p.SectionId, p.ArticleId)).ToHashSet(),
    };

    public ModuleConstruction? Site(Guid? id) => id is { } site ? Sites.FirstOrDefault(s => s.Id == site) : null;

    public CostsArticle? Article(Guid? id) => id is { } article ? Articles.FirstOrDefault(a => a.Id == article) : null;
}

/// <summary>Читает <see cref="AllocationPlaces" /> из ядра: стройки — одним портом, статьи — другим.</summary>
public sealed class AllocationPlacesSource(IModuleConstructions sites, IModuleCatalog catalog)
{
    public async Task<AllocationPlaces> LoadAsync(CancellationToken ct)
    {
        var articles = await ArticlesAsync(catalog, ct);
        return new(await sites.ListAsync(ct), articles ?? [], articles is not null);
    }

    /// <summary>
    /// Статьи по названию; <c>null</c> — типа статей нет (проекция его пропустила). Новая часть на статью
    /// тогда отвергается, а записанная — не потеряна: см. <see cref="AllocationPlaces.ArticlesKnown" />.
    /// </summary>
    public static async Task<IReadOnlyList<CostsArticle>?> ArticlesAsync(IModuleCatalog catalog, CancellationToken ct) =>
        await catalog.ListAsync(CostsRecordTypes.ArticleCode, ct) is { } entries
            ? [.. entries.Select(e => new CostsArticle(e.Id, e.DisplayName))]
            : null;
}
