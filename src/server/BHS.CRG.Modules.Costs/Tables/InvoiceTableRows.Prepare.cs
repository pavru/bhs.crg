using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Tables;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Tables;

/// <content>Подготовка чтения — одна на страницу и на счёт строк под готовыми отборами.</content>
public sealed partial class InvoiceTableRows : IModuleTableCounts
{
    /// <summary>Всё, что нужно запросу к счетам до самого запроса: справочники, «сегодня», ссылки не на месте.</summary>
    private sealed record Prepared(
        Dictionary<Guid, string> Names, InvoiceShares Shares, IReadOnlyDictionary<int, string> Calendar,
        DateOnly Today, InvoiceTroubles Troubles, TableSql<Invoice> Sql);

    /// <summary>
    /// Подготовка — ОДНА на все запросы вызова: страница спрашивает её для себя, счёт под готовыми
    /// отборами — сразу для всех. Опрос ядра идёт один, и об архиве спрашивают, если о нём спросил
    /// хоть один запрос: ответ с архивом покрывает и вопрос о потерях (ревью PR #1240).
    /// </summary>
    private async Task<Prepared> PrepareAsync(IReadOnlyList<ModuleTableQuery> queries, CancellationToken ct)
    {
        var names = await OrganizationNamesAsync(ct);

        // Объекты разноски — стройки и статьи вне строек одним списком названий: цель части — ровно
        // одно из двух. Раздел стройки — своей колонкой: отбор «по стройке» — по стройке целиком.
        var shares = InvoiceShares.Of(await places.LoadAsync(ct));

        // «Сегодня» — одно на весь ответ: и отбору, и клеткам. Спроси мы его дважды, запрос на
        // границе суток отобрал бы «просроченные» по вчерашнему дню, а признак показал бы по сегодняшнему.
        var today = await clock.TodayAsync(ct);

        // Ссылки не на месте — ТОЛЬКО когда о них спросили: колонкой, отбором, сортировкой или итогом.
        // Это опрос ядра по всем держащим колонкам модуля, и таблицу читают не только с экрана (наборы
        // данных, внешний агент) — им он не нужен вовсе. Архив — отдельным согласием: ссылок на
        // архивные записи на порядки больше, чем потерянных.
        var aboutArchive = queries.Any(q => Asked(q, InvoiceTable.ArchivedKey));
        var troubles = aboutArchive || queries.Any(q => Asked(q, InvoiceTable.LostKey))
            ? await trouble.ReadAsync(aboutArchive, ct)
            : InvoiceTroubles.None;

        var calendar = InvoicePeriods.Labels(today);
        return new(names, shares, calendar, today, troubles, Sql(names, shares, calendar, today, troubles));
    }

    /// <summary>
    /// Сколько счетов под каждым из отборов — тем же построителем запроса, каким читается страница:
    /// число, на которое нажимают, обязано совпасть с числом строк, которые после этого покажут.
    /// </summary>
    public async Task<IReadOnlyList<ModuleTableCount>> CountAsync(
        IReadOnlyList<ModuleTableQuery> queries, CancellationToken ct)
    {
        var prepared = await PrepareAsync(queries, ct);
        var counts = new List<ModuleTableCount>();
        foreach (var query in queries)
            counts.Add(new(
                await prepared.Sql.Where(db.Invoices.AsNoTracking(), query.Filter).CountAsync(ct),
                Doubts(query, prepared.Troubles)));
        return counts;
    }

    /// <summary>
    /// Опрос проверил не всё — говорим это у каждой колонки о ссылках, о которой спросили: причины
    /// непроверенности у потери и у архива одни. Без этого слова пустая клетка и ноль под отбором
    /// значили бы «всё на месте».
    /// </summary>
    private static IReadOnlyDictionary<string, string>? Doubts(ModuleTableQuery query, InvoiceTroubles troubles)
    {
        if (troubles.Doubt is not { } doubt) return null;
        var asked = new[] { InvoiceTable.LostKey, InvoiceTable.ArchivedKey }.Where(key => Asked(query, key))
            .ToDictionary(key => key, _ => doubt, StringComparer.Ordinal);
        return asked.Count == 0 ? null : asked;
    }
}
