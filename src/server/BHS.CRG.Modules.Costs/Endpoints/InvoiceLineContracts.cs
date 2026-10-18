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
/// <param name="VatMode">Режим НДС счёта (issue #1497) — едет со строками: он описывает их суммы, и
/// пишется атомарно с ними. <c>null</c> — режим не меняется; строки тогда читаются по лежащему.</param>
public sealed record InvoiceLinesRequest(IReadOnlyList<JsonElement>? Lines, VatMode? VatMode = null);

/// <summary>Строка счёта в ответе.</summary>
/// <param name="NomenclatureName">Название позиции из справочника ядра. <c>null</c> при заполненной
/// <see cref="NomenclatureId" /> означает «ссылка есть, записи нет» — позицию удалили, и молчать об
/// этом нельзя: строка выглядела бы неразобранной, хотя разбирал её человек.</param>
public sealed record InvoiceLineView(
    Guid Id,
    int Ordinal,
    Guid? NomenclatureId,
    string? NomenclatureName,
    /// <summary>
    /// Ссылка есть, а записи справочника по ней НЕТ — позицию удалили. Это потеря, и она обязана
    /// выглядеть иначе, чем «позиция не выбрана» (чинит человек за формой) и чем «позиция без
    /// названия» (законная запись, у которой имя не заполнено).
    ///
    /// <para>⚠️ Считает СЕРВЕР, потому что только он знает разницу. У формы на все три случая один
    /// признак — пустое название, — и она объявляла бы потерей выбранную только что позицию без
    /// имени; снять такую ссылку было нечем, и строка не сохранялась вовсе (нашло ревью PR #1117).</para>
    ///
    /// <para>⚠️ «Справочника нет вовсе» потерей НЕ считается: <c>false</c> здесь означает «не знаем»,
    /// а не «на месте». Выдай мы «потеряно» на ненайденный вид, каждая ссылка в установке без типа
    /// «Номенклатура» выглядела бы битой.</para>
    /// </summary>
    bool NomenclatureLost,
    // Что именно не так со ссылкой (issue #1184): «lost» — записи в ядре нет; «moved» — запись есть, но
    // она больше не позиция номенклатуры. Второе — не потерянная ссылка: счётчик её не считает.
    string? NomenclatureIssue,
    // Позиция в архиве (issue #1185): строка сведена и названа, а в поиске этой позиции больше нет.
    bool NomenclatureArchived,
    string? SupplierText,
    string? SupplierCode,
    string? Unit,
    decimal? Quantity,
    decimal? Price,
    decimal? VatRate,
    decimal? VatAmount,
    // Сумма строки С НДС — в обоих режимах счёта (issue #1497). Обратно в запрос её не присылают при НДС
    // сверху: там бумажная сумма — netAmount, а эта посчитана сервером.
    decimal? Amount,
    string? Note,
    /// <summary>Разноска строки по стройкам и остаток «не разнесено» (F1, issue #1085).</summary>
    LineAllocationView Allocation,
    // Пометка «(запомнено)» (issue #1079): позиция подставлена из соответствий поставщика. null —
    // позицию выбрал человек либо её нет.
    InvoiceLineMatchView? Match = null,
    // Сумма строки из бумаги, без НДС, — только у счёта с НДС сверху; при «в том числе» null (issue #1497).
    decimal? NetAmount = null);

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
/// <param name="Vat">Сумма НДС по строкам — то самое «в том числе НДС», посчитанное по строкам. Только по
/// строкам, где НДС указан: строки без него сюда не попадают — их НДС не ноль, а неизвестен.</param>
/// <param name="VatMissing">Сколько строк без НДС (issue #1409). Без этого числа <see cref="Vat" /> врёт:
/// у счёта, где НДС стоит лишь в итогах бумаги, строки о нём молчат, и сверка писала «в том числе НДС:
/// 0,00» рядом с шапкой, где НДС распознан верно, — неизвестное выдано за ноль ровно там, где человек
/// сверяет суммы. Равно <see cref="Count" /> — «НДС по строкам не указан», а не ноль.
///
/// <para>⚠️ «Не указан» — это пустая сумма НДС, а не нулевая. Строка со ставкой 0 («без НДС») даёт
/// сумму 0 и считается указанной; строка со ставкой и суммой, но без суммы НДС, получает его расчётом
/// (<see cref="InvoiceLineValues.Completed" />) ещё до записи — тоже указанной.</para></param>
/// <param name="Mode">Режим НДС счёта (issue #1497): от него зависит, как подписаны числа, — сами числа
/// одни. <see cref="Amount" /> — сумма строк С НДС в обоих режимах; у строки сверху без НДС суммы с НДС
/// нет, и в неё она не попадает (её видно по <see cref="VatMissing" />).</param>
/// <param name="Net">Сумма строк без НДС ИЗ БУМАГИ — только при НДС сверху: там она и есть бумага. При «в
/// том числе» <c>null</c>: сумма без НДС там производное, и полоса сверки её не показывает.</param>
/// <param name="VatFrom">Откуда сверке брать НДС: <c>lines</c> — он указан у каждой строки;
/// <c>requisites</c> — у строк его нет, есть «В том числе НДС» в шапке; <c>null</c> — НДС не указан нигде.
/// Число не подменяется: сервер называет источник, а сумму НДС шапки форма и так видит.</param>
public sealed record InvoiceLineTotals(
    int Count, int WithoutNomenclature, decimal Amount, decimal Vat, int VatMissing,
    VatMode Mode = VatMode.Included, decimal? Net = null, string? VatFrom = null)
{
    public static InvoiceLineTotals Of(Invoice invoice, IReadOnlyCollection<InvoiceLine> lines)
    {
        var missing = lines.Count(l => l.VatAmount is null);
        return new(
            lines.Count,
            lines.Count(l => l.NomenclatureId is null),
            lines.Sum(l => l.Amount ?? 0m),
            lines.Sum(l => l.VatAmount ?? 0m),
            missing,
            invoice.VatMode,
            invoice.VatMode == VatMode.OnTop ? lines.Sum(l => l.NetAmount ?? 0m) : null,
            lines.Count > 0 && missing == 0 ? "lines" : invoice.VatTotal is not null ? "requisites" : null);
    }
}

