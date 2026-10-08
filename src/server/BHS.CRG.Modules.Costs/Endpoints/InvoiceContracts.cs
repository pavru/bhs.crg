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
/// <param name="Lines">Строки счёта в порядке бумаги (C2, issue #1078). Приезжают ВМЕСТЕ со счётом, а
/// не отдельным запросом: форма показывает шапку и строки одним экраном, и два запроса дали бы два
/// снимка одного счёта — сверка суммы строк с суммой к оплате считалась бы по разным состояниям.</param>
/// <param name="Totals">Сверка суммы строк с суммой к оплате (ТЗ COST-6.2) и счётчик строк, ждущих
/// позиции номенклатуры.</param>
/// <param name="Allocation">«Разнесён» и чего ему не хватает (F1, issue #1085) — то самое условие,
/// которое проверяет переход «разобран»: считай форма его сама, кнопка и отказ расходились бы.</param>
/// <param name="Payment">Оплата (C5, issue #1082): оплачен ли, почему оплатить нельзя и чем счёт заперт —
/// словами сервера, чтобы форма не выводила «заперт» из границ периодов своей формулой.</param>
/// <param name="References">Что стало с записями ядра, на которые ссылается шапка (issue #1184).</param>
public sealed record InvoiceView(
    Guid Id,
    // Версия счёта (issue #1176): её называет каждая правка заголовком If-Match. Строкой — это отметка,
    // а не число: сравнивать её на «больше» нельзя, только на «та же».
    string Version,
    Guid DocumentTypeId,
    JsonObject Requisites,
    IReadOnlyList<string> Unconfirmed,
    IReadOnlyList<InvoiceDuplicate> Duplicates,
    IReadOnlyList<InvoiceLineView> Lines,
    InvoiceLineTotals Totals,
    AllocationSummaryView Allocation,
    PaymentView Payment,
    InvoiceReferencesView References,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Состояние ссылок шапки счёта на записи ядра (ТЗ CORE-34.4, issue #1184, #1185): <c>present</c>,
/// <c>archived</c>, <c>lost</c>; <c>null</c> — ссылки нет.
///
/// <para>Считает сервер, обратным опросом ядра, — тем же, что и счётчик потерянных ссылок. Выводи это
/// форма сравнением со списком организаций, «список ещё грузится» выглядел бы потерей.</para>
/// </summary>
/// <param name="SupplierName">Название поставщика, если он В АРХИВЕ; иначе <c>null</c>. Едет с
/// ответом счёта, а не берётся формой из списка на выбор: архивной организации в том списке нет, и
/// поле открылось бы пустым (issue #1185). Действующего поставщика форма называет по списку.</param>
/// <param name="PayerName">То же для плательщика.</param>
public sealed record InvoiceReferencesView(
    string? Supplier, string? Payer, string DocumentType, string? SupplierName, string? PayerName)
{
    public const string Present = "present";
    public const string Archived = "archived";
    public const string Lost = "lost";

    public static string Of(BHS.CRG.Modules.Ports.ReferenceState state) => state switch
    {
        BHS.CRG.Modules.Ports.ReferenceState.Lost => Lost,
        BHS.CRG.Modules.Ports.ReferenceState.Archived => Archived,
        _ => Present,
    };
}

/// <summary>Счёт в списке. Полей ровно столько, сколько нужно реестру, — реквизиты не едут.</summary>
/// <param name="SupplierName">Название поставщика из справочника ядра; <c>null</c> — поставщик не
/// выбран либо запись справочника удалили. Второе от первого отличимо по <c>SupplierId</c>: ссылка
/// есть, названия нет — это потеря, и молчать о ней нельзя.</param>
/// <param name="LinesWithoutNomenclature">Сколько строк ждёт позиции номенклатуры — счётчик
/// «Разобрать» (ТЗ COST-6.2). В реестре он нужен затем, чтобы не открывать счёт ради ответа на вопрос
/// «а с этим что делать».</param>
/// <param name="SupplierArchived">Поставщик в архиве (issue #1185) — реестр ставит значок у названия.</param>
/// <param name="References">Что со ссылками счёта на записи ядра (issue #1186): пометки строки.</param>
public sealed record InvoiceListItem(
    Guid Id,
    string? Number,
    DateOnly? IssuedOn,
    Guid? SupplierId,
    string? SupplierName,
    bool SupplierArchived,
    decimal? Total,
    string State,
    string Payment,
    DateOnly? DueDate,
    string? Purpose,
    int UnconfirmedCount,
    bool HasScan,
    int LinesCount,
    int LinesWithoutNomenclature,
    InvoiceListReferences? References = null,
    // Имя файла скана: у счёта из скана до распознавания нет ни номера, ни поставщика, и без имени
    // файла три таких строки подряд были бы неразличимы.
    string? ScanFileName = null,
    // Что со сканом: читается либо счёт стоит под «Не распознано» (определение — в
    // InvoiceListRecognition); пусто — сказать нечего.
    InvoiceListScan? Recognition = null);

/// <summary>Найденный дубликат: чем он дубликат — тем и назван.</summary>
public sealed record InvoiceDuplicate(Guid Id, string? Number, DateOnly? IssuedOn, decimal? Total);

/// <summary>Сборка ответов из записей. Отдельно от адресов: адреса про маршруты и права.</summary>
public static class InvoiceViews
{
    /// <param name="names">Названия позиций номенклатуры по идентификаторам, либо <c>null</c> — «вида
    /// нет вовсе, прочитать нечем». Разница важна: отсутствие в СЛОВАРЕ — потеря записи, отсутствие
    /// СЛОВАРЯ — незнание, и выдавать второе за первое нельзя (см. <c>InvoiceLineView</c>).</param>
    public static InvoiceView Of(
        Invoice invoice, string version, IReadOnlyList<InvoiceDuplicate> duplicates,
        IReadOnlyList<InvoiceLine> lines, IReadOnlyDictionary<Guid, string?>? names,
        InvoiceAllocationRead allocation, PaymentView payment,
        InvoiceReferencesView references, IReadOnlySet<Guid> lost, IReadOnlySet<Guid> archived) => new(
        invoice.Id,
        version,
        invoice.DocumentTypeId,
        InvoiceRequisites.Merge(invoice),
        invoice.Unconfirmed,
        duplicates,
        [.. lines.OrderBy(l => l.Ordinal).Select(l => Line(l, names, allocation.Lines[l.Id], lost, archived))],
        InvoiceLineTotals.Of(lines),
        allocation.Summary,
        payment,
        references,
        invoice.CreatedAt,
        invoice.UpdatedAt);

    /// <param name="lost">Записи, которых в ядре нет вовсе, — по обратному опросу. Нужен ПОМИМО словаря
    /// названий: без типа «Номенклатура» словаря нет, а удалённая запись от этого не перестаёт быть
    /// потерей. Запись, переехавшая в другой вид, потеряна для строки тоже — её называет словарь.</param>
    /// <param name="archived">Записи в архиве — по тому же опросу. Не потеря: позиция названа и
    /// сведена, пометка только объясняет, почему её нет в поиске (issue #1185).</param>
    public static InvoiceLineView Line(
        InvoiceLine line, IReadOnlyDictionary<Guid, string?>? names, LineAllocationView allocation,
        IReadOnlySet<Guid>? lost = null, IReadOnlySet<Guid>? archived = null) =>
        Line(line, names, allocation, Issue(line.NomenclatureId, names, lost),
            line.NomenclatureId is { } id && archived?.Contains(id) == true);

    private static InvoiceLineView Line(
        InvoiceLine line, IReadOnlyDictionary<Guid, string?>? names, LineAllocationView allocation, string? issue,
        bool positionArchived) => new(
        line.Id,
        line.Ordinal,
        line.NomenclatureId,
        line.NomenclatureId is { } id && names is not null && names.TryGetValue(id, out var name)
            ? name
            : null,
        issue is not null,
        issue,
        positionArchived,
        line.SupplierText,
        line.SupplierCode,
        line.Unit,
        line.Quantity,
        line.Price,
        line.VatRate,
        line.VatAmount,
        line.Amount,
        line.Note,
        allocation);

    public const string NomenclatureGone = "lost";
    public const string NomenclatureMoved = "moved";

    private static string? Issue(Guid? position, IReadOnlyDictionary<Guid, string?>? names, IReadOnlySet<Guid>? lost) =>
        position is not { } id ? null
        : lost?.Contains(id) == true ? NomenclatureGone
        // Без обратного опроса (lost не дан) отличить нечем — зовём потерей, как звали всегда.
        : names is not null && !names.ContainsKey(id) ? lost is null ? NomenclatureGone : NomenclatureMoved
        : null;

    public static InvoiceListItem Item(
        Invoice invoice, string? supplierName, bool supplierArchived, int lines, int withoutNomenclature) => new(
        invoice.Id,
        invoice.Number,
        invoice.IssuedOn,
        invoice.SupplierId,
        supplierName,
        supplierArchived,
        invoice.Total,
        InvoiceRequisites.Label(invoice.State),
        InvoiceRequisites.Label(invoice.Payment),
        invoice.DueDate,
        invoice.Purpose,
        invoice.Unconfirmed.Count,
        invoice.ScanBlobPath is not null,
        lines,
        withoutNomenclature,
        ScanFileName: invoice.ScanFileName);

    public static InvoiceDuplicate Duplicate(Invoice invoice) =>
        new(invoice.Id, invoice.Number, invoice.IssuedOn, invoice.Total);
}
