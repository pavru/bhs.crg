using System.Data;
using System.Text.Json.Serialization;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

public sealed record EstimateCostCell(
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Amount = null,
    bool BasisUnknown = false);

public sealed record EstimateVsCostsRow(
    Guid? NomenclatureId, string Name, decimal Allocated, decimal Paid,
    bool AllocatedVatUnknown, bool PaidVatUnknown, bool AllocatedIncomplete, bool PaidIncomplete, EstimateCostCell Estimate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Difference);

public sealed record EstimateVsCostsView(
    Guid ConstructionId, Guid? SectionId, string Construction, string? Section,
    string EstimateState, string? Permission, string? Message,
    IReadOnlyList<EstimateVsCostsRow> Rows, decimal Allocated, decimal Paid, int Undecided,
    string? EstimateCurrency, string FactCurrency, bool CurrencyMismatch);

internal sealed class EstimateVsCostsReport(
    CostsDbContext db, IModuleSettings settings, IModuleClock clock, IModuleContracts contracts,
    IModuleReaders readers, IModuleConstructions constructions, IModuleCatalog catalog, IModuleAccountingCurrency currency)
{
    private const string EstimatePermission = "plan.estimate.cost";

    public async Task<EstimateVsCostsView> ReadAsync(
        Guid site, Guid? section, CancellationToken ct, string? search = null, bool onlyExcess = false)
    {
        var construction = (await constructions.ListAsync(ct)).FirstOrDefault(item => item.Id == site)
            ?? throw new InvalidRequestException("Выбранной стройки нет.");
        if (section is not null && construction.Sections.All(item => item.Id != section))
            throw new InvalidRequestException("Выбранный раздел не принадлежит этой стройке.");

        var (state, permission, message, planned, undecided) = await PlanAsync(site, section, ct);
        var factCurrency = await currency.CodeAsync(ct);
        var currencyMismatch = state == "shown" && factCurrency != "RUB";
        if (currencyMismatch)
            message = $"Смета — RUB, факт — {factCurrency}. Для расчёта отклонения требуются суммы в одной валюте.";
        var tolerance = await settings.GetAsync(CostsSettings.AllocationTolerance, ct);
        var today = await clock.TodayAsync(ct);
        var first = InvoicePeriods.First;
        var through = InvoicePeriods.Last(today).AddMonths(1).AddDays(-1);
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var (paid, paidLines) = await SiteCostsEndpoints.InvoicesAsync(
            db, SiteCostsEndpoints.Paid(db, first, through, site), tolerance, ct);
        var (unpaid, unpaidLines) = await SiteCostsEndpoints.InvoicesAsync(
            db, SiteCostsEndpoints.Unpaid(db, site), tolerance, ct);
        var owners = paid.Select(invoice => invoice.Id).Concat(unpaid.Select(invoice => invoice.Id)).ToArray();
        var nomenclature = await db.InvoiceLines.AsNoTracking().Where(line => owners.Contains(line.InvoiceId))
            .Select(line => new { line.Id, line.NomenclatureId }).ToDictionaryAsync(line => line.Id, line => line.NomenclatureId, ct);

        var spent = Money(paid, paidLines, nomenclature, site, section, first, through, paid: true);
        var waiting = Money(unpaid, unpaidLines, nomenclature, site, section, first, through, paid: false);
        var estimates = planned.SelectMany(item => item.Materials).ToLookup(material => material.NomenclatureId);
        var ids = spent.Keys.Concat(waiting.Keys).Concat(estimates.Select(group => group.Key)).Distinct().ToArray();
        var names = (await catalog.RefsAsync(CostsDirectories.Nomenclature, ids, ct) ?? [])
            .ToDictionary(item => item.Id, item => item.DisplayName);
        var rows = ids.Select(id =>
        {
            var paidMoney = spent.GetValueOrDefault(id);
            var unpaidMoney = waiting.GetValueOrDefault(id);
            var sources = estimates[id].ToArray();
            var cell = Cell(state, sources);
            var allocated = paidMoney.Amount + unpaidMoney.Amount;
            var unknown = paidMoney.Unknown || unpaidMoney.Unknown;
            var incomplete = paidMoney.Incomplete || unpaidMoney.Incomplete;
            var name = names.ContainsKey(id) ? names[id] ?? "Позиция без наименования" : "Позиция номенклатуры удалена";
            return new EstimateVsCostsRow(id == Guid.Empty ? null : id,
                id == Guid.Empty ? "Без позиции номенклатуры" : name,
                allocated, paidMoney.Amount, unknown, paidMoney.Unknown, incomplete, paidMoney.Incomplete, cell,
                cell.Amount is { } amount && !cell.BasisUnknown && !unknown && !incomplete && !currencyMismatch ? allocated - amount : null);
        }).Where(row => string.IsNullOrWhiteSpace(search) || row.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(row => !onlyExcess || row.Difference > 0)
            .OrderBy(row => row.NomenclatureId is null).ThenBy(row => row.Name, StringComparer.Create(
            System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), true)).ToArray();

        return new(site, section, construction.Name, construction.Sections.FirstOrDefault(item => item.Id == section)?.Name,
            state, permission, message, rows, rows.Sum(row => row.Allocated), rows.Sum(row => row.Paid), undecided,
            state == "shown" ? "RUB" : null, factCurrency, currencyMismatch);
    }

    private async Task<(string State, string? Permission, string? Message, IReadOnlyList<PlannedCost> Costs, int Undecided)>
        PlanAsync(Guid site, Guid? section, CancellationToken ct)
    {
        var connection = contracts.Get<IPlannedWorks>();
        if (connection is ModuleContractAnswer<IPlannedWorks>.NotConnected missing)
            return ("not-connected", null, missing.Text, [], 0);
        var reader = await readers.ForRequestAsync(ct);
        if (!reader.Allows(EstimatePermission))
            return ("hidden", EstimatePermission, $"Скрыто правами — нужно право «{EstimatePermission}».", [], 0);
        var provider = (ModuleContractAnswer<IPlannedWorks>.Connected)connection;
        var answer = await provider.Contract.ReadAsync(reader, new(site, section), ct);
        if (answer is ContractAnswer<PlannedWorksAnswer>.Shown shown)
        {
            if (shown.Value.Cost is ContractAnswer<IReadOnlyList<PlannedCost>>.Shown cost)
                return ("shown", null, null, cost.Value, shown.Value.Undecided);
            if (shown.Value.Cost is ContractAnswer<IReadOnlyList<PlannedCost>>.Hidden hidden)
                return ("hidden", hidden.Permission, hidden.Text, [], 0);
        }
        throw new ForbiddenException("Для чтения сметной стоимости нужен пользователь с правом чтения.");
    }

    private static EstimateCostCell Cell(string state, IReadOnlyList<PlannedMaterialCost> sources)
    {
        if (state != "shown") return new(state);
        if (sources.Count == 0 || sources.Any(source => source.Amount is null)) return new("not-in-estimate");
        return new("shown", sources.Sum(source => source.Amount!.Value),
            sources.Any(source => source.AmountBasis != ResourceTaxBasis.ExcludingVat));
    }

    private static Dictionary<Guid, (decimal Amount, bool Unknown, bool Incomplete)> Money(
        IReadOnlyList<CostInvoice> invoices, IReadOnlyDictionary<Guid, LineVat> lines,
        IReadOnlyDictionary<Guid, Guid?> nomenclature, Guid site, Guid? section,
        DateOnly first, DateOnly through, bool paid)
    {
        // Сохраняем принадлежность доли строке, затем используем ту же арифметику, что у CostViews.
        var groups = invoices.SelectMany(invoice => invoice.Money
                .Where(money => money.Part?.ConstructionId == site && (section is null || money.Part.SectionId == section))
                .Where(money => !paid || money.AccountingOn is { } day && day >= first && day <= through)
                .GroupBy(money => money.Part?.LineId is { } line ? nomenclature.GetValueOrDefault(line) ?? Guid.Empty : Guid.Empty)
                .Select(group => (group.Key, Invoice: invoice with { Money = group.ToArray() })))
            .GroupBy(item => item.Key);
        return groups.ToDictionary(group => group.Key, group =>
        {
            var own = group.Select(item => item.Invoice).ToArray();
            var amount = paid
                ? SiteCosts.Of(own, lines, first, through, site, withVat: false).Total.Amount
                : SiteCosts.Payable(own, lines, site, withVat: false).Figure.Amount;
            var unknown = own.Any(invoice => invoice.Money.Any(money => money.Amount is not null
                && (!paid || money.AccountingOn is { } day && day >= first && day <= through)
                && SiteCosts.Net(money, invoice, lines).VatUnknown));
            return (amount, unknown, own.Any(invoice => invoice.Money.Any(money => money.Amount is null)));
        });
    }
}
