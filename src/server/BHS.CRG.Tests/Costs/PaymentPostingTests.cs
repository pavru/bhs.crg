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
    /// <summary>Допуск расхождения сумм — умолчание настройки модуля (ТЗ COST-13): рубль на счёт.</summary>
    private const decimal Rouble = 1.00m;

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

        var plan = PaymentPosting.Plan(D(9, 15), 100_000m, [First, Second], parts, PostedBefore.None, Closed, Rouble);

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

    /// <summary>
    /// Деньги счёта по частям — зерно, из которого сложены клетка реестра, её расшифровка и затраты
    /// (G5, issue #1098). Части и остаток вместе дают сумму к оплате; нулевого остатка нет вовсе, а у
    /// счёта без суммы к оплате остатку взяться не из чего.
    /// </summary>
    [Fact]
    public void Деньги_по_частям_складываются_в_сумму_к_оплате_а_нулевого_остатка_нет()
    {
        InvoiceAllocation[] parts =
        [
            Part(First, 1, AllocationTarget.Site(SiteA), quantity: 100),
            Part(Second, 1, AllocationTarget.Site(SiteB), amount: 60_000m),
        ];
        IReadOnlyList<PostedMoney> Money(decimal? total) =>
            PaymentPosting.Money(PaymentPosting.Balance([First, Second], parts, total, Rouble), total, parts, D(9, 15));

        var exact = Money(100_000m);
        Assert.Equal([40_000m, 60_000m], exact.Select(m => m.Amount!.Value));
        Assert.All(exact, m => Assert.NotNull(m.Part));

        var over = Money(100_500m);
        Assert.Equal(new PostedMoney(null, 500m, D(9, 15)), over[^1]);
        Assert.Equal(100_500m, over.Sum(m => m.Amount));

        Assert.All(Money(null), m => Assert.NotNull(m.Part));

        // Месяцы — из тех же строк: неоплаченный счёт (дней нет) месяцев не называет, а остаток с днём — называет.
        Assert.Equal([new PostedMonth(D(9, 1), 500m)], PaymentPosting.Months(over));
    }

    /// <summary>
    /// Строки больше суммы к оплате в пределах допуска — остаток отрицательный. Это поправка к деньгам
    /// долей, и своего месяца у неё нет (ревью PR #1192): она ложится в день самой поздней доли с
    /// деньгами, а не в день платежа по контуру компании.
    /// </summary>
    [Fact]
    public void Отрицательная_поправка_идёт_днём_самой_поздней_доли_и_своего_месяца_не_называет()
    {
        InvoiceAllocation[] parts =
        [
            Part(First, 1, AllocationTarget.Site(SiteA), quantity: 100),
            Part(Second, 1, AllocationTarget.Site(SiteB), amount: 59_999.80m),
        ];
        const decimal total = 99_999.50m;

        var plan = PaymentPosting.Plan(D(9, 15), total, [First, Second], parts, PostedBefore.None, Closed, Rouble);

        // Стройка А закрыта по 30.09 — её доля в октябре; поправка идёт с ней, а не остаётся в сентябре.
        Assert.Equal(new PostedRemainder(-0.30m, D(10, 1), true), plan.Remainder);

        PaymentPosting.Apply(Invoice(total), parts, plan);
        var months = PaymentPosting.Months(
            PaymentPosting.Balance([First, Second], parts, total, Rouble), total, parts, plan.Remainder!.AccountingOn);
        Assert.Equal(
            [new PostedMonth(D(9, 1), 59_999.80m), new PostedMonth(D(10, 1), 39_999.70m)],
            months);
        Assert.Equal(total, months.Sum(m => m.Amount));

        // Под отбором по объекту — только доли на него, без поправки: она не лежит ни на одном объекте.
        Assert.Equal(
            [new PostedMonth(D(10, 1), 40_000m)],
            PaymentPosting.Months(PaymentPosting.Balance([First, Second], parts, total, Rouble), total, parts,
                plan.Remainder.AccountingOn, (p, _) => p?.ConstructionId == SiteA));
    }

    [Fact]
    public void Платёж_в_закрытый_месяц_компании_переносит_всё_но_каждого_в_свой_первый_открытый_день()
    {
        InvoiceAllocation[] parts =
        [
            Part(First, 1, AllocationTarget.Site(SiteA), quantity: 100),
            Part(Second, 1, AllocationTarget.Site(SiteB), amount: 60_000),
        ];

        var plan = PaymentPosting.Plan(D(8, 20), 100_000m, [First, Second], parts, PostedBefore.None, Closed, Rouble);

        Assert.Equal(D(10, 1), plan.Shares.Single(s => s.Target.ConstructionId == SiteA).AccountingOn);
        Assert.Equal(D(9, 1), plan.Shares.Single(s => s.Target.ConstructionId == SiteB).AccountingOn);
        Assert.Null(plan.Remainder);
    }

    /// <summary>
    /// Правка разноски оплаченного счёта: та же строка и та же цель дату сохраняют, даже если период с
    /// тех пор закрыли и «по правилу» дата была бы другой; другая стройка получает дату по правилу.
    /// </summary>
    [Fact]
    public void При_правке_дата_сохраняется_по_значению_строки_и_цели()
    {
        var kept = Kept(100_000m, (Part(First, 1, AllocationTarget.Site(SiteB), quantity: 100), D(9, 15)))
            with { Remainder = D(9, 15) };

        // Часть та же по идентификатору, но цель у неё уже другая — построчная разноска правит на месте.
        InvoiceAllocation[] parts =
        [
            Part(First, 1, AllocationTarget.Site(SiteA), quantity: 50),
            Part(First, 2, AllocationTarget.Site(SiteB), quantity: 50),
        ];

        var later = new PeriodBoundaries(D(9, 30), new Dictionary<Guid, DateOnly>());
        var plan = PaymentPosting.Plan(D(9, 15), 100_000m, [First, Second], parts, kept, later, Rouble);

        Assert.Equal(D(9, 15), plan.Shares.Single(s => s.Target.ConstructionId == SiteB).AccountingOn);
        Assert.Equal(D(10, 1), plan.Shares.Single(s => s.Target.ConstructionId == SiteA).AccountingOn);
        Assert.Equal(D(9, 15), plan.Remainder!.AccountingOn);
    }

    /// <summary>
    /// Смена раздела той же стройки дня не двигает (issue #1341): контур тот же, и деньги не уходят из
    /// закрытого месяца стройки в следующий. Разделили раздел на два — у обоих прежний день.
    /// </summary>
    [Fact]
    public void Перенос_между_разделами_одной_стройки_сохраняет_день()
    {
        Guid r1 = Guid.NewGuid(), r2 = Guid.NewGuid();
        var kept = Kept(40_000m, (Part(First, 1, AllocationTarget.Site(SiteA, r1), quantity: 100), D(9, 15)));

        // Стройка А закрыта по 30.09 — «по правилу» новая доля легла бы 01.10.
        InvoiceAllocation[] moved = [Part(First, 1, AllocationTarget.Site(SiteA, r2), quantity: 100)];
        Assert.Equal(D(9, 15), Day(moved, kept, AllocationTarget.Site(SiteA, r2)));

        // Раздел без раздела — тоже та же стройка.
        InvoiceAllocation[] whole = [Part(First, 1, AllocationTarget.Site(SiteA), quantity: 100)];
        Assert.Equal(D(9, 15), Day(whole, kept, AllocationTarget.Site(SiteA)));

        InvoiceAllocation[] split =
        [
            Part(First, 1, AllocationTarget.Site(SiteA, r1), quantity: 60),
            Part(First, 2, AllocationTarget.Site(SiteA, r2), quantity: 40),
        ];
        Assert.Equal(D(9, 15), Day(split, kept, AllocationTarget.Site(SiteA, r2)));
    }

    /// <summary>
    /// День новая доля берёт только у прежней доли той же строки и того же контура, ОТДАВШЕЙ деньги (ревью
    /// PR #1342): не у соседа, который остался как был, и не у доли другой строки. Отдавших несколько —
    /// позднейший день. Стройка А закрыта по 30.09: «по правилу» новая доля на А легла бы 01.10.
    /// </summary>
    [Fact]
    public void Новая_доля_берёт_день_только_у_доли_отдавшей_деньги()
    {
        Guid r1 = Guid.NewGuid(), r2 = Guid.NewGuid(), r3 = Guid.NewGuid();

        // «р1» поделили на «р1» и «р3», «р2» не трогали: у «р3» день «р1», а не позднейший «р2».
        var two = Kept(40_000m,
            (Part(First, 1, AllocationTarget.Site(SiteA, r1), quantity: 50), D(9, 15)),
            (Part(First, 2, AllocationTarget.Site(SiteA, r2), quantity: 50), D(10, 2)));
        InvoiceAllocation[] carved =
        [
            Part(First, 1, AllocationTarget.Site(SiteA, r1), quantity: 25),
            Part(First, 2, AllocationTarget.Site(SiteA, r2), quantity: 50),
            Part(First, 3, AllocationTarget.Site(SiteA, r3), quantity: 25),
        ];
        Assert.Equal(D(9, 15), Day(carved, two, AllocationTarget.Site(SiteA, r3)));

        // Оба раздела свели в «р3» — отдали оба, день позднейший.
        Assert.Equal(D(10, 2), Day([Part(First, 1, AllocationTarget.Site(SiteA, r3), quantity: 100)], two,
            AllocationTarget.Site(SiteA, r3)));

        // Доля пришла в новый раздел А со стройки Б: «р1» осталась как была и дня не отдаёт — деньги Б не
        // ложатся в прошлый месяц А, день по правилу.
        var mixed = Kept(40_000m,
            (Part(First, 1, AllocationTarget.Site(SiteA, r1), quantity: 50), D(9, 15)),
            (Part(First, 2, AllocationTarget.Site(SiteB), quantity: 50), D(9, 15)));
        InvoiceAllocation[] arrived =
        [
            Part(First, 1, AllocationTarget.Site(SiteA, r1), quantity: 50),
            Part(First, 2, AllocationTarget.Site(SiteA, r2), quantity: 50),
        ];
        Assert.Equal(D(10, 1), Day(arrived, mixed, AllocationTarget.Site(SiteA, r2)));

        // Чужая строка дня не отдаёт: стройку Б первая строка получила впервые — день по правилу (Б открыта).
        var lines = Kept(100_000m,
            (Part(First, 1, AllocationTarget.Site(SiteA), quantity: 100), D(10, 1)),
            (Part(Second, 1, AllocationTarget.Site(SiteB, r1), amount: 60_000), D(9, 10)));
        InvoiceAllocation[] across =
        [
            Part(First, 1, AllocationTarget.Site(SiteB), quantity: 100),
            Part(Second, 1, AllocationTarget.Site(SiteB, r1), amount: 60_000),
        ];
        Assert.Equal(D(9, 15), Day(across, lines, AllocationTarget.Site(SiteB), total: 100_000m));
    }

    /// <summary>
    /// Статьи вне строек — контур компании: «Офис» → «Склад» день сохраняет; стройка → статья — новый день.
    /// </summary>
    [Fact]
    public void Статья_на_статью_сохраняет_день_а_стройка_на_статью_нет()
    {
        var office = Guid.NewGuid();
        var article = Kept(60_000m, (Part(Second, 1, AllocationTarget.Article(office), amount: 60_000), D(9, 12)));
        Assert.Equal(D(9, 12), Day([Part(Second, 1, AllocationTarget.Article(Stock), amount: 60_000)], article,
            AllocationTarget.Article(Stock), total: 60_000m));

        var site = Kept(60_000m, (Part(Second, 1, AllocationTarget.Site(SiteB), amount: 60_000), D(9, 12)));
        Assert.Equal(D(9, 15), Day([Part(Second, 1, AllocationTarget.Article(Stock), amount: 60_000)], site,
            AllocationTarget.Article(Stock), total: 60_000m));
    }

    /// <summary>Снимок до правки — как его снимает запись: доли с записанными днями и их деньги.</summary>
    private static PostedBefore Kept(decimal total, params (InvoiceAllocation Part, DateOnly Day)[] posted)
    {
        foreach (var (part, day) in posted) part.Post(day);
        InvoiceAllocation[] parts = [.. posted.Select(p => p.Part)];
        return PaymentPosting.Before(Invoice(total), parts, PaymentPosting.Balance([First, Second], parts, total, Rouble));
    }

    /// <summary>День доли на цель после правки — оплата 15.09, границы <see cref="Closed" />.</summary>
    private static DateOnly Day(InvoiceAllocation[] parts, PostedBefore kept, AllocationTarget target,
        decimal total = 40_000m) =>
        PaymentPosting.Plan(D(9, 15), total, [First, Second], parts, kept, Closed, Rouble)
            .Shares.Single(s => s.Target == target).AccountingOn;

    [Fact]
    public void Отказ_оплаты_без_суммы_и_при_расхождении_сверх_допуска()
    {
        Assert.Contains("не указана сумма", PaymentPosting.Refusal(Invoice(null), PaymentPosting.Balance([First], [], null, Rouble)));

        var off = PaymentPosting.Refusal(Invoice(40_500m), PaymentPosting.Balance([First], [], 40_500m, Rouble));
        Assert.Contains("расходится", off);

        // В пределах допуска — не отказ: это округление, оно уходит в последнюю долю.
        Assert.Null(PaymentPosting.Refusal(Invoice(40_000.50m), PaymentPosting.Balance([First], [], 40_000.50m, Rouble)));

        // Счёт без строк сверять не с чем.
        Assert.Null(PaymentPosting.Refusal(Invoice(100m), PaymentPosting.Balance([], [], 100m, Rouble)));
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
            PaymentPosting.Plan(D(9, 15), 100_000m, [First, Second], parts, PostedBefore.None, PeriodBoundaries.None, Rouble));

        Assert.Null(ClosedPeriodGuard.LockOf(invoice, parts, PeriodBoundaries.None));
        // Стройка А закрыта по 30.09 — доля от 15.09 закрыта, и заперт счёт целиком.
        Assert.Equal(new PeriodLock(SiteA, D(9, 30)), ClosedPeriodGuard.LockOf(invoice, parts, Closed));
        // Закрытие компании называется раньше закрытия стройки.
        Assert.Equal(new PeriodLock(null, D(9, 20)),
            ClosedPeriodGuard.LockOf(invoice, parts, new PeriodBoundaries(D(9, 20), Closed.Constructions)));
    }

    /// <summary>
    /// Допуск понизили после оплаты (ревью PR #1249). При оплате расхождение было в допуске и ушло в
    /// последнюю часть — остатка не было, и даты ему не записали. Теперь расхождение — остаток, и из
    /// учётных месяцев он выпасть не должен: сумма счёта по периодам обязана остаться суммой к оплате.
    /// </summary>
    [Fact]
    public void Остаток_появившийся_после_понижения_допуска_остаётся_в_месяце_последней_доли()
    {
        const decimal total = 100_000.80m;
        InvoiceAllocation[] parts =
        [
            Part(First, 1, AllocationTarget.Site(SiteB), quantity: 100),
            Part(Second, 1, AllocationTarget.Site(SiteB), amount: 60_000),
        ];
        var plan = PaymentPosting.Plan(D(9, 15), total, [First, Second], parts, PostedBefore.None, PeriodBoundaries.None, Rouble);
        Assert.Null(plan.Remainder);
        foreach (var part in parts) part.Post(D(9, 15));

        var lowered = PaymentPosting.Balance([First, Second], parts, total, tolerance: 0.50m);
        var money = PaymentPosting.Money(lowered, total, parts, remainderOn: null);

        Assert.Equal((0.80m, D(9, 15)), (money.Single(m => m.Part is null).Amount, money.Single(m => m.Part is null).AccountingOn));
        Assert.Equal(total, PaymentPosting.Months(money).Sum(m => m.Amount));

        // У неоплаченного счёта дат нет ни у долей, ни у остатка — период ему не называется.
        foreach (var part in parts) part.Post(null);
        var unpaid = PaymentPosting.Money(lowered, total, parts, remainderOn: null);
        Assert.All(unpaid, m => Assert.Null(m.AccountingOn));
    }

    /// <summary>
    /// Тот же остаток при правке разноски (issue #1345): день у него — тот, с которым его читают, а не
    /// «появился из нуля». Иначе правка, не тронувшая денег остатка, уносит его в другой месяц:
    /// положительный — в день платежа по контуру компании, отрицательный — в день самой поздней доли
    /// ПОСЛЕ правки.
    ///
    /// <para>Состояние — то, что откроет исправление счёта (E2, #1100): закрыто сейчас ничего, дни долей
    /// сдвинуты закрытиями, которые потом отменили. Сегодня до Plan такой счёт не доходит: при новом
    /// допуске он не сведён, и запись отказывает раньше.</para>
    /// </summary>
    [Theory]
    [InlineData("100000.80")]
    [InlineData("99999.20")]
    public void Остаток_без_записанного_дня_при_правке_остаётся_в_месяце_чтения(string whole)
    {
        var total = decimal.Parse(whole, System.Globalization.CultureInfo.InvariantCulture);
        const decimal lowered = 0.50m;
        var first = Part(First, 1, AllocationTarget.Site(SiteB), quantity: 100);
        var second = Part(Second, 1, AllocationTarget.Site(SiteB), amount: 60_000);
        first.Post(D(10, 1));
        second.Post(D(9, 20));
        InvoiceAllocation[] before = [first, second];
        var kept = PaymentPosting.Before(Invoice(total), before,
            PaymentPosting.Balance([First, Second], before, total, lowered));

        // Первую строку перенесли на статью — её день по правилу оплаты, деньги остатка не тронуты.
        var moved = Part(First, 1, AllocationTarget.Article(Stock), quantity: 100);
        var plan = PaymentPosting.Plan(D(9, 15), total, [First, Second], [moved, second], kept,
            PeriodBoundaries.None, lowered);

        Assert.Equal(D(9, 15), plan.Shares.Single(s => s.Target == moved.Target).AccountingOn);
        Assert.Equal(total - 100_000m, plan.Remainder!.Amount);
        Assert.Equal(D(10, 1), plan.Remainder.AccountingOn);
        Assert.Equal(D(10, 1), kept.Remainder);
    }

    /// <summary>
    /// «Оплачен и отклонён» не складывается ни с какой стороны (ревью PR #1290): правило стоит в самой
    /// сущности, а не в адресах — следующему, кто позовёт метод, повторять проверку не придётся. Деньги
    /// такого счёта молча выпали бы из затрат: отчёты считают «оплачен и не отклонён».
    /// </summary>
    /// <summary>
    /// Нет остатка — нет и его дня в снимке, даже записанного (E2, spec v3.3 разд. 3): день у нуля
    /// рублей — «фантом». Возьми снимок сырую колонку, остаток, появившийся снова, вернулся бы старым
    /// днём, а не днём по правилу оплаты.
    /// </summary>
    [Fact]
    public void Снимок_не_несёт_дня_у_нулевого_остатка()
    {
        var invoice = Invoice(100_000m);
        invoice.Pay(D(9, 15), null, null);
        invoice.PostRemainder(D(9, 15));
        var first = Part(First, 1, AllocationTarget.Site(SiteB), quantity: 100);
        var second = Part(Second, 1, AllocationTarget.Site(SiteB), amount: 60_000);
        first.Post(D(9, 15));
        second.Post(D(9, 15));
        InvoiceAllocation[] parts = [first, second];

        var kept = PaymentPosting.Before(invoice, parts, PaymentPosting.Balance([First, Second], parts, 100_000m, Rouble));

        Assert.Null(kept.Remainder);
    }

    [Fact]
    public void Оплаченный_не_отклоняется_а_отклонённый_не_оплачивается()
    {
        var paid = Invoice(100m);
        paid.Pay(D(9, 15), null, null);
        Assert.Contains("отмените оплату", Assert.Throws<ConflictException>(paid.Reject).Message);
        Assert.Equal(InvoiceState.Draft, paid.State);

        var rejected = Invoice(100m);
        rejected.Reject();
        Assert.Contains("верните его в черновик",
            Assert.Throws<ConflictException>(() => rejected.Pay(D(9, 15), null, null)).Message);
        Assert.Equal(InvoicePaymentState.Unpaid, rejected.Payment);

        // Обе дороги назад открыты: отмена оплаты и возврат в черновик.
        paid.Unpay();
        paid.Reject();
        rejected.ReturnToDraft();
        rejected.Pay(D(9, 15), null, null);
    }

    private static Invoice Invoice(decimal? total)
    {
        var invoice = BHS.CRG.Modules.Costs.Data.Invoice.Create(Guid.NewGuid(), null);
        invoice.ApplyRequisites(new InvoiceColumns(null, null, null, null, null, total, null, null, null, null),
            JsonDocument.Parse("{}"), dueDateByHand: false, new InvoiceDueRule(true));
        return invoice;
    }
}
