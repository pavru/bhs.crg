using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Учётные даты оплаты — чистая арифметика (задача C5, issue #1082, ТЗ COST-16): чей период у доли,
/// что переносится, что сохраняется при правке и чем счёт запирается.
/// </summary>
public class PaymentPostingTests
{
    private static readonly Guid SiteA = Guid.NewGuid();
    private static readonly Guid SiteB = Guid.NewGuid();
    private static readonly Guid Stock = Guid.NewGuid();
    private static readonly Guid InvoiceId = Guid.NewGuid();

    private static readonly AllocationLine First = new(Guid.NewGuid(), 1, 100m, 40_000m);
    private static readonly AllocationLine Second = new(Guid.NewGuid(), 2, null, 60_000m);

    private static DateOnly D(int month, int day) => new(2026, month, day);

    /// <summary>Компания закрыта по 31.08, стройка А — своим закрытием по 30.09.</summary>
    private static readonly PeriodBoundaries Closed =
        new(D(8, 31), new Dictionary<Guid, DateOnly> { [SiteA] = D(9, 30) });

    private static InvoiceAllocation Part(AllocationLine line, int ordinal, AllocationTarget target,
        decimal? quantity = null, decimal? amount = null)
    {
        var part = InvoiceAllocation.Create(InvoiceId, line.Id);
        part.Apply(ordinal, new AllocationValues(target, quantity, amount));
        return part;
    }

    [Fact]
    public void Переносится_только_доля_закрытой_стройки_а_статья_и_остаток_идут_по_компании()
    {
        InvoiceAllocation[] parts =
        [
            Part(First, 1, AllocationTarget.Site(SiteA), quantity: 100),
            Part(Second, 1, AllocationTarget.Site(SiteB), amount: 30_000),
            Part(Second, 2, AllocationTarget.Article(Stock), amount: 10_000),
        ];

        var plan = PaymentPosting.Plan(D(9, 15), 100_000m, [First, Second], parts, PostedBefore.None, Closed);

        var a = plan.Shares.Single(s => s.Target.ConstructionId == SiteA);
        Assert.Equal((D(10, 1), true, 40_000m), (a.AccountingOn, a.Moved, a.Amount));

        var b = plan.Shares.Single(s => s.Target.ConstructionId == SiteB);
        Assert.Equal((D(9, 15), false), (b.AccountingOn, b.Moved));

        // Статья вне строек и неразнесённые 20 000 — контур компании: 15.09 для неё открыто.
        Assert.Equal(D(9, 15), plan.Shares.Single(s => s.Target.ArticleId == Stock).AccountingOn);
        Assert.Equal(new PostedRemainder(20_000m, D(9, 15), false), plan.Remainder);

        // Сумма расклада — сумма счёта: остаток входит слагаемым.
        Assert.Equal(100_000m, plan.Shares.Sum(s => s.Amount ?? 0) + plan.Remainder!.Amount);
    }

    [Fact]
    public void Платёж_в_закрытый_месяц_компании_переносит_всё_но_каждого_в_свой_первый_открытый_день()
    {
        InvoiceAllocation[] parts =
        [
            Part(First, 1, AllocationTarget.Site(SiteA), quantity: 100),
            Part(Second, 1, AllocationTarget.Site(SiteB), amount: 60_000),
        ];

        var plan = PaymentPosting.Plan(D(8, 20), 100_000m, [First, Second], parts, PostedBefore.None, Closed);

        Assert.Equal(D(10, 1), plan.Shares.Single(s => s.Target.ConstructionId == SiteA).AccountingOn);
        Assert.Equal(D(9, 1), plan.Shares.Single(s => s.Target.ConstructionId == SiteB).AccountingOn);
        Assert.Null(plan.Remainder);
    }

    /// <summary>
    /// Правка разноски оплаченного счёта: та же строка и та же цель дату сохраняют, даже если период с
    /// тех пор закрыли и «по правилу» дата была бы другой; новая цель получает дату по правилу.
    /// </summary>
    [Fact]
    public void При_правке_дата_сохраняется_по_значению_строки_и_цели()
    {
        var kept = new PostedBefore(
            new Dictionary<(Guid?, AllocationTarget), DateOnly> { [(First.Id, AllocationTarget.Site(SiteB))] = D(9, 15) },
            Remainder: D(9, 15));

        // Часть та же по идентификатору, но цель у неё уже другая — построчная разноска правит на месте.
        InvoiceAllocation[] parts =
        [
            Part(First, 1, AllocationTarget.Site(SiteA), quantity: 50),
            Part(First, 2, AllocationTarget.Site(SiteB), quantity: 50),
        ];

        var later = new PeriodBoundaries(D(9, 30), new Dictionary<Guid, DateOnly>());
        var plan = PaymentPosting.Plan(D(9, 15), 100_000m, [First, Second], parts, kept, later);

        Assert.Equal(D(9, 15), plan.Shares.Single(s => s.Target.ConstructionId == SiteB).AccountingOn);
        Assert.Equal(D(10, 1), plan.Shares.Single(s => s.Target.ConstructionId == SiteA).AccountingOn);
        Assert.Equal(D(9, 15), plan.Remainder!.AccountingOn);
    }

    [Fact]
    public void Отказ_оплаты_без_суммы_и_при_расхождении_сверх_допуска()
    {
        Assert.Contains("не указана сумма", PaymentPosting.Refusal(Invoice(null), PaymentPosting.Balance([First], [], null)));

        var off = PaymentPosting.Refusal(Invoice(40_500m), PaymentPosting.Balance([First], [], 40_500m));
        Assert.Contains("расходится", off);

        // В пределах допуска — не отказ: это округление, оно уходит в последнюю долю.
        Assert.Null(PaymentPosting.Refusal(Invoice(40_000.50m), PaymentPosting.Balance([First], [], 40_000.50m)));

        // Счёт без строк сверять не с чем.
        Assert.Null(PaymentPosting.Refusal(Invoice(100m), PaymentPosting.Balance([], [], 100m)));
    }

    [Fact]
    public void Запирает_любая_закрытая_учётная_дата_а_неоплаченный_счёт_не_заперт_ничем()
    {
        var invoice = Invoice(100_000m);
        InvoiceAllocation[] parts =
        [
            Part(First, 1, AllocationTarget.Site(SiteA), quantity: 100),
            Part(Second, 1, AllocationTarget.Site(SiteB), amount: 60_000),
        ];
        Assert.Null(ClosedPeriodGuard.LockOf(invoice, parts, Closed));

        invoice.Pay(D(9, 15), null, null);
        PaymentPosting.Apply(invoice, parts,
            PaymentPosting.Plan(D(9, 15), 100_000m, [First, Second], parts, PostedBefore.None, PeriodBoundaries.None));

        Assert.Null(ClosedPeriodGuard.LockOf(invoice, parts, PeriodBoundaries.None));
        // Стройка А закрыта по 30.09 — доля от 15.09 закрыта, и заперт счёт целиком.
        Assert.Equal(new PeriodLock(SiteA, D(9, 30)), ClosedPeriodGuard.LockOf(invoice, parts, Closed));
        // Закрытие компании называется раньше закрытия стройки.
        Assert.Equal(new PeriodLock(null, D(9, 20)),
            ClosedPeriodGuard.LockOf(invoice, parts, new PeriodBoundaries(D(9, 20), Closed.Constructions)));
    }

    private static Invoice Invoice(decimal? total)
    {
        var invoice = BHS.CRG.Modules.Costs.Data.Invoice.Create(Guid.NewGuid(), null);
        invoice.Apply(new InvoiceColumns(null, null, null, null, null, total, null, null, null, null),
            JsonDocument.Parse("{}"), dueDateByHand: false);
        return invoice;
    }
}
