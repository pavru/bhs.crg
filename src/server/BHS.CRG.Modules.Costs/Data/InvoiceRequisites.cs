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
            [ScanKey] = "файл счёта прикладывается своим адресом — «/scan», файлом, а не значением поля",
        };

    /// <summary>Все ключи, за которыми стоит колонка.</summary>
    public static IReadOnlySet<string> ColumnKeys { get; } =
        new HashSet<string>(WritableColumnKeys.Concat(ReadOnlyColumnKeys.Keys), StringComparer.Ordinal);

    /// <summary>
    /// Почему организация — ссылка, а не название текстом. Одной строкой на оба поля: причина у них
    /// одна, а разойдись формулировки — человек решил бы, что правила у поставщика и плательщика разные.
    /// </summary>
    private const string OrganizationWhy =
        "Организацию выбирают из справочника ядра, а не вписывают названием: по записи справочника " +
        "счёт находит ИНН, а по нему — сопоставление поставщика.";

    /// <summary>
    /// Разобрать присланные реквизиты: колонки — отдельно, остаток схемы — отдельно.
    ///
    /// <para>Отказ (400), а не тихое приведение: значение не того вида означает, что клиент и сервер
    /// разошлись в понимании поля, и молча записанный <c>null</c> выглядел бы как «человек стёр
    /// сумму».</para>
    /// </summary>
    /// <param name="stored">
    /// Реквизиты, КАК ОНИ ЛЕЖАТ (<see cref="Merge" />), или <c>null</c> при создании. Нужны затем,
    /// чтобы у полей, которые ведёт код, правило было ТЕМ ЖЕ, что у охраны записи ядра: присылать
    /// можно, менять нельзя.
    ///
    /// <para>⚠️ Требовать, чтобы их не присылали вовсе, нельзя. Запись модуля рисует общая форма по
    /// схеме типа и возвращает её целиком — ровно то, что отдал <see cref="Merge" />, вместе с
    /// состояниями и сканом. Прежнее правило на таком запросе отказывало ВСЕГДА: круг «прочитать →
    /// поправить → сохранить» не проходил ни разу, а в тестах это было не видно, потому что помощник
    /// вырезал те же три ключа перед каждой правкой.</para>
    /// </param>
    public static (InvoiceColumns Columns, JsonDocument Extra) Split(JsonElement requisites, JsonObject? stored)
    {
        if (requisites.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException(
                "Реквизиты счёта — объект «ключ поля: значение». Пришло: " +
                $"{requisites.ValueKind}. Схему полей задаёт тип «{CostsRecordTypes.InvoiceCode}».");

        foreach (var (key, why) in ReadOnlyColumnKeys)
        {
            if (!requisites.TryGetProperty(key, out var sent)) continue;

            // Сравнение ЗНАЧЕНИЙ, как у охраны ядра: присланное «как лежит» — не правка, и отказывать
            // на нём нечему. При создании лежащего нет, поэтому пройдёт только пустое значение — и
            // это верно: запертое поле нельзя заполнить даже впервые.
            var now = JsonNode.Parse(sent.GetRawText());
            var was = stored is not null && stored.TryGetPropertyValue(key, out var value) ? value : null;
            if (JsonNode.DeepEquals(was, now)) continue;

            throw new InvalidRequestException(
                $"Поле «{key}» правкой счёта не меняется: {why}. Пришлите его значение как есть или " +
                "не присылайте вовсе — молча пропущенная правка выглядела бы записанной.");
        }

        var columns = new InvoiceColumns(
            Number: CostsValues.Text(requisites, NumberKey, limit: Invoice.NumberLength),
            IssuedOn: CostsValues.Date(requisites, DateKey),
            SupplierId: CostsValues.Reference(requisites, SupplierKey, OrganizationWhy),
            PayerId: CostsValues.Reference(requisites, PayerKey, OrganizationWhy),
            Purpose: CostsValues.Text(requisites, PurposeKey),
            Total: CostsValues.Money(requisites, TotalKey),
            VatTotal: CostsValues.Money(requisites, VatTotalKey),
            ShippedOn: CostsValues.Date(requisites, ShippedOnKey),
            DeferralDays: CostsValues.Days(requisites, DeferralKey),
            DueDate: CostsValues.Date(requisites, DueDateKey));

        var rest = new JsonObject();
        foreach (var property in requisites.EnumerateObject())
            if (!ColumnKeys.Contains(property.Name))
                rest[property.Name] = JsonNode.Parse(property.Value.GetRawText());

        return (columns, JsonDocument.Parse(rest.ToJsonString()));
    }

    /// <summary>
    /// Незаполненные ОБЯЗАТЕЛЬНЫЕ поля счёта — подписями, как они стоят в форме (issue #1078).
    ///
    /// <para>Обязательность проверяется на переходе «черновик → разобран» и при печати, а НЕ при
    /// сохранении (ТЗ COST-6.2): черновик без плательщика и без суммы — штатное состояние счёта,
    /// который приехал сканом и ждёт человека. Здесь — тот самый переход.</para>
    ///
    /// <para>⚠️ Перечень берётся из ОБЪЯВЛЕНИЯ типа (<c>CostsRecordTypes.Invoice</c>), а не переписан
    /// здесь списком. Перепиши — и добавленное в объявление обязательное поле молча перестало бы
    /// требоваться: тип показывал бы звёздочку, а переход бы её не замечал.</para>
    /// </summary>
    public static IReadOnlyList<string> Missing(Invoice invoice)
    {
        var requisites = Merge(invoice);

        return [.. CostsRecordTypes.Invoice.Fields
            .Where(f => f.Required && IsBlank(requisites[f.Key]))
            .Select(f => f.Title)];
    }

    /// <summary>Пусто ли значение: нет вовсе, пустая строка или пробелы.</summary>
    private static bool IsBlank(JsonNode? value) =>
        value is null || (value is JsonValue text && text.TryGetValue(out string? s)
            && string.IsNullOrWhiteSpace(s));

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
        date is { } value ? JsonValue.Create(value.ToString(CostsValues.DateFormat, CultureInfo.InvariantCulture)) : null;

    internal static JsonNode? ReferenceNode(Guid? id) => id is { } value
        ? new JsonObject { ["$ref"] = "catalog", ["entryId"] = value.ToString() }
        : null;

    /// <summary>
    /// Скан — значением файлового поля общего контракта. ⚠️ <c>size</c> в нём обязателен: форма
    /// показывает размер вложения рядом с именем и в свой признак «это файл» его не включает —
    /// значение без размера рисуется как «NaN ГБ», а не как файл без размера.
    /// </summary>
    private static JsonNode? ScanNode(Invoice invoice) => invoice.ScanBlobPath is { } path
        ? new JsonObject
        {
            ["$type"] = "file",
            ["blobPath"] = path,
            ["fileName"] = invoice.ScanFileName,
            ["mimeType"] = invoice.ScanMimeType,
            ["size"] = JsonValue.Create(invoice.ScanSize),
        }
        : null;
}
