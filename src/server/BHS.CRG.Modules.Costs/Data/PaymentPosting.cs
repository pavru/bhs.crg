using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Доля разноски оплаченного счёта с её учётной датой.</summary>
/// <param name="Amount">Деньги доли — посчитанные той же арифметикой, что и счёт; <c>null</c> — посчитать
/// нечем (в строке не вписана цена) либо доля денег не несёт (разноска ждёт пересчёта по строкам).</param>
/// <param name="Moved">Учётная дата не совпала с датой платежа: период стройки был закрыт.</param>
public sealed record PostedShare(
    Guid Id, Guid? LineId, AllocationTarget Target, decimal? Amount, DateOnly AccountingOn, bool Moved);

/// <summary>
/// Неразнесённый остаток оплаченного счёта — по контуру компании.
///
/// <para>⚠️ Бывает ОТРИЦАТЕЛЬНЫМ: строки больше суммы к оплате в пределах допуска (счёт на 1000,00 со
/// строками на 1000,80), и доли несут деньги строк. Это не ошибка, а поправка: с ней строки расклада
/// в сумме дают ровно сумму к оплате, а без неё затраты периодов разошлись бы с оплаченным на копейки.
/// Экран называет её поправкой, а не «не разнесено».</para>
/// </summary>
public sealed record PostedRemainder(decimal Amount, DateOnly AccountingOn, bool Moved);

/// <summary>Деньги счёта, вошедшие в затраты одного месяца.</summary>
/// <param name="Month">Первый день месяца.</param>
public sealed record PostedMonth(DateOnly Month, decimal Amount);

/// <summary>
/// Деньги счёта в одной его части — зерно, из которого складывается всё остальное: клетка «Сумма» под
/// отбором, учётные месяцы, расшифровка строки реестра и «Затраты по стройке».
/// </summary>
/// <param name="Part">Часть разноски; null — остаток счёта, не лёгший ни на один объект.</param>
/// <param name="Amount">Деньги части. null — посчитать нечем: в строке не вписана цена либо часть не
/// того вида, что строка. У остатка — всегда число, и не ноль.</param>
/// <param name="AccountingOn">Учётный день; null — счёт не оплачен.</param>
public sealed record PostedMoney(InvoiceAllocation? Part, decimal? Amount, DateOnly? AccountingOn);

/// <summary>Расклад оплаты: куда и каким днём легли деньги счёта.</summary>
public sealed record PaymentPlan(DateOnly PaidOn, IReadOnlyList<PostedShare> Shares, PostedRemainder? Remainder);

/// <summary>
/// Что было записано ДО правки — по значению, строка и цель: учётные даты долей и их деньги. По ним
/// правка разноски решает, какая доля дату сохраняет и от какой прежней доли её получает новая.
/// </summary>
/// <param name="Money">Деньги каждой доли до правки; <c>null</c> — посчитать было нечем (в строке не
/// вписана цена). Нужны, чтобы узнать, у какой прежней доли деньги ушли (см. <see cref="PaymentPosting.Plan" />).</param>
public sealed record PostedBefore(
    IReadOnlyDictionary<(Guid? LineId, AllocationTarget Target), DateOnly> Shares,
    IReadOnlyDictionary<(Guid? LineId, AllocationTarget Target), decimal?> Money,
    DateOnly? Remainder)
{
    /// <summary>Счёт не был оплачен: сохранять нечего, все даты — по правилу оплаты.</summary>
    public static PostedBefore None { get; } = new(
        new Dictionary<(Guid? LineId, AllocationTarget Target), DateOnly>(),
        new Dictionary<(Guid? LineId, AllocationTarget Target), decimal?>(), null);
}

/// <summary>
/// Учётные даты оплаты (задача C5, issue #1082, ТЗ COST-16) — чистая арифметика, без базы.
///
/// <para><b>Одна функция на предпросмотр и на запись.</b> Форма обещает перенос, сервер записывает —
/// посчитай они порознь, обещанное разошлось бы с записанным, и узнали бы об этом по отчёту закрытого
/// месяца. Поэтому расклад считает <see cref="Plan" />, и зовут его все: предпросмотр, отметка оплаты,
/// правка разноски оплаченного счёта и чтение расклада.</para>
///
/// <para><b>Своей формулы даты у модуля нет</b> — её даёт порт (<see cref="PeriodBoundaries.AccountingDate" />).
/// Здесь решается только, ЧЕЙ это период: доля на стройку — контур стройки, доля на статью вне строек и
/// неразнесённый остаток — контур компании.</para>
///
/// <para>⚠️ Учётную дату в модуле считают два файла — этот и <see cref="ClosedPeriodGuard" />; сторож по
/// исходникам не даёт появиться третьему.</para>
/// </summary>
public static class PaymentPosting
{
    /// <summary>Чей период у доли: стройки, на которую она легла, иначе — компании.</summary>
    public static PeriodContour ContourOf(AllocationTarget target) =>
        target.ConstructionId is { } site ? new PeriodContour.Construction(site) : new PeriodContour.Company();

