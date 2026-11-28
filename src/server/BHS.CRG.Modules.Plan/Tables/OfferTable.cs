using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Tables;
using Microsoft.EntityFrameworkCore;
using static BHS.CRG.Modules.Tables.ModuleTableColumnKind;

namespace BHS.CRG.Modules.Plan.Tables;

internal sealed class OfferTable(PlanDbContext db, IModuleConstructions constructions) : IModuleTableRows
{
    private const string NumberKey = "Номер", Site = "Стройка", Source = "Основание", Status = "Статус";
    private const string Version = "Версия", SentOn = "ДатаОтправки", Total = "Итого";
    private const string Incomplete = "НеполныйИтог";
    private const string SiteKey = "КлючСтройки";

    private static readonly IReadOnlyDictionary<OfferStatus, string> Statuses = new Dictionary<OfferStatus, string>
    {
        [OfferStatus.Draft] = "Черновик",
        [OfferStatus.Sent] = "Отправлено",
        [OfferStatus.Accepted] = "Принято",
        [OfferStatus.Rejected] = "Отклонено",
        [OfferStatus.NoAnswer] = "Без ответа",
    };

    internal static readonly ModuleTable Declaration = new(
        "offers", "Коммерческие предложения", "КП", PlanModule.OfferRead, ModuleTableIsolation.None,
        ModuleTableBoundary.Only("Коммерческие предложения всех строек, действующие версии"),
        [
            new(NumberKey, "Номер", Text),
            new(Site, "Стройка", Text, Sites: true),
            new(Source, "Основание", Text),
            new(Status, "Статус", Choice, Options: [.. Statuses.Values]),
            new(Version, "Действующая версия", ModuleTableColumnKind.Number),
            new(SentOn, "Дата отправки", ModuleTableColumnKind.Date),
            new(Total, "Итого", ModuleTableColumnKind.Number),
            new(Incomplete, "Неполный итог", ModuleTableColumnKind.Boolean),
            new(SiteKey, "Ключ стройки", Text, OnDemand: true),
        ],
        typeof(OfferTable),
        Views: [new("registry", "Реестр КП", [NumberKey, Site, Source, Status, Version, SentOn, Total, Incomplete],
            Filters: [NumberKey, Site, Status, SentOn])],
        MissingRowIsNotFound: true);

    public async Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct)
    {
        var sites = (await constructions.ListAsync(ct)).ToDictionary(s => s.Id, s => s.Name);
        var rows = from offer in db.Offers.AsNoTracking()
                   join revision in db.EstimateRevisions on offer.RevisionId equals (Guid?)revision.Id into revisions
                   from revision in revisions.DefaultIfEmpty()
                   join estimate in db.Estimates on revision.EstimateId equals estimate.Id into estimates
                   from estimate in estimates.DefaultIfEmpty()
                   select new Cells
                   {
                       Id = offer.Id,
                       Number = offer.Number,
                       ConstructionId = offer.ConstructionId,
                       Source = offer.RevisionId == null ? "По прайс-листу" : estimate.Name + " · ред. " + revision.Number,
                       Status = offer.Status,
                       Version = offer.CurrentVersion,
                       SentOn = offer.SentOn,
                       Total = offer.Total,
                       Incomplete = offer.RevisionId == null && offer.Lines.Any(line => line.PriceListPrice == null),
                       Date = offer.Date,
                       Year = offer.Year,
                       Sequence = offer.Sequence,
                   };
        if (query.Row is { } key)
            rows = Guid.TryParseExact(key, "D", out var id) && id.ToString() == key
                ? rows.Where(r => r.Id == id)
                : rows.Where(_ => false);

        var sql = TableSql<Cells>.Describe(Declaration, columns => columns
            .Text(NumberKey, r => r.Number)
            .Lookup(Site, r => (Guid?)r.ConstructionId, sites)
            .Text(Source, r => r.Source)
            .Choice(Status, r => (OfferStatus?)r.Status, Statuses)
            .Number(Version, r => r.Version)
            .Date(SentOn, r => r.SentOn)
            .Number(Total, r => r.Total)
            .Flag(Incomplete, r => r.Incomplete)
            .Text(SiteKey, r => r.ConstructionId.ToString()));
        var filtered = sql.Where(rows, query.Filter);
        var count = await filtered.CountAsync(ct);
        var totals = await sql.TotalsAsync(filtered, query.Totals, ct);
        var sorted = (sql.OrderBy(filtered, query.Sort) ?? filtered.OrderByDescending(r => r.Date))
            .ThenByDescending(r => r.Year).ThenByDescending(r => r.Sequence).ThenBy(r => r.Id);
        var page = sorted.Skip(query.Offset);
        if (query.Limit is { } limit) page = page.Take(limit);
        var read = await page.ToListAsync(ct);
        return new([.. read.Select(r => Values(r, sites))], count, totals, Keys: [.. read.Select(r => r.Id.ToString())]);
    }

    private static IReadOnlyDictionary<string, object?> Values(Cells row, IReadOnlyDictionary<Guid, string> sites) =>
        new Dictionary<string, object?>
        {
            [NumberKey] = row.Number,
            [Site] = sites.GetValueOrDefault(row.ConstructionId),
            [Source] = row.Source,
            [Status] = Statuses[row.Status],
            [Version] = (decimal)row.Version,
            [SentOn] = row.SentOn,
            [Total] = row.Total,
            [Incomplete] = row.Incomplete,
            [SiteKey] = row.ConstructionId.ToString(),
        };

    private sealed class Cells
    {
        public Guid Id { get; init; }
        public string Number { get; init; } = "";
        public Guid ConstructionId { get; init; }
        public string Source { get; init; } = "";
        public OfferStatus Status { get; init; }
        public int Version { get; init; }
        public DateOnly? SentOn { get; init; }
        public decimal Total { get; init; }
        public bool Incomplete { get; init; }
        public DateOnly Date { get; init; }
        public int Year { get; init; }
        public int Sequence { get; init; }
    }
}
