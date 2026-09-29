using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Перекладывание счёта между реквизитами по схеме типа и колонками таблицы модуля (задача C1,
/// issue #1076, ТЗ CORE-15, CORE-20.2).
///
/// <para><b>Зачем это вообще есть.</b> Запись модуля снаружи выглядит как обычный документ: её
/// рисует общая форма по схеме типа, её печатает общий механизм, её поля находит реестр тэгов. А
/// внутри половина полей — колонки, потому что на них опирается код. Значит кто-то обязан переводить
/// одно в другое, и лучше в одном месте, чем в каждом обработчике.</para>
///
/// <para>⚠️ <b>Главное правило: ключ, за которым стоит колонка, в <c>Data</c> не попадает
/// никогда.</b> Иначе у значения два источника, и расходятся они молча — форма покажет одно, отбор
/// реестра найдёт другое, и виноватым будет выглядеть отбор. Правило держится здесь, в
/// <see cref="Split" />, и проверяется сторожем.</para>
///
/// <para>⚠️ <b>Двух словарей у состояний не избежать</b> (<c>Draft</c> в базе, «Черновик» в
/// реквизитах): в базе нужен устойчивый код, в форме — слово для человека, а вид поля в объявлении
/// модуля может быть только <c>string</c> — перечисление объявить в коде нельзя, оно адресует тип по
/// идентификатору. Перевод стоит ровно здесь, и другого места у него нет.</para>
/// </summary>
public static class InvoiceRequisites
{
    public const string NumberKey = "Номер";
    public const string DateKey = "Дата";
    public const string SupplierKey = "Поставщик";
    public const string PayerKey = "Плательщик";
    public const string PurposeKey = "Назначение";
    public const string TotalKey = "Итого";
    public const string VatTotalKey = "ВТомЧислеНДС";
    public const string ShippedOnKey = "ДатаОтгрузки";
    public const string DeferralKey = "Отсрочка";
    public const string DueDateKey = "Срок";
    public const string StateKey = "Состояние";
    public const string PaymentKey = "СостояниеОплаты";
    public const string ScanKey = "Скан";

    /// <summary>Ключи, которые человек присылает и которые ложатся в колонки.</summary>
    public static readonly IReadOnlySet<string> WritableColumnKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        NumberKey, DateKey, SupplierKey, PayerKey, PurposeKey,
        TotalKey, VatTotalKey, ShippedOnKey, DeferralKey, DueDateKey,
    };

    /// <summary>
    /// Ключи, за которыми стоит колонка, но которые формой НЕ присылаются: состояния двигают
    /// действия, скан приезжает своим адресом. С объяснением у каждого — отказ обязан говорить, где
    /// же это делается.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ReadOnlyColumnKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [StateKey] = "состояние документа двигают действия («разобрать», «отклонить»), а не форма",
            [PaymentKey] = "состояние оплаты ставит отметка платежа",
            [ScanKey] = "скан прикладывается своим адресом — «/scan», файлом, а не значением поля",
        };

    /// <summary>Все ключи, за которыми стоит колонка.</summary>
    public static IReadOnlySet<string> ColumnKeys { get; } =
        new HashSet<string>(WritableColumnKeys.Concat(ReadOnlyColumnKeys.Keys), StringComparer.Ordinal);

    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Разобрать присланные реквизиты: колонки — отдельно, остаток схемы — отдельно.
    ///
    /// <para>Отказ (400), а не тихое приведение: значение не того вида означает, что клиент и сервер
    /// разошлись в понимании поля, и молча записанный <c>null</c> выглядел бы как «человек стёр
    /// сумму».</para>
    /// </summary>
    public static (InvoiceColumns Columns, JsonDocument Extra) Split(JsonElement requisites)
    {
        if (requisites.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException(
                "Реквизиты счёта — объект «ключ поля: значение». Пришло: " +
                $"{requisites.ValueKind}. Схему полей задаёт тип «{CostsRecordTypes.InvoiceCode}».");

        foreach (var (key, why) in ReadOnlyColumnKeys)
            if (requisites.TryGetProperty(key, out _))
                throw new InvalidRequestException(
                    $"Поле «{key}» правкой счёта не задаётся: {why}. Уберите его из реквизитов — " +
                    "иначе непонятно, чего ждать: молча пропущенное значение выглядело бы записанным.");

        var columns = new InvoiceColumns(
            Number: Text(requisites, NumberKey),
            IssuedOn: Date(requisites, DateKey),
            SupplierId: Reference(requisites, SupplierKey),
            PayerId: Reference(requisites, PayerKey),
            Purpose: Text(requisites, PurposeKey),
            Total: Money(requisites, TotalKey),
            VatTotal: Money(requisites, VatTotalKey),
            ShippedOn: Date(requisites, ShippedOnKey),
            DeferralDays: Days(requisites, DeferralKey),
            DueDate: Date(requisites, DueDateKey));

        var rest = new JsonObject();
        foreach (var property in requisites.EnumerateObject())
            if (!ColumnKeys.Contains(property.Name))
                rest[property.Name] = JsonNode.Parse(property.Value.GetRawText());

        return (columns, JsonDocument.Parse(rest.ToJsonString()));
    }

    /// <summary>
    /// Собрать реквизиты записи: колонки плюс остаток схемы. Это то, что видит форма, печать и
    /// реестр тэгов — то есть счёт снаружи выглядит обычным документом.
    /// </summary>
    public static JsonObject Merge(Invoice invoice)
    {
        var result = new JsonObject();

        // Сначала остаток схемы, потом колонки: при столкновении ключей побеждает колонка. Столкнуться
        // они не должны (за этим следит Split), но если в базе лежит запись, сделанная до этого
        // правила, спорить о значении будет нечем — верно то, что в колонке.
        foreach (var property in invoice.Data.RootElement.EnumerateObject())
            result[property.Name] = JsonNode.Parse(property.Value.GetRawText());

        Put(result, NumberKey, invoice.Number is { } number ? JsonValue.Create(number) : null);
        Put(result, DateKey, DateNode(invoice.IssuedOn));
        Put(result, SupplierKey, ReferenceNode(invoice.SupplierId));
        Put(result, PayerKey, ReferenceNode(invoice.PayerId));
        Put(result, PurposeKey, invoice.Purpose is { } purpose ? JsonValue.Create(purpose) : null);
        Put(result, TotalKey, invoice.Total is { } total ? JsonValue.Create(total) : null);
        Put(result, VatTotalKey, invoice.VatTotal is { } vat ? JsonValue.Create(vat) : null);
        Put(result, ShippedOnKey, DateNode(invoice.ShippedOn));
        Put(result, DeferralKey, invoice.DeferralDays is { } days ? JsonValue.Create(days) : null);
        Put(result, DueDateKey, DateNode(invoice.DueDate));
        Put(result, StateKey, JsonValue.Create(Label(invoice.State)));
        Put(result, PaymentKey, JsonValue.Create(Label(invoice.Payment)));
        Put(result, ScanKey, ScanNode(invoice));

        return result;
    }

    /// <summary>
    /// Запись, какой она СТАНЕТ после правки: присланные поля плюс те, что ведёт код (состояния,
    /// скан) — из того, что лежит.
    ///
    /// <para>Нужно охране записи ядра, и нужно именно так. Охрана спрашивает «можно ли записи стать
    /// такой», сравнивая присланное с лежащим, и отсутствующее запертое поле читает как <b>стёртое</b>:
    /// «значение стёрто — верните его в запись как есть». Передай мы ей одну присланную часть, правка
    /// счёта отказывала бы ВСЕГДА, потому что состояния форма не присылает и присылать не должна.</para>
    ///
    /// <para>⚠️ При СОЗДАНИИ так делать нельзя: там лежащего нет, и всё присланное считается внесённым
    /// впервые — а запертое поле нельзя заполнить даже впервые. Поэтому при создании охране уходит
    /// присланное как есть, а состояния код кладёт сам, после проверки.</para>
    /// </summary>
    public static JsonObject Resulting(JsonElement incoming, JsonObject stored)
    {
        var result = new JsonObject();

        foreach (var property in incoming.EnumerateObject())
            result[property.Name] = JsonNode.Parse(property.Value.GetRawText());

        foreach (var key in ReadOnlyColumnKeys.Keys)
            if (stored.TryGetPropertyValue(key, out var value))
                result[key] = value?.DeepClone();

        return result;
    }

    /// <summary>
    /// Какие поля изменились — по ним снимаются метки «распознано, не подтверждено» (решение
    /// владельца 29.09.2026).
    ///
    /// <para>Сравнивается то, что лежало, с тем, что пришло, — а не «пришло ли поле вообще». Форма
    /// присылает счёт целиком, поэтому «пришло» верно для всех полей сразу, и метка снималась бы с
    /// каждого при первом же сохранении. А сохранение метку снимать не должно: черновик сохраняет и
    /// фоновая загрузка, до человека (ТЗ COST-6.2, <c>D4</c>).</para>
    ///
    /// <para>Сравнение значений, а не текста JSON: <c>100</c> и <c>100.00</c> — одно и то же число, и
    /// «правка», которой не было, снимала бы метку с нетронутого поля.</para>
    /// </summary>
    public static IReadOnlyList<string> Changed(JsonObject before, JsonElement after)
    {
        var changed = new List<string>();

        foreach (var property in after.EnumerateObject())
        {
            if (ReadOnlyColumnKeys.ContainsKey(property.Name)) continue;

            var was = before.TryGetPropertyValue(property.Name, out var node) ? node : null;
            var now = JsonNode.Parse(property.Value.GetRawText());
            if (!JsonNode.DeepEquals(was, now)) changed.Add(property.Name);
        }

        // Поле, которое БЫЛО и в правке не пришло, — стёртое значение: это тоже правка.
        foreach (var (key, was) in before)
            if (was is not null && !ColumnKeys.Contains(key) && !after.TryGetProperty(key, out _))
                changed.Add(key);

        foreach (var key in WritableColumnKeys)
            if (before.TryGetPropertyValue(key, out var was) && was is not null
                && !after.TryGetProperty(key, out _) && !changed.Contains(key))
                changed.Add(key);

        return changed;
    }

    public static string Label(InvoiceState state) => state switch
    {
        InvoiceState.Draft => "Черновик",
        InvoiceState.Parsed => "Разобран",
        InvoiceState.Rejected => "Отклонён",
        _ => throw new InvalidRequestException($"Неизвестное состояние счёта «{state}»."),
    };

    public static string Label(InvoicePaymentState payment) => payment switch
    {
        InvoicePaymentState.Unpaid => "Не оплачен",
        InvoicePaymentState.Partial => "Частично оплачен",
        InvoicePaymentState.Paid => "Оплачен",
        _ => throw new InvalidRequestException($"Неизвестное состояние оплаты «{payment}»."),
    };

    private static void Put(JsonObject target, string key, JsonNode? value)
    {
        // Колонка без значения — ключ с null, а не отсутствующий ключ. Разница видна человеку: поле
        // рисуется пустым, а не исчезает из формы, и «осталось дней: —» отличимо от «поля нет».
        target[key] = value;
    }

    private static JsonNode? DateNode(DateOnly? date) =>
        date is { } value ? JsonValue.Create(value.ToString(DateFormat, CultureInfo.InvariantCulture)) : null;

    private static JsonNode? ReferenceNode(Guid? id) => id is { } value
        ? new JsonObject { ["$ref"] = "catalog", ["entryId"] = value.ToString() }
        : null;

    private static JsonNode? ScanNode(Invoice invoice) => invoice.ScanBlobPath is { } path
        ? new JsonObject
        {
            ["$type"] = "file",
            ["blobPath"] = path,
            ["fileName"] = invoice.ScanFileName,
            ["mimeType"] = invoice.ScanMimeType,
        }
        : null;

    private static string? Text(JsonElement requisites, string key) => Value(requisites, key) switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } value => value.GetString() is { Length: > 0 } text ? text : null,
        var other => throw Wrong(key, other, "строку"),
    };

    private static DateOnly? Date(JsonElement requisites, string key) => Value(requisites, key) switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } value =>
            value.GetString() is { Length: > 0 } text
                ? DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out var date)
                    ? date
                    : throw new InvalidRequestException(
                        $"Поле «{key}»: дата «{text}» не разобрана. Ожидается «{DateFormat}» — так её " +
                        "хранят все даты системы, и так её присылает форма.")
                : null,
        var other => throw Wrong(key, other, "дату строкой «" + DateFormat + "»"),
    };

    private static decimal? Money(JsonElement requisites, string key) => Value(requisites, key) switch
    {
        null => null,
        { ValueKind: JsonValueKind.Number } value => value.GetDecimal(),
        // Строку принимаем: числа приходят строками и из распознавания, и из вставки из буфера, а
        // отказ на «1 234,56» человек прочтёт как «система не понимает сумм».
        { ValueKind: JsonValueKind.String } value => Parse(key, value.GetString()),
        var other => throw Wrong(key, other, "число"),
    };

    private static decimal? Parse(string key, string? text)
    {
        if (text is not { Length: > 0 }) return null;

        var normalized = text.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(',', '.');

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidRequestException(
                $"Поле «{key}»: «{text}» — не число. Разделителем дробной части понимается и точка, и " +
                "запятая, пробелы внутри числа не мешают; всё остальное разобрать нечем.");
    }

    private static int? Days(JsonElement requisites, string key) => Money(requisites, key) switch
    {
        null => null,
        { } value when value == decimal.Truncate(value) && value is >= 0 and < 3651 => (int)value,
        { } value => throw new InvalidRequestException(
            $"Поле «{key}»: «{value}» — не срок в днях. Ожидается целое число от 0 до 3650 " +
            "(десять лет): отсрочка в полдня и отсрочка в век — это опечатка, а не условие поставщика."),
    };

    private static Guid? Reference(JsonElement requisites, string key)
    {
        if (Value(requisites, key) is not { } value) return null;

        if (value.ValueKind != JsonValueKind.Object)
            throw Wrong(key, value, "ссылку на запись справочника {\"$ref\":\"catalog\",\"entryId\":\"…\"}");

        if (!value.TryGetProperty("entryId", out var entry) || !Guid.TryParse(entry.GetString(), out var id))
            throw new InvalidRequestException(
                $"Поле «{key}»: в ссылке нет «entryId» с идентификатором записи справочника. " +
                "Организацию выбирают из справочника ядра, а не вписывают названием: по записи " +
                "справочника счёт находит ИНН, а по нему — сопоставление поставщика.");

        return id;
    }

    private static JsonElement? Value(JsonElement requisites, string key) =>
        requisites.TryGetProperty(key, out var value) && value.ValueKind is not JsonValueKind.Null
            ? value
            : null;

    private static InvalidRequestException Wrong(string key, JsonElement? value, string expected) =>
        new($"Поле «{key}»: ожидается {expected}, пришло {value?.ValueKind.ToString() ?? "ничего"}.");
}
