using BHS.CRG.Modules.Ports;
using static BHS.CRG.Modules.Costs.Data.InvoiceRequisites;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Препятствие оплате с ключом поля формы, причиной missing/lost и подписью для пользователя.</summary>
public sealed record PaymentRequisiteGap(string Field, string Kind, string Label);

/// <summary>Реквизиты новой оплаты (COST-4). Состояние поставщика читает вызывающий код.</summary>
public static class InvoicePaymentRequisites
{
    public static Guid? SupplierOf(Invoice invoice) =>
        invoice.SupplierId is { } id && id != Guid.Empty ? id : null;

    public static IReadOnlyList<PaymentRequisiteGap> Find(Invoice invoice, ReferenceState supplierState)
    {
        List<PaymentRequisiteGap> gaps = [];
        if (SupplierOf(invoice) is null)
            gaps.Add(Missing(SupplierKey));
        else if (supplierState == ReferenceState.Lost)
            gaps.Add(new(SupplierKey, "lost", $"«{TitleOf(SupplierKey)}»: запись удалена"));
        if (string.IsNullOrWhiteSpace(invoice.Number))
            gaps.Add(Missing(NumberKey));
        if (invoice.IssuedOn is null)
            gaps.Add(Missing(DateKey));
        if (NoTotal(invoice.Total))
            gaps.Add(Missing(TotalKey));
        return gaps;
    }

    private static PaymentRequisiteGap Missing(string field) =>
        new(field, "missing", $"Не заполнено поле «{TitleOf(field)}»");
}