    /// <summary>
    /// Расклад оплаты.
    ///
    /// <para><b>Дата доли при правке — по значению.</b> По значению, а не по идентификатору части:
    /// построчная разноска правит цель на месте, и прежняя дата уехала бы на новую стройку. Подбор:</para>
    /// <list type="number">
    /// <item>та же строка и та же цель — дата сохраняется; так читается и записанный расклад;</item>
    /// <item>иначе — дата прежней доли той же строки и того же КОНТУРА (стройка; у статей вне строек —
    /// компания), у которой деньги УШЛИ: её убрали или уменьшили (issue #1341). Перенос в другой раздел
    /// той же стройки и раздел, поделённый на два, не уносят деньги из её месяца в следующий — ТЗ
    /// (COST-16) пересчитывает день только у доли, перенесённой на другую стройку;</item>
    /// <item>иначе — дата по правилу оплаты.</item>
    /// </list>
    /// <para>⚠️ Только у доли, отдавшей деньги, а не у любой прежней доли контура (ревью PR #1342). Доля,
    /// пришедшая в новый раздел С ДРУГОЙ стройки, взяла бы день соседа, который никуда не делся, — и
    /// деньги чужой стройки легли бы в её прошлый месяц; доля, выделенная из «р1», взяла бы день
    /// нетронутого «р2». Отдавших несколько, а дни у них разные — позднейший, как у отрицательного
    /// остатка: какая из них отдала деньги этой доле, не знает никто, а поздний день не кладёт деньги в
    /// месяц раньше, чем они лежали у любой из отдавших.</para>
    ///
    /// <para><b>Остаток</b> — сумма к оплате минус деньги долей. Его дата сохраняется, пока остаток
    /// есть, и ставится заново, когда он появился из нуля. Есть ли он «до правки» и с какой датой —
    /// говорит снимок (<see cref="PostedBefore.Remainder" />), сам Plan дату не выводит. Снимок от
    /// <see cref="Before" /> называет ту, с которой остаток читают: записанную, а без неё — день самой
    /// поздней доли с деньгами. Собранный иначе снимок обязан делать то же — иначе остаток без
    /// записанной даты сочтётся появившимся из нуля.</para>
    /// </summary>
    public static PaymentPlan Plan(
        DateOnly paidOn, decimal total, IEnumerable<AllocationLine> lines, IReadOnlyList<InvoiceAllocation> parts,
        PostedBefore kept, PeriodBoundaries boundaries, decimal tolerance)
    {
        var money = Balance(lines, parts, total, tolerance).Money
            .Where(share => share.Amount is not null)
            .ToDictionary(share => share.Id, share => share.Amount!.Value);

        var given = Given(kept, parts, money);
        var shares = parts
            .OrderBy(p => p.LineId).ThenBy(p => p.Ordinal).ThenBy(p => p.Id)
            .Select(p =>
            {
                var date = kept.Shares.TryGetValue((p.LineId, p.Target), out var was) ? was
                    : given.TryGetValue((p.LineId, ContourOf(p.Target)), out var from) ? from
                    : boundaries.AccountingDate(paidOn, ContourOf(p.Target));
                return new PostedShare(p.Id, p.LineId, p.Target,
                    money.TryGetValue(p.Id, out var amount) ? amount : null, date, date != paidOn);
            })
            .ToList();

        var rest = total - money.Values.Sum();
        if (rest == 0) return new PaymentPlan(paidOn, shares, null);

        // Отрицательный остаток — поправка к деньгам долей (строки больше суммы к оплате), и день у неё
        // тот же, что у денег, которые она поправляет: самая поздняя доля с деньгами. Своей даты по
        // контуру компании ей давать нельзя (ревью PR #1192): месяц, где лежит одна поправка, стал бы
        // «учётным периодом» счёта с отрицательной суммой, а закрытие компании запирало бы счёт из-за неё.
        var corrected = rest < 0 && shares.Any(s => s.Amount is not null)
            ? shares.Where(s => s.Amount is not null).Max(s => s.AccountingOn)
            : (DateOnly?)null;
        var on = kept.Remainder ?? corrected ?? boundaries.AccountingDate(paidOn, new PeriodContour.Company());
        return new PaymentPlan(paidOn, shares, new PostedRemainder(rest, on, on != paidOn));
    }

