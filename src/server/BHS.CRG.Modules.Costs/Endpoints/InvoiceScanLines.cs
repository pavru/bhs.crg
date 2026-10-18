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
    /// Ключи колонок строки счёта: колонка профиля с таким ключом заполняет это поле строки (issue
    /// #1480). Значение — имя свойства <see cref="InvoiceLineValues" />.
    ///
    /// <para><b>Общее правило, а не перечисление заводских ключей</b> (решение владельца 18.10.2026).
    /// Профиль правит администратор, и раньше колонка, которой разбор не знал, читалась моделью и молча
    /// терялась: так на продакшене пропадал добавленный «Артикул». Теперь у каждого поля строки есть
    /// ключ, а колонка с другим ключом не теряется молча — она попадает в примечание строки (и остаётся
    /// в наборах данных, которые читают тот же профиль).</para>
    ///
    /// <para>Ключ сравнивается точно, после обрезки краевых пробелов: «артикул» со строчной — другой
    /// ключ, и это видно в примечании, а не угадывается.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Targets = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [CostsRecognitionProfiles.LineName] = nameof(InvoiceLineValues.SupplierText),
        [CostsRecognitionProfiles.LineCode] = nameof(InvoiceLineValues.SupplierCode),
        [CostsRecognitionProfiles.LineUnit] = nameof(InvoiceLineValues.Unit),
        [CostsRecognitionProfiles.LineQuantity] = nameof(InvoiceLineValues.Quantity),
        [CostsRecognitionProfiles.LinePrice] = nameof(InvoiceLineValues.Price),
        [CostsRecognitionProfiles.LineAmount] = nameof(InvoiceLineValues.Amount),
        [CostsRecognitionProfiles.LineVatRate] = nameof(InvoiceLineValues.VatRate),
        [CostsRecognitionProfiles.LineVatAmount] = nameof(InvoiceLineValues.VatAmount),
    };

    /// <summary>
    /// Поля строки, которые распознавание не заполняет, — и почему. Новое поле строки обязано попасть
    /// либо в <see cref="Targets" />, либо сюда: иначе краснеет сторож, а не теряется колонка.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NotTargets = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(InvoiceLineValues.NomenclatureId)] =
            "позицию подставляет запомненное для поставщика или выбирает человек — не бумага",
        [nameof(InvoiceLineValues.MatchedBy)] = "пометка подстановки из запомненного ставится сервером, а не читается",
        [nameof(InvoiceLineValues.Note)] = "примечание собирает сам разбор: что не прочиталось и что не легло",
    };

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
    /// <para>⚠️ «Есть НДС» — это сумма НДС, а не ставка: отчёты смотрят на сумму. Но и строка с одной
    /// ставкой (сумма НДС стояла и не прочиталась) тянет счёт туда же, только позже: первое сохранение
    /// формы досчитает ей НДС по ставке (<see cref="InvoiceLineValues.Completed" />), а строкам без
    /// ставки — нет. Поэтому снимается всё, если хоть у одной строки НДС ТРОНУТ, а полным — суммой —
    /// он есть не у всех (ревью PR #1444).</para>
    ///
    /// <para>Снятое не теряется: прочитанное лежит в примечании строки, причина — в примечании
    /// распознавания. Человек впишет недостающее, и счёт перейдёт на НДС по строкам уже целиком.</para>
    /// </summary>
    public static Reading Read(IEnumerable<IReadOnlyDictionary<string, string?>> rows)
    {
        var lines = rows.Select(Line).OfType<Scanned>().ToList();
        var named = lines.Count(l => l.Values.VatAmount is not null);
        var touched = lines.Any(l => l.Values.VatAmount is not null || l.Values.VatRate is not null);
        if (!touched || named == lines.Count) return new([.. lines.Select(l => l.Values)], null);

        return new([.. lines.Select(Strip)],
            $"НДС прочитан не у всех строк (у {named} из {lines.Count}) — в строки не положен ни у одной: " +
            "отчёты берут НДС из шапки, только когда о нём молчат все строки. Прочитанное — в примечаниях " +
            "строк; впишите недостающее, если НДС нужен по строкам.");
    }

    /// <summary>Строка и то, прочитана ли её сумма НДС в файле, — или досчитана по ставке.</summary>
    private sealed record Scanned(InvoiceLineValues Values, bool VatAmountRead);

    /// <summary>
    /// Снять НДС со строки, оставив в примечании ПРОЧИТАННОЕ. Досчитанная по ставке сумма туда не
    /// пишется: в файле её нет, а «НДС в файле: сумма 100,00» человек перенёс бы как число с бумаги.
    /// </summary>
    private static InvoiceLineValues Strip(Scanned scanned)
    {
        var line = scanned.Values;
        var read = new List<string>();
        // Разбор отдаёт «22» как 22,0 — в примечании число пишется так, как его пишет бумага.
        if (line.VatRate is { } rate) read.Add($"ставка {rate.ToString("0.##", Russian)} %");
        if (scanned.VatAmountRead && line.VatAmount is { } vat) read.Add($"сумма {vat.ToString("0.00", Russian)}");
        if (read.Count == 0) return line;

        var note = $"НДС в файле: {string.Join(", ", read)} — не положен: НДС прочитан не у всех строк";
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
    ///
    /// <para>Ячейки берутся по полю строки через <see cref="Targets" />, а не по ключам профиля: разбор
    /// знает, КАК читать количество или артикул, а каким ключом колонка названа — решает таблица.</para>
    /// </summary>
    private static Scanned? Line(IReadOnlyDictionary<string, string?> row)
    {
        // Строка, в которой движок не прочитал ничего, — не строка счёта.
        if (row.Values.All(string.IsNullOrWhiteSpace)) return null;

        var (cells, stray) = Cells(row);
        string? Cell(string field) => cells.GetValueOrDefault(field);

        var unread = new List<string>();

        decimal? Number(string field, string title, Func<string?, decimal?> parse, int digits)
        {
            var text = Cell(field);
            if (string.IsNullOrWhiteSpace(text)) return null;

            if (parse(text) is { } value && Math.Abs(value) < CostsValues.Limit && decimal.Round(value, digits) == value)
                return value;

            unread.Add($"{title} «{Short(text)}»");
            return null;
        }

        string? Text(string field, string title, int? limit = null)
        {
            var text = Cell(field);
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (limit is not { } max || text.Length <= max) return text;

            unread.Add($"{title} «{Short(text)}» (длиннее {limit} знаков)");
            return null;
        }

        // Колонки НДС: прочерк — пустая ячейка, а не «не прочитано» и не ноль. «Без НДС» — ноль: строка
        // НДС назвала, и назвала нулём (issue #1410). Ставку разбирает VatRate целиком, вместе с «Без НДС»;
        // у суммы НДС «Без НДС» понимается тем же правилом (RecognizedValues.NoVat) — одно место на оба.
        var rateEmpty = Empty(Cell(nameof(InvoiceLineValues.VatRate)));
        var vatEmpty = Empty(Cell(nameof(InvoiceLineValues.VatAmount)));

        var quantity = Number(nameof(InvoiceLineValues.Quantity), "количество", RecognizedValues.Quantity, 3);
        // Цена — деньгами, а не количеством: «1.250» в графе цены бывает и тысячей двести пятьюдесятью.
        var price = Number(nameof(InvoiceLineValues.Price), "цена", RecognizedValues.Money, 2);
        var amount = Number(nameof(InvoiceLineValues.Amount), "сумма", RecognizedValues.Money, 2);
        var rate = rateEmpty ? null : Number(nameof(InvoiceLineValues.VatRate), "ставка НДС", RecognizedValues.VatRate, 2);
        var vat = vatEmpty
            ? null
            : Number(nameof(InvoiceLineValues.VatAmount), "сумма НДС",
                text => RecognizedValues.NoVat(text) ? 0m : RecognizedValues.Money(text), 2);
        var unit = Text(nameof(InvoiceLineValues.Unit), "единица", InvoiceLine.UnitLength);
        // Длиннее колонки — в примечание, как непрочитанное число: отказ базы уронил бы всё распознавание.
        var code = Text(nameof(InvoiceLineValues.SupplierCode), "артикул", InvoiceLine.SupplierCodeLength);

        if (amount is null && quantity is { } q && price is { } p && Math.Abs(q * p) >= CostsValues.Limit)
        {
            // Сумму досчитала бы строка сама — и получила бы число, которого колонка не вместит.
            unread.Add($"цена «{CostsValues.Shown(p)}» (количество × цена больше, чем здесь бывает)");
            price = null;
        }

        string?[] notes =
        [
            unread.Count > 0 ? "В файле не прочитано: " + string.Join(", ", unread) : null,
            // Временная форма (issue #1480): задача #1484 переносит это в замечания распознавания счёта.
            stray.Count > 0 ? "Прочитано, в счёт не положено: " + string.Join(", ", stray) : null,
        ];
        var note = string.Join("; ", notes.OfType<string>());

        var values = new InvoiceLineValues(
            NomenclatureId: null,
            SupplierText: Text(nameof(InvoiceLineValues.SupplierText), "наименование"),
            SupplierCode: code,
            Unit: unit,
            Quantity: quantity, Price: price, VatRate: rate, VatAmount: vat, Amount: amount,
            Note: note.Length > 0 ? note : null);

        // Досчитываем только то, чего в скане НЕТ: сумму — по количеству и цене, сумму НДС — по ставке.
        // Стояло, но не прочиталось — оставляем пустым: посчитанное выглядело бы прочитанным, а на бумаге
        // написано другое.
        var total = string.IsNullOrWhiteSpace(Cell(nameof(InvoiceLineValues.Amount)))
            ? values.Completed().Amount
            : amount;
        values = values with { Amount = total };

        // ⚠️ НДС — только от суммы, которая ЕСТЬ у строки (ревью PR #1444). Не прочиталась сумма — нет и
        // НДС: Completed досчитал бы его от «количество × цена», и у строки без суммы стоял бы НДС,
        // посчитанный из числа, которого никто не видел.
        if (vatEmpty && total is not null) values = values with { VatAmount = values.Completed().VatAmount };

        return new(values, VatAmountRead: !vatEmpty && vat is not null);
    }

    /// <summary>
    /// Ячейки строки по полям (<see cref="Targets" />) и колонки, у которых поля в счёте нет, — для
    /// примечания: «ключ «значение»». Пустая колонка без поля не называется: ей нечего терять.
    ///
    /// <para>Два ключа одного поля после обрезки пробелов («Цена» и «Цена ») — берётся непустой: модель
    /// отвечает по ключам профиля, и пустой дубль иначе мог бы затереть прочитанное.</para>
    /// </summary>
    private static (Dictionary<string, string?> Cells, List<string> Stray) Cells(IReadOnlyDictionary<string, string?> row)
    {
        var cells = new Dictionary<string, string?>(StringComparer.Ordinal);
        var stray = new List<string>();
        foreach (var (key, text) in row)
        {
            var bare = key.Trim();
            if (Targets.TryGetValue(bare, out var field))
            {
                if (string.IsNullOrWhiteSpace(cells.GetValueOrDefault(field))) cells[field] = text;
            }
            else if (!string.IsNullOrWhiteSpace(text))
                stray.Add($"{bare} «{Short(text)}»");
        }
        return (cells, stray);
    }

    /// <summary>
    /// Пустая ячейка — в том числе прочерком («-», «--», «—») или крестом «Х»: так в таблицах счетов и
    /// УПД пишут «здесь нечего указать». Иначе прочерк считался бы непрочитанным значением, и правило
    /// «все или ни одной» снимало бы из-за одной такой ячейки НДС со всего счёта.
    /// </summary>
    private static bool Empty(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var bare = text.Trim();
        return bare.All(c => c is '-' or '—' or '–') || bare is "Х" or "х" or "X" or "x";
    }

    private static string Short(string text) => text.Length > 40 ? text[..40] + "…" : text;
}
