using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Две колонки срока согласны между собой у ЛЮБОГО счёта (задача G1c, issue #1090): «просрочен» — это
/// ровно «осталось дней меньше нуля». Перебираются все состояния документа и оплаты, а не названные
/// поимённо: новое состояние попадёт в перебор само.
///
/// <para>Зачем: «ждёт ли счёт оплаты» решают обе колонки, и запиши каждая это своими словами, правка
/// одной дала бы счёт с меткой «просрочен» и пустым «осталось дней» — а отборы по двум колонкам
/// вернули бы разные строки.</para>
/// </summary>
public class InvoiceDueTests
{
    private static readonly DateOnly Today = new(2031, 1, 15);

    public static TheoryData<InvoiceState, InvoicePaymentState, int?> Invoices()
    {
        var data = new TheoryData<InvoiceState, InvoicePaymentState, int?>();
        foreach (var state in Enum.GetValues<InvoiceState>())
        foreach (var payment in Enum.GetValues<InvoicePaymentState>())
        foreach (var shift in new int?[] { null, -40, -1, 0, 1, 40 })
            data.Add(state, payment, shift);
        return data;
    }

    [Theory]
    [MemberData(nameof(Invoices))]
    public void Просрочен_значит_осталось_меньше_нуля_дней(InvoiceState state, InvoicePaymentState payment, int? shift)
    {
        var invoice = Invoice(state, payment, shift);

        var days = InvoiceDue.DaysLeftOf(invoice, Today);
        var overdue = InvoiceDue.OverdueOf(invoice, Today);

        Assert.Equal(days < 0, overdue);
        // Функция клетки и выражение запроса — одно правило: собранное выражение отвечает тем же.
        Assert.Equal(days, InvoiceDue.DaysLeft(Today).Compile()(invoice));
        Assert.Equal(overdue, InvoiceDue.Overdue(Today).Compile()(invoice));
    }

    [Theory]
    [MemberData(nameof(Invoices))]
    public void Осталось_дней_есть_только_у_счёта_который_ждёт_оплаты(
        InvoiceState state, InvoicePaymentState payment, int? shift)
    {
        var awaited = state != InvoiceState.Rejected && payment != InvoicePaymentState.Paid && shift is not null;

        Assert.Equal(awaited ? shift : null, InvoiceDue.DaysLeftOf(Invoice(state, payment, shift), Today));
    }

    /// <summary>
    /// Счёт в названном состоянии. Свойства выставлены в обход методов записи: оплату и отклонение
    /// счёт сам себе не ставит (их ведут свои операции), а правилу срока важно только, что лежит в полях.
    /// </summary>
    private static Invoice Invoice(InvoiceState state, InvoicePaymentState payment, int? shift)
    {
        var invoice = Modules.Costs.Data.Invoice.Create(Guid.NewGuid(), null);
        Set(nameof(invoice.State), state);
        Set(nameof(invoice.Payment), payment);
        Set(nameof(invoice.DueDate), shift is { } days ? Today.AddDays(days) : (DateOnly?)null);
        return invoice;

        void Set(string property, object? value) =>
            typeof(Invoice).GetProperty(property)!.SetValue(invoice, value);
    }
}
