using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Кто записал счёт последним — в ответе счёта (COST-6.4, Э4): для пункта индикатора «изменили, пока вы
/// правили». Время — <c>updatedAt</c> рядом.
/// </summary>
/// <param name="Via">Род записи: <c>user</c>, <c>recognition</c>, <c>system</c> (<see cref="InvoiceAuthor" />).</param>
/// <param name="Name">Имя на момент записи — только у <c>user</c>. Снимок: переименованный автор назван
/// прежним именем, каким он был, когда писал.</param>
/// <param name="Mine">Записал тот, кто читает. Сравнением учётных записей, а не имён: тёзки бывают.</param>
public sealed record InvoiceAuthorView(string Via, string? Name, bool Mine)
{
    /// <returns><c>null</c> — автор неизвестен: счёт записан до колонок автора либо восстановлен из копии
    /// старше них. Это «неизвестно», а не «никто», и экран тогда называет только время.</returns>
    public static InvoiceAuthorView? Of(Invoice invoice, Guid? reader) =>
        invoice.UpdatedVia is not { } via
            ? null
            : new InvoiceAuthorView(via, invoice.UpdatedByName,
                via == InvoiceAuthor.User && invoice.UpdatedBy is { } by && by == reader);
}