    /// <summary>
    /// Дни, которые прежние доли ОТДАЮТ новым: по строке и контуру — позднейший день среди прежних долей,
    /// у которых деньги ушли (доли нет — или денег у неё стало меньше по модулю). Считается один раз на
    /// расклад, а не перебором на каждую долю.
    /// </summary>
    private static Dictionary<(Guid? LineId, PeriodContour Contour), DateOnly> Given(
        PostedBefore kept, IReadOnlyList<InvoiceAllocation> parts, IReadOnlyDictionary<Guid, decimal> money)
    {
        var now = parts.GroupBy(p => (p.LineId, p.Target)).ToDictionary(
            g => g.Key,
            g => g.Any(p => money.ContainsKey(p.Id)) ? g.Sum(p => money.GetValueOrDefault(p.Id)) : (decimal?)null);

        // Деньги, которых не посчитать (в строке не вписана цена), «ушедшими» не считаем: сравнивать нечего,
        // а убранную долю узнаём и так — её нет.
        bool Gave((Guid? LineId, AllocationTarget Target) key) =>
            !now.TryGetValue(key, out var after)
            || (kept.Money.GetValueOrDefault(key) is { } before && after is { } left && Math.Abs(left) < Math.Abs(before));

        return kept.Shares
            .Where(s => Gave(s.Key))
            .GroupBy(s => (s.Key.LineId, Contour: ContourOf(s.Key.Target)))
            .ToDictionary(g => g.Key, g => g.Max(s => s.Value));
    }

    /// <summary>
    /// Почему счёт нельзя оплатить — и почему оплаченный нельзя так править; <c>null</c> — можно.
    ///
    /// <para>Сумма обязана биться со строками (решение владельца 04.10.2026): расхождение сверх допуска
    /// — не остаток, а несведённый счёт. Допуск тот же, что у «разобран» (ТЗ COST-13), и число одно.
    /// Счёт без строк сверять не с чем — его сумма целиком лежит в остатке или в разноске суммой.</para>
    /// </summary>
    public static string? Refusal(Invoice invoice, AllocationBalance balance)
    {
        if (invoice.Total is not { } total || total == 0)
            return "у счёта не указана сумма к оплате";

        if (!balance.WithinTolerance && balance.Discrepancy is { } gap)
            return $"сумма строк {total - gap:0.00} расходится с суммой к оплате {total:0.00} на {Math.Abs(gap):0.00} " +
                   $"(допуск {balance.Tolerance:0.00})";

        return null;
    }

    public static AllocationBalance Balance(
        IEnumerable<AllocationLine> lines, IEnumerable<InvoiceAllocation> parts, decimal? total,
        decimal tolerance) =>
        AllocationMath.Of(lines,
            parts.Select(p => new AllocationPart(p.Id, p.LineId, p.Ordinal, p.Quantity, p.Amount)), total,
            tolerance);

    /// <summary>
    /// Деньги счёта по частям: каждая часть разноски со своей суммой и учётным днём и — если суммы
    /// частей не сложились в сумму к оплате — остаток. Остаток с нулём не приходит: дата у него могла
    /// остаться, а денег в ней нет. У счёта без суммы к оплате остатка нет — считать его не от чего.
    /// </summary>
    /// <param name="balance">Баланс счёта — посчитанный вызывающим: он нужен ему и сам по себе.</param>
    public static IReadOnlyList<PostedMoney> Money(
        AllocationBalance balance, decimal? total, IReadOnlyList<InvoiceAllocation> parts, DateOnly? remainderOn)
    {
        var amounts = balance.Money.ToDictionary(share => share.Id, share => share.Amount);
        var money = parts.Select(p => new PostedMoney(p, amounts.GetValueOrDefault(p.Id), p.AccountingOn)).ToList();
        if (total is { } whole && whole - money.Sum(m => m.Amount ?? 0) is var rest && rest != 0)
        {
            // ⚠️ Остаток без своей даты у счёта, доли которого датированы (ревью PR #1249). Так бывает
            // после понижения допуска: при оплате расхождение было в допуске и ушло в последнюю
            // часть, остатка не было — и даты ему не записали. Теперь он есть, а без даты выпал бы из
            // ВСЕХ учётных месяцев: сумма счёта по периодам стала бы меньше суммы к оплате. День
            // берём у самой поздней доли с деньгами — тем же правилом, каким расклад датирует
            // поправку к деньгам долей (см. Plan). У неоплаченного счёта дат нет ни у кого — и здесь.
            money.Add(new(null, rest, remainderOn ?? Latest(money.Select(m => (m.Amount, m.AccountingOn)))));
        }

        return money;
    }

