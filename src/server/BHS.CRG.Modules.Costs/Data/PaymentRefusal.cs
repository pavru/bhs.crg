namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Род причины, по которой счёт нельзя оплатить (COST-6.4): приходит в ответе счёта рядом с причиной
/// словами (<c>payment.refusalKind</c>), а готовность по нему помечает пункт «мешает оплате».
///
/// <para>Зачем род, если есть текст. Индикатор состояния собирает сообщения о счёте в одно место, и одно
/// расхождение там обязано быть одним пунктом. Причину отказа и пункт готовности, чинящий её же, он
/// сводит по роду — сравнивать тексты значило бы держать на клиенте второе правило.</para>
/// </summary>
public static class PaymentRefusal
{
    /// <summary>Суммы к оплате нет (или она нулевая) — <see cref="PaymentPosting.RefusalKind(Invoice, AllocationBalance)" />.</summary>
    public const string Amount = "amount";

    /// <summary>Сумма строк расходится с суммой к оплате сверх допуска — там же.</summary>
    public const string Discrepancy = "discrepancy";

    /// <summary>Счёт отклонён — по нему решили не платить. Решает связка записи, а не правило денег.</summary>
    public const string Rejected = "rejected";

    /// <summary>
    /// Род причины, по которой НЕОПЛАЧЕННЫЙ счёт оплатить нельзя; <c>null</c> — можно. Одним местом для ответа
    /// оплаты (<c>payment.refusalKind</c>) и готовности (пометка «мешает оплате»): порядок «отклонён раньше
    /// денег», записанный дважды, однажды разошёлся бы — отклонённому с расхождением готовность пометила бы
    /// пункт, починка которого оплаты не открывает (ревью PR #1790).
    /// </summary>
    public static string? Of(Invoice invoice, decimal? discrepancy, bool withinTolerance) =>
        invoice.State == InvoiceState.Rejected ? Rejected
        : PaymentPosting.RefusalKind(invoice, discrepancy, withinTolerance);

    /// <inheritdoc cref="Of(Invoice, decimal?, bool)" />
    public static string? Of(Invoice invoice, AllocationBalance balance) =>
        Of(invoice, balance.Discrepancy, balance.WithinTolerance);

    /// <summary>
    /// Та же причина словами — по роду, а не второй цепочкой условий: причина, добавленная в текст и
    /// забытая в роде, дала бы клиенту отказ без рода, то есть «оплатить можно».
    /// </summary>
    public static string? Text(Invoice invoice, AllocationBalance balance) => Of(invoice, balance) switch
    {
        null => null,
        Rejected => "счёт отклонён",
        _ => PaymentPosting.Refusal(invoice, balance),
    };
}
