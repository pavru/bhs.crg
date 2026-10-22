using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Recognition;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Шапка скана → поля счёта (issue #1483): правило «ключ поля профиля = ключ поля счёта».
///
/// <para>Куда ведёт каждое поле профиля, решает ядро (<c>IModuleRecognitionTargets</c>): объявленное
/// модулем (<see cref="CostsRecognitionProfiles.InvoiceTargets" />) плюс поля, которые заказчик дописал в
/// тип. Здесь — разбор значений и что из прочитанного не ляжет (<see cref="UnplacedRead" />). Сами поля счёта, слияние в пустые и охрана —
/// в <see cref="InvoiceScanReading" />.</para>
///
/// <para>Отдельно — затем же, зачем <see cref="InvoiceScanLines" />: правил много, и каждое проверяется
/// без очереди и движка.</para>
/// </summary>
public static class InvoiceScanHeader
{
    /// <summary>
    /// Разбор полей МОДУЛЯ по ключу поля счёта. Таблица и есть код: сторож сверяет её ключи с целями-полями
    /// объявления — «объявили, но не раскладываем» невозможно. <c>null</c> из разбора — значение не
    /// прочитано однозначно: оно уйдёт в предложение с замечанием, а не в поле.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Func<string, JsonNode?>> Parsers =
        new Dictionary<string, Func<string, JsonNode?>>(StringComparer.Ordinal)
        {
            [InvoiceRequisites.NumberKey] = text => text.Length <= Invoice.NumberLength ? JsonValue.Create(text) : null,
            [InvoiceRequisites.DateKey] = DateNode,
            [InvoiceRequisites.BasisKey] = TextNode,
            [InvoiceRequisites.PurposeKey] = TextNode,
            [InvoiceRequisites.TotalKey] = MoneyNode,
            [InvoiceRequisites.VatTotalKey] = MoneyNode,
            [InvoiceRequisites.ShippedOnKey] = DateNode,
        };

    /// <summary>
    /// Разбор поля, которое заказчик дописал в тип, — по виду поля, строго. Деньги это или метры, разбор
    /// не знает, поэтому число — <see cref="RecognizedValues.Decimal" />, без угадывания разрядов.
    /// Ограничения поля (шаблон, длина, границы) проверяет охрана записи, а не разбор.
    /// </summary>
    public static JsonNode? ByType(string type, string text) => type switch
    {
        "string" or "text" => TextNode(text),
        "number" => RecognizedValues.Decimal(text) is { } number ? JsonValue.Create(number) : null,
        "date" => DateNode(text),
        _ => null,
    };

    /// <summary>Прочитанное для поля счёта.</summary>
    /// <param name="Key">Ключ поля счёта.</param>
    /// <param name="ProfileKey">Ключ поля профиля, из которого прочитано (у прежнего ключа — он).</param>
    /// <param name="Value">Разобранное значение; <c>null</c> — не разобрано.</param>
    public sealed record Candidate(string Key, string Title, string ProfileKey, string Text, JsonNode? Value);

    /// <summary>Что ложится в поля и что прочитано, но в поля не ляжет (issue #1484).</summary>
    public sealed record Plan(IReadOnlyList<Candidate> Candidates, IReadOnlyList<UnplacedRead> Unplaced);

    /// <param name="fields">Прочитанное по ключам профиля; <c>null</c> — спросили, в документе не нашлось.</param>
    /// <param name="targets">Цели ключей от ядра; ключа нет или <c>null</c> — у счёта такого поля нет.</param>
    public static Plan Of(
        IReadOnlyDictionary<string, string?> fields, IReadOnlyDictionary<string, ModuleFieldTarget?> targets)
    {
        var candidates = new List<Candidate>();
        var unplaced = new List<UnplacedRead>();

        foreach (var (key, text) in fields)
        {
            // Спросили — не нашли: не новость, говорить не о чем.
            if (string.IsNullOrWhiteSpace(text)) continue;
            switch (targets.GetValueOrDefault(key))
            {
                case null:
                    unplaced.Add(InvoiceScanUnplaced.InHeader(key, text, InvoiceScanUnplaced.NoField));
                    break;
                // Прочитанное для сопоставления сторон (название, ИНН) использовано — не «не положено».
                case { Origin: ModuleTargetOrigin.Input }:
                    break;
                case { Unavailable: { } why } target:
                    unplaced.Add(InvoiceScanUnplaced.InHeader(key, text, InvoiceScanUnplaced.NotRecognizable, why,
                        target.Title ?? target.Key));
                    break;
                case var target:
                    var value = target.Origin == ModuleTargetOrigin.Module && Parsers.TryGetValue(target.Key, out var parse)
                        ? parse(text)
                        : ByType(target.Type, text);
                    candidates.Add(new(target.Key, target.Title ?? target.Key, key, text, value));
                    break;
            }
        }

        var chosen = new List<Candidate>();
        // Одно поле — одно значение: точный ключ старше прежнего (НомерСчёта → Номер). Второе прочитанное
        // не теряется молча — оно названо, человек сверит с файлом.
        foreach (var group in candidates.GroupBy(c => c.Key, StringComparer.Ordinal))
        {
            var ordered = group.OrderByDescending(c => c.ProfileKey == c.Key).ToList();
            chosen.Add(ordered[0]);
            foreach (var other in ordered.Skip(1))
                unplaced.Add(InvoiceScanUnplaced.InHeader(other.ProfileKey, other.Text,
                    InvoiceScanUnplaced.DuplicateKey, ordered[0].ProfileKey, other.Title));
        }

        return new(chosen, unplaced);
    }

    /// <summary>
    /// Предложение под полем (issue #1483): прочитанный текст и разобранное значение. «Взять» кладёт
    /// значение — второго разборщика на клиенте нет. Не разобрано — только текст: взять нечего.
    /// </summary>
    public static JsonObject Offer(string text, JsonNode? value)
    {
        var offer = new JsonObject { ["text"] = text };
        if (value is not null) offer["value"] = value.DeepClone();
        return offer;
    }

    /// <summary>
    /// Предложения, как их отдаёт вид распознавания (ревью PR #1523). Записанные до #1483 лежат строкой —
    /// текстом скана без разобранного значения, и «Взять» у них пропало бы. Разбираются здесь, тем же
    /// разбором, что при распознавании: ключи у них — поля модуля (номер, дата, суммы; «Назначение» — куда
    /// тогда ложилось основание). Второго разборщика на клиенте так и нет.
    /// </summary>
    public static JsonElement? Shown(JsonElement? stored)
    {
        if (stored is not { ValueKind: JsonValueKind.Object } offers
            || !offers.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.String))
            return stored;

        var result = new JsonObject();
        foreach (var offer in offers.EnumerateObject())
            result[offer.Name] = offer.Value.ValueKind == JsonValueKind.String && offer.Value.GetString() is { } text
                ? Offer(text, Parsers.TryGetValue(offer.Name, out var parse) ? parse(text) : null)
                : JsonNode.Parse(offer.Value.GetRawText());
        return JsonSerializer.SerializeToElement(result);
    }

    private static JsonNode TextNode(string text) => JsonValue.Create(text);

    private static JsonNode? DateNode(string text) => RecognizedValues.Date(text) is { } date
        ? JsonValue.Create(date.ToString(CostsValues.DateFormat, CultureInfo.InvariantCulture))
        : null;

    private static JsonNode? MoneyNode(string text) =>
        RecognizedValues.Money(text) is { } money ? JsonValue.Create(money) : null;
}