/// <summary>Разбор присланной строки — с отказами, которые называют поле подписью с экрана.</summary>
public static class InvoiceLineRequests
{
    private const string IdKey = "id";
    private const string AmountKey = "amount";
    private const string NetAmountKey = "netAmount";
    private const string NomenclatureKey = "nomenclature";

    /// <summary>
    /// Почему позиция — ссылка, а не наименование текстом (ТЗ COST-7). Главный сторож задачи C2, и
    /// сказан он человеку, а не только коду: отказ читают за формой.
    /// </summary>
    /// <summary>
    /// Что делать с ценой, у которой в бумаге доли копейки (бывает: цена за метр, посчитанная из цены
    /// за километр). Отказ без этого был бы тупиком — а выход есть: сумма строки принимается как
    /// прислана и по цене не пересчитывается.
    /// </summary>
    private const string PriceHint =
        "Если цена в бумаге именно такая — впишите её до копеек, а сумму строки возьмите из бумаги: " +
        "присланная сумма ложится как есть и по цене не пересчитывается.";

    private const string NomenclatureWhy =
        "Позицию выбирают из справочника номенклатуры, а не вписывают наименованием: без ссылки на " +
        "справочник нельзя ни свести затраты, ни связать материал с документом качества, ни сказать " +
        "монтажнику, что ему отпустили. Наименование из бумаги поставщика присылайте в «supplierText» — " +
        "оно останется цитатой и ключом сопоставления, а строка будет ждать позиции в отборе «Разобрать».";

