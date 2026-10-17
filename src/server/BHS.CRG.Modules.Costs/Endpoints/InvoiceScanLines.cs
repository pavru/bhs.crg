using System.Globalization;
using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Строки скана → строки счёта (issue #1077, НДС строк — issue #1410).
///
/// <para>Отдельно от <see cref="InvoiceScanReading" /> затем, чтобы разбор проверялся без очереди и
/// движка: правил в нём много, и каждое — про то, чем «пусто», «ноль» и «не прочитано» отличаются друг
/// от друга.</para>
/// </summary>
public static class InvoiceScanLines
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Разобранные строки и, если НДС пришлось снять, — почему (в примечания распознавания).</summary>
    public sealed record Reading(IReadOnlyList<InvoiceLineValues> Lines, string? VatNote);

    /// <summary>
    /// Разобрать строки скана. НДС строк ложится, только если он есть у ВСЕХ строк (issue #1410).
    ///
    /// <para>⚠️ Почему «все или ни одной». Отчёты по затратам и книга берут НДС шапки, только когда
    /// строки об НДС молчат все (<see cref="CostInvoice.VatByLines" />); назван хоть у одной — НДС шапки
    /// считается разложенным, и строка без своего НДС идёт в отчёт полной суммой. Положи мы НДС у части
    /// строк, счёт перешёл бы на НДС по строкам и посчитался бы грубее, чем с шапкой, — ровно обратное
    /// тому, ради чего НДС строк читается. Сверка «сумма НДС строк ≈ НДС шапки» эту дыру не закрывает:
    /// она судит о сумме, а беда в строках, у которых НДС нет вовсе; и шапка бывает не прочитана.</para>
    ///
    /// <para>Снятое не теряется: прочитанное лежит в примечании строки, причина — в примечании
    /// распознавания. Человек впишет недостающее, и счёт перейдёт на НДС по строкам уже целиком.</para>
    /// </summary>
    public static Reading Read(IEnumerable<IReadOnlyDictionary<string, string?>> rows)
    {
        var lines = rows.Select(Line).OfType<InvoiceLineValues>().ToList();
        var named = lines.Count(l => l.VatAmount is not null);
        if (named == 0 || named == lines.Count) return new(lines, null);

        var stripped = lines.Select(Strip).ToList();
        return new(stripped,
            $"НДС прочитан не у всех строк (у {named} из {lines.Count}) — в строки не положен ни у одной: " +
            "отчёты берут НДС из шапки, только когда о нём молчат все строки. Прочитанное — в примечаниях " +
            "строк; впишите недостающее, если НДС нужен по строкам.");
    }

    /// <summary>Снять НДС со строки, оставив прочитанное в примечании.</summary>
    private static InvoiceLineValues Strip(InvoiceLineValues line)
    {
        if (line.VatRate is null && line.VatAmount is null) return line;

        var read = new List<string>();
        // Разбор отдаёт «22» как 22,0 — в примечании число пишется так, как его пишет бумага.
        if (line.VatRate is { } rate) read.Add($"ставка {rate.ToString("0.##", Russian)} %");
        if (line.VatAmount is { } vat) read.Add($"сумма {vat.ToString("0.00", Russian)}");
        var note = $"НДС в файле: {string.Join(", ", read)} — не положен, у других строк его нет";
        return line with
        {
            VatRate = null,
            VatAmount = null,
            Note = line.Note is null ? note : $"{line.Note}; {note}",
        };
    }

    /// <summary>
    /// Строка скана → строка счёта. Номенклатуры здесь нет: её подставит запомненное для поставщика
    /// (<see cref="SupplierMatching.RecallAsync" />) либо выберет человек — тогда строка ждёт в очереди
    /// «Разобрать».
    ///
    /// <para>⚠️ Пределы — те же, что у строки из формы (<see cref="InvoiceLineRequests" />): длина,
    /// «триллион», точность колонки. Форма на нарушение отвечает отказом; здесь отказывать некому, и
    /// значение, которое не помещается или не читается однозначно, в поле не попадает — уходит в
    /// примечание строки. Молча округлить или обрезать нельзя: записанное разошлось бы с бумагой, а
    /// отказ базы уронил бы всё распознавание вместе с шапкой.</para>
    /// </summary>
    private static InvoiceLineValues? Line(IReadOnlyDictionary<string, string?> row)
    {
        // Строка, в которой движок не прочитал ничего, — не строка счёта.
        if (row.Values.All(string.IsNullOrWhiteSpace)) return null;

        var unread = new List<string>();

        decimal? Number(string key, string title, Func<string?, decimal?> parse, int digits)
        {
            var text = row.GetValueOrDefault(key);
            if (string.IsNullOrWhiteSpace(text)) return null;

            if (parse(text) is { } value && Math.Abs(value) < CostsValues.Limit && decimal.Round(value, digits) == value)
                return value;

            unread.Add($"{title} «{Short(text)}»");
            return null;
        }

        string? Text(string key, string title, int limit)
        {
            var text = row.GetValueOrDefault(key);
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (text.Length <= limit) return text;

            unread.Add($"{title} «{Short(text)}» (длиннее {limit} знаков)");
            return null;
        }

        // Колонки НДС: прочерк — пустая ячейка, а не «не прочитано» и не ноль. «Без НДС» — ноль: строка
        // НДС назвала, и назвала нулём (issue #1410).
        decimal? Vat(string key, string title, Func<string?, decimal?> parse) =>
            Dash(row.GetValueOrDefault(key))
                ? null
                : Number(key, title, text => RecognizedValues.NoVat(text) ? 0m : parse(text), 2);

        var quantity = Number(CostsRecognitionProfiles.LineQuantity, "количество", RecognizedValues.Quantity, 3);
        // Цена — деньгами, а не количеством: «1.250» в графе цены бывает и тысячей двести пятьюдесятью.
        var price = Number(CostsRecognitionProfiles.LinePrice, "цена", RecognizedValues.Money, 2);
        var amount = Number(CostsRecognitionProfiles.LineAmount, "сумма", RecognizedValues.Money, 2);
        var rate = Vat(CostsRecognitionProfiles.LineVatRate, "ставка НДС", RecognizedValues.VatRate);
        var vat = Vat(CostsRecognitionProfiles.LineVatAmount, "сумма НДС", RecognizedValues.Money);
        var unit = Text(CostsRecognitionProfiles.LineUnit, "единица", InvoiceLine.UnitLength);

        if (amount is null && quantity is { } q && price is { } p && Math.Abs(q * p) >= CostsValues.Limit)
        {
            // Сумму досчитала бы строка сама — и получила бы число, которого колонка не вместит.
            unread.Add($"цена «{CostsValues.Shown(p)}» (количество × цена больше, чем здесь бывает)");
            price = null;
        }

        var values = new InvoiceLineValues(
            NomenclatureId: null,
            SupplierText: row.GetValueOrDefault(CostsRecognitionProfiles.LineName) is { } name && !string.IsNullOrWhiteSpace(name)
                ? name
                : null,
            SupplierCode: null,
            Unit: unit,
            Quantity: quantity, Price: price, VatRate: rate, VatAmount: vat, Amount: amount,
            Note: unread.Count > 0 ? "В файле не прочитано: " + string.Join(", ", unread) : null);

        // Досчитываем только то, чего в скане НЕТ: сумму — по количеству и цене, сумму НДС — по ставке.
        // Стояло, но не прочиталось — оставляем пустым: посчитанное выглядело бы прочитанным, а на бумаге
        // написано другое.
        var completed = values.Completed();
        return values with
        {
            Amount = string.IsNullOrWhiteSpace(row.GetValueOrDefault(CostsRecognitionProfiles.LineAmount))
                ? completed.Amount
                : amount,
            VatAmount = Dash(row.GetValueOrDefault(CostsRecognitionProfiles.LineVatAmount)) ? completed.VatAmount : vat,
        };
    }

    /// <summary>Пустая ячейка — в том числе прочерком: «-», «—» в таблице счёта значат «нет».</summary>
    private static bool Dash(string? text) =>
        string.IsNullOrWhiteSpace(text) || text.Trim() is "-" or "—" or "–";

    private static string Short(string text) => text.Length > 40 ? text[..40] + "…" : text;
}