    /// <summary>
    /// Деньги оплаченного счёта по учётным МЕСЯЦАМ — как записано: доли с деньгами своими датами и
    /// остаток своей. По возрастанию; месяц назван первым своим днём.
    ///
    /// <para>Одна функция на форму счёта, реестр и затраты: посчитай они порознь, «учётный период» в
    /// форме однажды разошёлся бы с колонкой реестра. Доля без денег (в строке не вписана цена,
    /// разноска ждёт пересчёта) дату несёт, а в затраты месяца не входит — её месяц периодом счёта не
    /// называется.</para>
    /// </summary>
    /// <param name="only">Какие деньги считать: реестр под сужающим отбором называет доли на объект и
    /// (или) месяцы. Спрашивается о каждой доле с её учётным днём и об остатке — у него доли нет (null):
    /// остаток не лежит ни на одном объекте, и под отбором по объекту условие его не пропустит.</param>
    public static IReadOnlyList<PostedMonth> Months(
        IEnumerable<PostedMoney> money, Func<InvoiceAllocation?, DateOnly, bool>? only = null) =>
    [
        .. money
            .Where(m => m is { Amount: not null, AccountingOn: { } day } && only?.Invoke(m.Part, day) != false)
            .GroupBy(m => MonthOf(m.AccountingOn!.Value))
            .OrderBy(g => g.Key)
            .Select(g => new PostedMonth(g.Key, g.Sum(m => m.Amount!.Value))),
    ];

    /// <summary>То же — из баланса: так месяцы спрашивает форма счёта.</summary>
    public static IReadOnlyList<PostedMonth> Months(
        AllocationBalance balance, decimal? total, IReadOnlyList<InvoiceAllocation> parts, DateOnly? remainderOn,
        Func<InvoiceAllocation?, DateOnly, bool>? only = null) =>
        Months(Money(balance, total, parts, remainderOn), only);

    /// <summary>Учётный месяц дня — первым своим числом: так месяц назван везде, где он группирует деньги.</summary>
    public static DateOnly MonthOf(DateOnly day) => new(day.Year, day.Month, 1);

    /// <summary>Что записано сейчас — снимок до правки.</summary>
    /// <param name="balance">Баланс счёта ДО правки — по нему видно, у какой доли деньги потом ушли.</param>
    public static PostedBefore Before(Invoice invoice, IReadOnlyList<InvoiceAllocation> parts, AllocationBalance balance)
    {
        var amounts = balance.Money.ToDictionary(share => share.Id, share => share.Amount);
        var rest = invoice.Total - parts.Sum(p => amounts.GetValueOrDefault(p.Id) ?? 0);
        return new(
            parts.Where(p => p.AccountingOn is not null)
                .GroupBy(p => (p.LineId, p.Target))
                .ToDictionary(g => g.Key, g => g.First().AccountingOn!.Value),
            parts.GroupBy(p => (p.LineId, p.Target))
                .ToDictionary(g => g.Key, g => g.Any(p => amounts.GetValueOrDefault(p.Id) is not null)
                    ? g.Sum(p => amounts.GetValueOrDefault(p.Id) ?? 0) : (decimal?)null),
            // ⚠️ День остатка — тот, каким его ЧИТАЮТ (issue #1345), а не сырая колонка. Остаток,
            // рождённый понижением допуска, своего дня не записал; чтение (Money) кладёт его в месяц
            // самой поздней доли. Возьми снимок колонку — Plan счёл бы остаток появившимся из нуля и дал
            // ему новый день: правка, не тронувшая денег остатка, унесла бы его в другой месяц.
            // Сегодня такой счёт не сведён и расклад по нему не считается — путь откроет исправление (E2).
            // ⚠️ Нет остатка — нет и дня, даже записанного (E2, spec v3.3 разд. 3): день у нуля рублей —
            // «фантом». Сырая колонка победила бы и при нуле, и остаток, появившийся снова, вернулся бы
            // старым днём, а не днём по правилу оплаты.
            rest is null or 0
                ? null
                : invoice.RemainderAccountingOn ?? Latest(parts.Select(p => (amounts.GetValueOrDefault(p.Id), p.AccountingOn))));
    }

