using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Что приходит на запись счёта (задача C1, issue #1076).
/// </summary>
/// <param name="Requisites">Поля по схеме типа — объект «ключ: значение». Часть из них ложится в
/// колонки таблицы, часть остаётся схемой; какая именно — дело <see cref="InvoiceRequisites" />, и
/// клиенту эта граница не видна вовсе.</param>
/// <param name="Unconfirmed">Ключи полей, которые заполнило распознавание и человек ещё не
/// подтвердил (решение владельца 29.09.2026).
///
/// <para>⚠️ Принимается только при СОЗДАНИИ. Так черновик заводит фоновое распознавание (B1b,
/// issue #1077): метки приезжают вместе с полями, потому что ставит их тот, кто их заполнил. На
/// правке метки не присылаются — там они снимаются сами, правкой поля, или действием «Всё
/// верно».</para></param>
public sealed record InvoiceSaveRequest(JsonElement Requisites, IReadOnlyList<string>? Unconfirmed = null);

/// <summary>
/// «Всё верно» по блоку формы: снять метки с названных полей (решение владельца 29.09.2026).
/// </summary>
/// <param name="Fields">Ключи полей блока. Перечисляет их КЛИЕНТ: блок — это видимая группа формы, и
/// сервер о ней не знает. Придумай он группы сам, «Всё верно» подтверждало бы поля, которых человек
/// на экране не видел.
///
/// <para>⚠️ Пустой список — отказ, а не «снять все». Подтверждение ничего — это промах клиента, и
/// самое дорогое из возможных прочтений промаха здесь именно «снять все»: метки исчезли бы разом и
/// вернуть их было бы нечем.</para></param>
public sealed record InvoiceConfirmRequest(IReadOnlyList<string> Fields);

/// <summary>Счёт целиком: реквизиты по схеме плюс то, что знает о записи сам модуль.</summary>
/// <param name="Requisites">Поля по схеме типа — колонки и остаток схемы, собранные вместе. Снаружи
/// запись модуля выглядит обычным документом, и рисует её общая форма.</param>
/// <param name="Unconfirmed">Что распознано и не подтверждено. Переживает повторное открытие
/// черновика — в этом весь смысл (ТЗ COST-6.2).</param>
/// <param name="Duplicates">Счета с тем же поставщиком, номером и датой (ТЗ COST-6.2) — оговорка, а
/// не запрет: у поставщика бывает два счёта с одним номером в один день, и человек знает об этом
/// больше нас.</param>
public sealed record InvoiceView(
    Guid Id,
    Guid DocumentTypeId,
    JsonObject Requisites,
    IReadOnlyList<string> Unconfirmed,
    IReadOnlyList<InvoiceDuplicate> Duplicates,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Счёт в списке. Полей ровно столько, сколько нужно реестру, — реквизиты не едут.</summary>
/// <param name="SupplierName">Название поставщика из справочника ядра; <c>null</c> — поставщик не
/// выбран либо запись справочника удалили. Второе от первого отличимо по <c>SupplierId</c>: ссылка
/// есть, названия нет — это потеря, и молчать о ней нельзя.</param>
public sealed record InvoiceListItem(
    Guid Id,
    string? Number,
    DateOnly? IssuedOn,
    Guid? SupplierId,
    string? SupplierName,
    decimal? Total,
    string State,
    string Payment,
    DateOnly? DueDate,
    string? Purpose,
    int UnconfirmedCount,
    bool HasScan);

/// <summary>Найденный дубликат: чем он дубликат — тем и назван.</summary>
public sealed record InvoiceDuplicate(Guid Id, string? Number, DateOnly? IssuedOn, decimal? Total);

/// <summary>Сборка ответов из записей. Отдельно от адресов: адреса про маршруты и права.</summary>
public static class InvoiceViews
{
    public static InvoiceView Of(Invoice invoice, IReadOnlyList<InvoiceDuplicate> duplicates) => new(
        invoice.Id,
        invoice.DocumentTypeId,
        InvoiceRequisites.Merge(invoice),
        invoice.Unconfirmed,
        duplicates,
        invoice.CreatedAt,
        invoice.UpdatedAt);

    public static InvoiceListItem Item(Invoice invoice, string? supplierName) => new(
        invoice.Id,
        invoice.Number,
        invoice.IssuedOn,
        invoice.SupplierId,
        supplierName,
        invoice.Total,
        InvoiceRequisites.Label(invoice.State),
        InvoiceRequisites.Label(invoice.Payment),
        invoice.DueDate,
        invoice.Purpose,
        invoice.Unconfirmed.Count,
        invoice.ScanBlobPath is not null);

    public static InvoiceDuplicate Duplicate(Invoice invoice) =>
        new(invoice.Id, invoice.Number, invoice.IssuedOn, invoice.Total);
}
