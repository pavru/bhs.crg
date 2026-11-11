using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Search;

/// <summary>
/// Группа поиска «Счета» — вклад модуля в контракт «Поиск записей» (shell-05, задача 1.2; ТЗ AUTH-16.1,
/// CORE-34). Ищет то же, что поиск списка счетов: номер, поставщика, назначение — каждое слово запроса
/// хоть в одном из них.
///
/// <para>Карточка — как в макете палитры (М-4): «№ 418 · ООО „КабельТорг“», назначение пояснением и сумма.
/// Стройки у счёта нет — раздел у него по строкам разноски, — поэтому места карточка не называет.</para>
///
/// <para>Архива у счёта нет (<see cref="RecordSearchArchive.None" />). Поставщики читаются ПОКАЗОМ, а не
/// выбором: найден счёт, а не поставщик, и счёт архивного поставщика обязан находиться по его имени — как
/// он читается по имени в списке (issue #1185). Место чтения вписано в <c>ArchiveReadInventoryTests</c>.</para>
/// </summary>
public sealed class InvoiceRecordSearch(CostsDbContext db, IModuleCatalog catalog) : IRecordSearch
{
    public const string Group = "invoices";

    public static readonly RecordSearchGroup Declared = new(
        Group, "Счета", CostsRecordTypes.InvoiceCode, InvoiceRead, RecordSearchArchive.None);

    /// <summary>Право списка счетов — тот же ключ у группы поиска.</summary>
    private const string InvoiceRead = "costs.invoice.read";

    public IReadOnlyList<RecordSearchGroup> Groups { get; } = [Declared];

    public async Task<ContractAnswer<RecordSearchHits>> SearchAsync(
        string group, RecordSearchQuery query, ModuleReader reader, CancellationToken ct)
    {
        Own(group);
        if (ContractAnswer.Refusal<RecordSearchHits>(reader, InvoiceRead) is { } refused) return refused;

        var suppliers = await SuppliersAsync(ct);
        var invoices = db.Invoices.AsNoTracking();
        foreach (var word in query.Words)
        {
            var pattern = RecordSearchText.LikePattern(word);
            // Поставщик — в справочнике ядра, а не в схеме модуля: совпавшие по имени отбираются здесь и
            // приходят в запрос списком ключей.
            List<Guid> named = [.. suppliers.Where(s => RecordSearchText.AnyContains(word, s.Value)).Select(s => s.Key)];
            invoices = invoices.Where(i =>
                (i.Number != null && EF.Functions.ILike(i.Number.Replace("ё", "е").Replace("Ё", "Е"), pattern, @"\"))
                || (i.Purpose != null && EF.Functions.ILike(i.Purpose.Replace("ё", "е").Replace("Ё", "Е"), pattern, @"\"))
                || (i.SupplierId != null && named.Contains(i.SupplierId.Value)));
        }

        // Свежие первыми: ищут обычно счёт, который только что пришёл.
        var found = await invoices.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id)
            .Take(query.Take + 1)
            .Select(i => new Row(i.Id, i.Number, i.SupplierId, i.Purpose, i.Total))
            .ToListAsync(ct);
        return ContractAnswer.Shown(new RecordSearchHits(
            [.. found.Take(query.Take).Select(r => Card(r, suppliers))], found.Count > query.Take));
    }

    public async Task<ContractAnswer<IReadOnlyList<RecordSearchHit>>> CardsAsync(
        string group, IReadOnlyCollection<Guid> ids, ModuleReader reader, CancellationToken ct)
    {
        Own(group);
        if (ContractAnswer.Refusal<IReadOnlyList<RecordSearchHit>>(reader, InvoiceRead) is { } refused) return refused;

        var found = await db.Invoices.AsNoTracking().Where(i => ids.Contains(i.Id))
            .Select(i => new Row(i.Id, i.Number, i.SupplierId, i.Purpose, i.Total))
            .ToListAsync(ct);
        var suppliers = found.Any(r => r.SupplierId is not null) ? await SuppliersAsync(ct) : new Dictionary<Guid, string>();
        return ContractAnswer.Shown<IReadOnlyList<RecordSearchHit>>([.. found.Select(r => Card(r, suppliers))]);
    }

    private sealed record Row(Guid Id, string? Number, Guid? SupplierId, string? Purpose, decimal? Total);

    private static RecordSearchHit Card(Row r, IReadOnlyDictionary<Guid, string> suppliers)
    {
        var number = string.IsNullOrWhiteSpace(r.Number) ? "Без номера" : $"№ {r.Number}";
        var supplier = r.SupplierId is { } id && suppliers.TryGetValue(id, out var name) ? name : null;
        return new RecordSearchHit(r.Id, supplier is null ? number : $"{number} · {supplier}",
            Detail: string.IsNullOrWhiteSpace(r.Purpose) ? null : r.Purpose, Amount: r.Total);
    }

    /// <summary>
    /// Имена организаций — поставщиков счетов. Вида «Организация» на чистой установке может не быть вовсе
    /// (порт отвечает <c>null</c>) — тогда и счетов с поставщиком нет, и искать по имени нечего.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> SuppliersAsync(CancellationToken ct) =>
        (await catalog.ListAsync(CostsDirectories.Organization, RecordsFor.Display, ct))
            ?.ToDictionary(o => o.Id, o => o.DisplayName)
        ?? new Dictionary<Guid, string>();

    private static void Own(string group)
    {
        if (group != Group)
            throw new ArgumentException($"У модуля «costs» нет группы поиска «{group}».", nameof(group));
    }
}
