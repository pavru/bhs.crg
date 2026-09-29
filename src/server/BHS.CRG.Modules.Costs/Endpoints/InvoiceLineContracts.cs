using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Строки счёта: что приходит, что уходит и как считается сверка (задача C2, issue #1078,
/// ТЗ COST-7, COST-7.2, COST-6.2).
/// </summary>
/// <param name="Lines">Строки ЦЕЛИКОМ — весь набор, а не правка одной.
///
/// <para>Почему набором: так их и правят. Человек работает с таблицей — дописывает строку, удаляет
/// строку, меняет порядок, вставляет из буфера сразу двадцать, — и адрес «изменить строку номер три»
/// потребовал бы от формы вести список отправленных правок. Присланный набор и есть новое состояние.</para>
///
/// <para>⚠️ Строка с <c>id</c> УЗНАЁТСЯ: она правится на месте, а не заводится заново. Иначе ссылки на
/// строку (разноска по количеству, F1) рвались бы на каждом сохранении формы, и виноватой выглядела бы
/// разноска.</para></param>
public sealed record InvoiceLinesRequest(IReadOnlyList<JsonElement>? Lines);

/// <summary>Строка счёта в ответе.</summary>
/// <param name="NomenclatureName">Название позиции из справочника ядра. <c>null</c> при заполненной
/// <see cref="NomenclatureId" /> означает «ссылка есть, записи нет» — позицию удалили, и молчать об
/// этом нельзя: строка выглядела бы неразобранной, хотя разбирал её человек.</param>
public sealed record InvoiceLineView(
    Guid Id,
    int Ordinal,
    Guid? NomenclatureId,
    string? NomenclatureName,
    string? SupplierText,
    string? SupplierCode,
    string? Unit,
    decimal? Quantity,
    decimal? Price,
    decimal? VatRate,
    decimal? VatAmount,
    decimal? Amount,
    string? Note);

/// <summary>
/// Сверка: сумма строк против суммы к оплате (ТЗ COST-6.2 — «всегда на виду, числом и не запретом»).
///
/// <para><b>Считает сервер, а не форма.</b> Не из недоверия к форме: это же число сверяет переход
/// «разобран», отчёты по затратам и — с F1 — баланс разноски. Посчитай его каждый у себя, расхождения
/// между экраном и отбором реестра объяснить было бы нечем.</para>
///
/// <para>⚠️ Расхождение НЕ считается здесь ни ошибкой, ни полем. Разность видна из двух чисел, и
/// назвать её «ошибкой» значило бы решить за человека: у поставщика бывает округление, скидка строкой
/// и доставка, не попавшая в таблицу. Форма показывает разность, а запретов на ней не строит.</para>
/// </summary>
/// <param name="Count">Сколько строк всего.</param>
/// <param name="WithoutNomenclature">Сколько строк ждёт позиции номенклатуры — это и есть счётчик
/// «Разобрать» (ТЗ COST-6.2).</param>
/// <param name="Amount">Сумма строк. Строки без суммы в неё не попадают — их нет, а не нуль.</param>
/// <param name="Vat">Сумма НДС по строкам — то самое «в том числе НДС», посчитанное по строкам.</param>
public sealed record InvoiceLineTotals(int Count, int WithoutNomenclature, decimal Amount, decimal Vat)
{
    /// <summary>Итоги пустого набора строк: ни суммы, ни ожидающих — черновик без строк штатен.</summary>
    public static readonly InvoiceLineTotals Empty = new(0, 0, 0m, 0m);

    public static InvoiceLineTotals Of(IReadOnlyCollection<InvoiceLine> lines) => new(
        lines.Count,
        lines.Count(l => l.NomenclatureId is null),
        lines.Sum(l => l.Amount ?? 0m),
        lines.Sum(l => l.VatAmount ?? 0m));
}

/// <summary>Разбор присланной строки — с отказами, которые называют поле подписью с экрана.</summary>
public static class InvoiceLineRequests
{
    private const string IdKey = "id";
    private const string NomenclatureKey = "nomenclature";

    /// <summary>
    /// Почему позиция — ссылка, а не наименование текстом (ТЗ COST-7). Главный сторож задачи C2, и
    /// сказан он человеку, а не только коду: отказ читают за формой.
    /// </summary>
    private const string NomenclatureWhy =
        "Позицию выбирают из справочника номенклатуры, а не вписывают наименованием: без ссылки на " +
        "справочник нельзя ни свести затраты, ни связать материал с документом качества, ни сказать " +
        "монтажнику, что ему отпустили. Наименование из бумаги поставщика присылайте в «supplierText» — " +
        "оно останется цитатой и ключом сопоставления, а строка будет ждать позиции в отборе «Разобрать».";

    /// <summary>Идентификатор присланной строки: есть — правим её, нет — заводим новую.</summary>
    public static Guid? Id(JsonElement line, int number) =>
        CostsValues.Value(line, IdKey) switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } value when Guid.TryParse(value.GetString(), out var id) => id,
            var other => throw CostsValues.Wrong($"Строка {number}: идентификатор", other,
                "строку-идентификатор уже сохранённой строки либо ничего"),
        };

    /// <summary>
    /// Значения строки — разобранные и досчитанные.
    /// </summary>
    /// <param name="number">Номер строки в присланном наборе, с единицы. Нужен отказам: «ожидается
    /// число» без номера строки заставило бы человека искать опечатку во всей таблице.</param>
    public static InvoiceLineValues Values(JsonElement line, int number)
    {
        if (line.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException(
                $"Строка {number} прислана как {line.ValueKind}, а ожидается объект с полями строки.");

        var values = new InvoiceLineValues(
            NomenclatureId: CostsValues.Reference(line, NomenclatureKey, NomenclatureWhy,
                $"Позиция номенклатуры, строка {number}"),
            SupplierText: CostsValues.Text(line, "supplierText", $"Наименование в счёте, строка {number}"),
            SupplierCode: CostsValues.Text(line, "supplierCode", $"Артикул поставщика, строка {number}"),
            Unit: CostsValues.Text(line, "unit", $"Единица измерения, строка {number}"),
            Quantity: CostsValues.Money(line, "quantity", $"Количество, строка {number}"),
            Price: CostsValues.Money(line, "price", $"Цена, строка {number}"),
            VatRate: Rate(line, number),
            VatAmount: CostsValues.Money(line, "vatAmount", $"Сумма НДС, строка {number}"),
            Amount: CostsValues.Money(line, "amount", $"Сумма, строка {number}"),
            Note: CostsValues.Text(line, "note", $"Примечание, строка {number}"));

        return values.Completed();
    }

    /// <summary>
    /// Ставка НДС в процентах, 0…100.
    ///
    /// <para>Границы ловят «2000» и «−20» — опечатку и знак, которые дали бы сумму НДС больше суммы
    /// строки. ⚠️ Путаницу «0,2 вместо 20» они НЕ ловят и не могут: 0,2 — законная ставка по виду, и
    /// отличить её от доли нечем. Видна она человеку суммой НДС, которая посчитается в сто раз меньше и
    /// не сойдётся с «в том числе НДС» из бумаги — та сверка стоит в форме на виду ровно за этим.</para>
    /// </summary>
    private static decimal? Rate(JsonElement line, int number)
    {
        var label = $"Ставка НДС, строка {number}";
        return CostsValues.Money(line, "vatRate", label) switch
        {
            null => null,
            { } rate when rate is >= 0 and <= 100 => rate,
            { } rate => throw new InvalidRequestException(
                $"Поле «{label}»: «{rate}» — не ставка НДС. Ожидаются проценты от 0 до 100 (20, 10, 0): " +
                "при ставке вне этих границ сумма НДС вышла бы больше суммы строки."),
        };
    }
}