    /// <summary>
    /// День остатка без записанного — самой поздней доли с деньгами (ревью PR #1249): тем же правилом
    /// расклад датирует поправку к деньгам долей. Одно место на чтение (<see cref="Money" />) и снимок
    /// «до правки» (<see cref="Before" />): разойдись они — правка унесла бы остаток из месяца, где его
    /// показывают.
    /// </summary>
    private static DateOnly? Latest(IEnumerable<(decimal? Amount, DateOnly? AccountingOn)> money) =>
        money.Where(m => m.Amount is not null).Max(m => m.AccountingOn);

    /// <summary>Положить расклад в записи.</summary>
    public static void Apply(Invoice invoice, IReadOnlyList<InvoiceAllocation> parts, PaymentPlan plan)
    {
        var dates = plan.Shares.ToDictionary(s => s.Id, s => s.AccountingOn);
        foreach (var part in parts) part.Post(dates[part.Id]);
        invoice.PostRemainder(plan.Remainder?.AccountingOn);
    }

    /// <summary>Оплату отменили: учётных дат у счёта больше нет.</summary>
    public static void Clear(Invoice invoice, IReadOnlyList<InvoiceAllocation> parts)
    {
        foreach (var part in parts) part.Post(null);
        invoice.PostRemainder(null);
    }
}

/// <summary>Чем заперт счёт: чьё закрытие и по какую дату.</summary>
/// <param name="ConstructionId">Стройка со своим закрытием; <c>null</c> — закрытие компании.</param>
public sealed record PeriodLock(Guid? ConstructionId, DateOnly Through);

/// <summary>
/// Закрытый период запирает оплаченный счёт (C5, issue #1082, ТЗ CORE-35).
///
/// <para><b>Запирается счёт целиком</b>, если закрыта хотя бы одна его учётная дата (решение владельца
/// 04.10.2026). По долям нельзя: копейки округления уходят в последнюю часть строки, расхождение — в
/// последнюю часть счёта, так что правка открытой доли меняет деньги закрытой.</para>
///
/// <para>Неоплаченный счёт не принадлежит ни одному периоду — его проверка не трогает.</para>
///
/// <para>⚠️ Запирает и доля БЕЗ ДЕНЕГ (в строке не вписана цена, разноска ждёт пересчёта), хотя в
/// затраты закрытого месяца она не вошла (ревью PR #1191). Нарочно: деньги ей даёт правка — вписанная
/// цена, — а дату доля при правке сохраняет, так что открытый для правок счёт положил бы деньги в
/// закрытый период обычным сохранением строки. Отказать лишнему счёту дешевле, чем пустить такой.</para>
/// </summary>
public static class ClosedPeriodGuard
{
    public static PeriodLock? LockOf(Invoice invoice, IEnumerable<InvoiceAllocation> parts, PeriodBoundaries boundaries)
    {
        if (invoice.Payment != InvoicePaymentState.Paid) return null;

        var company = new PeriodContour.Company();
        var dated = parts.Where(p => p.AccountingOn is not null)
            .Select(p => (Date: p.AccountingOn!.Value, Contour: PaymentPosting.ContourOf(p.Target), p.ConstructionId))
            .ToList();
        if (invoice.RemainderAccountingOn is { } rest) dated.Add((rest, company, null));

        // Закрытие компании называем раньше закрытия стройки: оно закрывает всё, и «у стройки А» про
        // день, закрытый для всех, отправило бы человека отменять не то закрытие.
        foreach (var (date, _, _) in dated)
            if (boundaries.IsClosed(date, company))
                return new PeriodLock(null, boundaries.ClosedThrough(company)!.Value);

        foreach (var (date, contour, site) in dated)
            if (boundaries.IsClosed(date, contour))
                return new PeriodLock(site, boundaries.ClosedThrough(contour)!.Value);

        return null;
    }
}
