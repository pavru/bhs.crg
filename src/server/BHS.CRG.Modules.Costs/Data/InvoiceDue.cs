using System.Linq.Expressions;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// «Осталось дней» и «просрочен» — то, что у счёта считается ОТ СЕГОДНЯ (ТЗ COST-9.1, CORE-33; задача
/// G1c, issue #1090).
///
/// <para><b>Не хранится нигде.</b> Признак, который никто не ставит, верен ровно до полуночи: положи мы
/// его в колонку, наутро он был бы вчерашним, и кто-то должен был бы его пересчитывать. Поэтому
/// правило — выражением от хранимого срока и названного «сегодня».</para>
///
/// <para><b>Одно правило на запрос и на клетку.</b> Отбор и сортировка исполняют выражение в базе, а
/// клетку строки считает оно же, собранное в функцию. Запиши мы правило дважды — словами запроса и
/// словами кода, — однажды экран отобрал бы «просроченные» по одному правилу, а показал бы признак по
/// другому.</para>
///
/// <para><b>И одно «ждёт оплаты» на обе колонки</b> (<see cref="Awaited" />): оно вставлено в оба
/// правила, а не написано в каждом своими словами. Иначе правка одного из них — а решение об
/// отклонённых счетах пересмотреть могут — дала бы счёт с меткой «просрочен» и пустым «осталось дней».</para>
/// </summary>
public static class InvoiceDue
{
    /// <summary>
    /// Счёт ждёт оплаты к сроку: не отклонён, оплачен не полностью, срок определён. Частично
    /// оплаченный — ждёт: долг по нему есть. Отклонённый («не платим») — не ждёт, и срока у него по
    /// смыслу нет: иначе он висел бы в просроченных вечно.
    ///
    /// <para>Состояния оплаты названы ПЕРЕЧНЕМ, а не «всё, кроме оплаченного», по двум причинам. Новое
    /// состояние не станет «ждущим» само, молча. И перечень ложится на индекс «оплата + срок»
    /// (<c>ix_invoices_due</c>), заведённый под отбор «просрочен», а неравенство — нет.</para>
    /// </summary>
    private static readonly Expression<Func<Invoice, bool>> Awaited =
        i => i.State != InvoiceState.Rejected
             && (i.Payment == InvoicePaymentState.Unpaid || i.Payment == InvoicePaymentState.Partial)
             && i.DueDate != null;

    private static readonly Expression<Func<Invoice, DateOnly, decimal?>> DaysLeftRule = WhenAwaited(
        (Invoice i, DateOnly today) => (decimal?)(i.DueDate!.Value.DayNumber - today.DayNumber),
        (awaited, days) => Expression.Condition(awaited, days, Expression.Constant(null, typeof(decimal?))));

    // Условия соединены «И», а не выбором «если ждёт — то…»: так запрос «просрочен» остаётся
    // перечнем условий по колонкам, и база может взять индекс.
    private static readonly Expression<Func<Invoice, DateOnly, bool?>> OverdueRule = WhenAwaited(
        (Invoice i, DateOnly today) => (bool?)(i.DueDate < today),
        (awaited, late) => Expression.Convert(
            Expression.AndAlso(awaited, Expression.Convert(late, typeof(bool))), typeof(bool?)));

    // Клеткам — те же правила, собранные в функции ОДИН раз: «сегодня» у них параметр, и собирать их
    // заново на каждое чтение таблицы незачем.
    private static readonly Func<Invoice, DateOnly, decimal?> DaysLeftCell = DaysLeftRule.Compile();
    private static readonly Func<Invoice, DateOnly, bool?> OverdueCell = OverdueRule.Compile();

    /// <summary>
    /// Сколько дней до срока «оплатить до»; у просроченного — отрицательное, в день срока — ноль.
    /// Пусто, если счёт оплаты не ждёт или срок не определён (ТЗ COST-9.1: без даты отгрузки срока
    /// нет, и «осталось дней» у оплаченного не показывается).
    /// </summary>
    public static Expression<Func<Invoice, decimal?>> DaysLeft(DateOnly today) => On(DaysLeftRule, today);

    /// <summary>
    /// Просрочен ли счёт: ждёт оплаты, а срок прошёл. В сам день срока — ещё нет. У счёта без срока и
    /// у счёта, который оплаты не ждёт, — «нет», а не «не определено»: это метка, она либо стоит, либо нет.
    /// </summary>
    public static Expression<Func<Invoice, bool?>> Overdue(DateOnly today) => On(OverdueRule, today);

    /// <summary>«Осталось дней» одного счёта — тем же правилом, что у запроса.</summary>
    public static decimal? DaysLeftOf(Invoice invoice, DateOnly today) => DaysLeftCell(invoice, today);

    /// <summary>«Просрочен» одного счёта — тем же правилом, что у запроса.</summary>
    public static bool? OverdueOf(Invoice invoice, DateOnly today) => OverdueCell(invoice, today);

    /// <summary>Правило колонки: её значение, соединённое с общим «ждёт оплаты».</summary>
    private static Expression<Func<Invoice, DateOnly, T>> WhenAwaited<T>(
        Expression<Func<Invoice, DateOnly, T>> value, Func<Expression, Expression, Expression> join)
    {
        var awaited = new Swap(Awaited.Parameters[0], value.Parameters[0]).Visit(Awaited.Body);
        return Expression.Lambda<Func<Invoice, DateOnly, T>>(join(awaited, value.Body), value.Parameters);
    }

    /// <summary>Правило на названный день: «сегодня» уходит в запрос значением.</summary>
    private static Expression<Func<Invoice, T>> On<T>(Expression<Func<Invoice, DateOnly, T>> rule, DateOnly today) =>
        Expression.Lambda<Func<Invoice, T>>(
            new Swap(rule.Parameters[1], Expression.Constant(today)).Visit(rule.Body), rule.Parameters[0]);

    private sealed class Swap(ParameterExpression from, Expression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