    /// <summary>
    /// Идентификатор присланной строки: есть — правим её, нет — заводим новую.
    ///
    /// <para>⚠️ Вид строки проверяется и здесь, а не только в <see cref="Values" />: идентификатор
    /// читают ПЕРВЫМ, и отказ «строка N прислана как …», стоявший только там, был недостижим — раньше
    /// него приходил 500 (issue #1163).</para>
    /// </summary>
    public static Guid? Id(JsonElement line, int number)
    {
        CostsValues.EnsureObject(line, $"Строка {number}", "строки");

        return CostsValues.Value(line, IdKey) switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } value when Guid.TryParse(value.GetString(), out var id) => id,
            var other => throw CostsValues.Wrong($"Строка {number}: идентификатор", other,
                "строку-идентификатор уже сохранённой строки либо ничего"),
        };
    }

    /// <summary>
    /// Значения строки — разобранные, как в бумаге. Досчитывает их <see cref="Complete" /> — по режиму
    /// счёта, который известен под замком записи.
    /// </summary>
    /// <param name="number">Номер строки в присланном наборе, с единицы. Нужен отказам: «ожидается
    /// число» без номера строки заставило бы человека искать опечатку во всей таблице.</param>
    public static InvoiceLineValues Values(JsonElement line, int number)
    {
        CostsValues.EnsureObject(line, $"Строка {number}", "строки");

        var values = new InvoiceLineValues(
            NomenclatureId: CostsValues.Reference(line, NomenclatureKey, NomenclatureWhy,
                $"Позиция номенклатуры, строка {number}"),
            SupplierText: CostsValues.Text(line, "supplierText", $"Наименование в счёте, строка {number}"),
            SupplierCode: CostsValues.Text(line, "supplierCode", $"Артикул поставщика, строка {number}",
                InvoiceLine.SupplierCodeLength),
            Unit: CostsValues.Text(line, "unit", $"Единица измерения, строка {number}", InvoiceLine.UnitLength),
            Quantity: CostsValues.Quantity(line, "quantity", $"Количество, строка {number}"),
            Price: CostsValues.Money(line, "price", $"Цена, строка {number}", PriceHint),
            VatRate: Rate(line, number),
            VatAmount: CostsValues.Money(line, "vatAmount", $"Сумма НДС, строка {number}"),
            Amount: CostsValues.Money(line, AmountKey, $"Сумма, строка {number}"),
            Note: CostsValues.Text(line, "note", $"Примечание, строка {number}"),
            MatchedBy: MatchedBy(line, number),
            NetAmount: CostsValues.Money(line, NetAmountKey, $"Сумма без НДС, строка {number}"));

        if (values.MatchedBy is not null && values.NomenclatureId is null)
            throw new InvalidRequestException(
                $"Строка {number}: пометка «запомнено» прислана без позиции номенклатуры. Пометка говорит, " +
                "откуда позиция взялась, — без позиции ей говорить не о чем. Отмена подстановки снимает и " +
                "позицию, и пометку.");

        return values;
    }

    /// <summary>
    /// Досчитать разобранную строку по режиму счёта (issue #1497) — с отказами человеку.
    ///
    /// <para>⚠️ <b>Чужое поле суммы — отказ, а не догадка.</b> Ответ отдаёт <c>amount</c> С НДС в обоих
    /// режимах; прими запрос при НДС сверху <c>amount</c> за бумагу, круг «прочитал → отправил без правок»
    /// прибавлял бы НДС на каждом сохранении формы. Поэтому сумма строки из бумаги приходит полем своего
    /// режима: <c>amount</c> при «в том числе», <c>netAmount</c> при НДС сверху.</para>
    /// </summary>
    public static CompletedLine Complete(InvoiceLineValues values, int number, VatMode mode)
    {
        if (mode == VatMode.OnTop && values.Amount is not null)
            throw new InvalidRequestException(
                $"Строка {number}: при НДС сверху сумму строки из бумаги присылают полем «{NetAmountKey}», " +
                $"а «{AmountKey}» — сумма с НДС, её считает сервер. Принять её за бумагу значило бы прибавить " +
                "НДС ещё раз.");
        if (mode == VatMode.Included && values.NetAmount is not null)
            throw new InvalidRequestException(
                $"Строка {number}: у счёта с НДС «в том числе» сумму строки присылают полем «{AmountKey}»; " +
                $"«{NetAmountKey}» — сумма без НДС, она бывает только при НДС сверху. Если в бумаге цены без " +
                "НДС, переключите режим счёта — он едет в том же запросе полем «vatMode».");

        var completed = values.Completed(mode);

        // Присланные числа в границе, а ПОСЧИТАННАЯ сумма — уже нет: количество × цена у двух чисел
        // под триллион даёт до 10²⁴. В decimal оно помещается, в колонку — нет, и отказала бы база.
        foreach (var sum in new[] { completed.Values.Amount, completed.Values.NetAmount })
            if (sum is { } amount && Math.Abs(amount) >= CostsValues.Limit)
                throw new InvalidRequestException(
                    $"Строка {number}: количество × цена = {CostsValues.Shown(amount)} — " +
                    "больше, чем здесь бывает (предел — триллион). В одном из двух чисел лишние нули или " +
                    "склейка при вставке из буфера.");

        return completed;
    }

    /// <summary>
    /// Ставка НДС в процентах, 0…100.
    ///
    /// <para>Границы ловят «2000» и «−20» — опечатку и знак, которые дали бы сумму НДС больше суммы
    /// строки. ⚠️ Путаницу «0,2 вместо 20» они НЕ ловят и не могут: 0,2 — законная ставка по виду, и
    /// отличить её от доли нечем. Видна она человеку суммой НДС, которая посчитается в сто раз меньше и
    /// не сойдётся с «в том числе НДС» из бумаги — та сверка стоит в форме на виду ровно за этим.</para>
    /// </summary>
    /// <summary>
    /// Соответствие, из которого позиция подставлена, — пометка «(запомнено)» (issue #1079). Верна ли
    /// она, проверяет запись строк: здесь только разбор.
    /// </summary>
    private static Guid? MatchedBy(JsonElement line, int number) =>
        CostsValues.Value(line, "matchedBy") switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } value when Guid.TryParse(value.GetString(), out var id) => id,
            var other => throw CostsValues.Wrong($"Строка {number}: пометка «запомнено»", other,
                "строку-идентификатор соответствия либо ничего"),
        };

    /// <summary>
    /// Запоминать ли выбор позиции в этой строке. Умолчание — да (ТЗ COST-7.1: выбор запоминается);
    /// <c>false</c> — человек сказал «только в этой строке».
    /// </summary>
    public static bool Remember(JsonElement line, int number) =>
        CostsValues.Value(line, "remember") switch
        {
            null => true,
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            var other => throw CostsValues.Wrong($"Строка {number}: «запоминать»", other, "true либо false"),
        };

    private static decimal? Rate(JsonElement line, int number)
    {
        var label = $"Ставка НДС, строка {number}";
        return CostsValues.Money(line, "vatRate", label) switch
        {
            null => null,
            { } rate when rate is >= 0 and <= 100 => rate,
            { } rate => throw new InvalidRequestException(
                $"Поле «{label}»: «{rate}» — не ставка НДС. Ожидаются проценты от 0 до 100 (22, 10, 0): " +
                "при ставке вне этих границ сумма НДС вышла бы больше суммы строки."),
        };
    }
}
