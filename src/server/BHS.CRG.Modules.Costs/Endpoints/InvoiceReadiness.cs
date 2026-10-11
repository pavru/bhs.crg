using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Costs.Data;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Одно, чего счёту не хватает до «Разобран», — пунктом строки готовности.</summary>
/// <param name="Kind">К какому месту формы пункт ведёт: <c>field</c> — обязательное поле шапки,
/// <c>lines</c> — строк нет, <c>nomenclature</c> — строки ждут позиции, <c>reference</c> — организации
/// шапки нет в справочнике, <c>position</c> — позиции строки нет в справочнике, <c>catalog</c> —
/// справочника номенклатуры нет в установке, <c>allocation</c> — разноска, <c>total</c> — сумма строк
/// расходится с суммой к оплате.</param>
/// <param name="Label">Пункт словами, как он стоит в строке: «плательщик», «3 строки без позиции».</param>
/// <param name="Field">Ключ поля шапки — у <c>field</c>, <c>reference</c> и <c>total</c>.</param>
/// <param name="Lines">Номера строк — у <c>nomenclature</c>, <c>position</c> и у <c>allocation</c>, когда
/// не разнесены конкретные строки. По ним форма ведёт к строке, а не к таблице целиком.</param>
public sealed record InvoiceReadinessGap(
    string Kind, string Label, string? Field = null, IReadOnlyList<int>? Lines = null);

/// <summary>
/// Готовность счёта к «Разобран» в ответе чтения (issue #1291).
/// </summary>
/// <param name="Required">Ключи обязательных полей шапки — форма помечает их. Из объявления типа, а не
/// списком в форме: добавленное обязательное поле иначе требовалось бы молча.</param>
/// <param name="Gaps">Чего не хватает; пусто — «Разобран» пройдёт. <c>null</c> — вопрос не стоит: счёт
/// уже разобран либо отклонён. ⚠️ <c>null</c> и пусто — разное: пустой перечень означает «всё готово».</param>
public sealed record InvoiceReadinessView(IReadOnlyList<string> Required, IReadOnlyList<InvoiceReadinessGap>? Gaps);

/// <summary>
/// Чего счёту не хватает до «Разобран» (ТЗ COST-9; issue #1291).
///
/// <para><b>Одна функция — два потребителя</b>: переход «разобран» отказывает, если перечень не пуст, а
/// ответ чтения счёта несёт тот же перечень форме. Раньше условия проверялись цепочкой, и человек
/// узнавал их нажатием, по одному за раз: пять условий — пять отказов. Второй реализации условий нет ни
/// на сервере, ни на клиенте, и «переход прошёл ⇔ перечень пуст» верно по устройству, а не по
/// совпадению двух списков.</para>
///
/// <para>⚠️ Функция чистая: ядро она не опрашивает. Всё, что ей нужно знать о записях ядра, приносит
/// звавший — тем же снимком, из которого собран ответ чтения (<see cref="InvoiceDesk" />). Чтение счёта
/// зовётся после каждой записи, и второй опрос ядра ради перечня удвоил бы его цену.</para>
///
/// <para>Порядок пунктов — порядок прежней цепочки и порядок формы сверху вниз: поля шапки → строки →
/// позиции → ссылки → разноска.</para>
///
/// <para>⚠️ <b>Замок закрытого периода условием готовности НЕ является</b>: он отказывает любой записи
/// счёта, а не переходу, и чинится не в счёте. Поэтому у запертого счёта перечня нет вовсе
/// (<see cref="InvoiceViews" /> отдаёт <c>null</c>) — «всё готово» рядом с переходом, которому связка
/// записи ответит «заперт», было бы неправдой (ревью PR #1301).</para>
/// </summary>
public sealed class InvoiceReadiness
{
    private readonly List<(InvoiceReadinessGap Gap, int Group)> _gaps = [];
    private readonly List<(string Text, bool Conflict)> _refusals = [];

    /// <summary>Чего не хватает. Пусто — счёт готов.</summary>
    public IReadOnlyList<InvoiceReadinessGap> Gaps => [.. _gaps.Select(g => g.Gap)];

    public bool Ready => _gaps.Count == 0;

    /// <summary>Ключи обязательных полей шапки.</summary>
    public static IReadOnlyList<string> Required { get; } =
        [.. CostsRecordTypes.Invoice.Fields.Where(f => f.Required).Select(f => f.Key)];

    /// <param name="names">Названия позиций строк; <c>null</c> — вида «Номенклатура» в установке нет.</param>
    /// <param name="lost">Записи ядра, которых нет вовсе, — по обратному опросу.</param>
    public static InvoiceReadiness Of(
        Invoice invoice, IReadOnlyList<InvoiceLine> lines, IReadOnlyDictionary<Guid, string?>? names,
        IReadOnlySet<Guid> lost, AllocationSummaryView allocation)
    {
        var found = new InvoiceReadiness();
        var label = InvoiceEndpoints.Label(invoice);

        found.Fields(invoice, label);
        found.Lines(lines, label);
        found.References(invoice, lines, names, lost, label);
        found.Allocation(allocation, label);
        return found;
    }

    /// <summary>
    /// Отказать переходу, если счёт не готов. Слова — первого по порядку условия, прежние: кнопку можно
    /// нажать и по устаревшей форме, и отказ обязан объяснять, а не перечислять. Остальное названо
    /// следом, коротко: человек, чинивший первое, раньше узнавал о втором следующим нажатием.
    /// </summary>
    public void EnsureReady()
    {
        if (Ready) return;

        var (text, conflict) = _refusals[0];
        var rest = _gaps.Where(g => g.Group > 0).Select(g => g.Gap.Label).ToList();
        if (rest.Count > 0) text += $" Кроме того, до «Разобран» осталось: {string.Join(" · ", rest)}.";

        throw conflict ? new ConflictException(text) : new InvalidRequestException(text);
    }

    private void Add(string text, bool conflict, params InvoiceReadinessGap[] gaps)
    {
        _gaps.AddRange(gaps.Select(gap => (gap, _refusals.Count)));
        _refusals.Add((text, conflict));
    }

    private void Fields(Invoice invoice, string label)
    {
        var missing = InvoiceRequisites.MissingFields(invoice);
        if (missing.Count == 0) return;

        Add($"{label}: не заполнено обязательное — " +
            string.Join(", ", missing.Select(m => $"«{m.Title}»")) + ". Черновик так живёт, а разобранный " +
            "счёт — нет: с него считаются затраты, и незаполненное поле там означало бы пропуск в " +
            "отчёте, а не пустую клетку в форме.",
            conflict: false,
            [.. missing.Select(m => new InvoiceReadinessGap("field", m.Title.ToLowerInvariant(), Field: m.Key))]);
    }

    private void Lines(IReadOnlyList<InvoiceLine> lines, string label)
    {
        if (lines.Count == 0)
        {
            Add($"{label}: строк нет, а «разобран» означает «строки есть, и у " +
                "каждой своя позиция номенклатуры». Счёт без строк живёт черновиком сколько нужно — " +
                "именно для этого черновик и есть.",
                conflict: false, new InvoiceReadinessGap("lines", "строки"));
            return;
        }

        var unmatched = lines.Where(l => l.NomenclatureId is null).Select(l => l.Ordinal).ToList();
        if (unmatched.Count > 0)
            Add($"{label}: {Subject(unmatched)} " +
                $"{(unmatched.Count == 1 ? "ждёт" : "ждут")} позиции номенклатуры. Сопоставьте их со " +
                "справочником — свободный текст позицию не заменяет: без ссылки нельзя ни свести " +
                "затраты, ни связать материал с документом качества. Пока они ждут, счёт остаётся " +
                "черновиком и виден в отборе «Разобрать».",
                conflict: false,
                new InvoiceReadinessGap("nomenclature", $"{Counted(unmatched.Count)} без позиции", Lines: unmatched));
    }

    /// <summary>
    /// Ссылки счёта на справочник ядра обязаны вести к записям: позиции строк и организации шапки.
    ///
    /// <para>⚠️ «Позиция указана» и «позиция есть» — разное, и разводит их не опечатка, а устройство:
    /// внешнего ключа между схемой модуля и справочником ядра нет, и ядро строк счёта ссылающимися не
    /// видит — позицию можно удалить. Переход проверял только первое, и счёт становился «разобран» со
    /// строкой, которую ответ ТЕМ ЖЕ запросом называл потерянной (находка ревью, issue #1166).</para>
    ///
    /// <para>Организации шапки — та же дверь: обязательность поля отвечает на «заполнено», а не на
    /// «запись на месте». Архивная запись — не потеря: счёт с ней разбирается.</para>
    ///
    /// <para>Чего здесь НЕТ: позицию, удалённую ПОСЛЕ перехода, эта проверка не видит — модуль об
    /// удалении не узнаёт, и счёт остаётся разобранным с пометкой «позиция не найдена» в форме.</para>
    /// </summary>
    private void References(
        Invoice invoice, IReadOnlyList<InvoiceLine> lines, IReadOnlyDictionary<Guid, string?>? names,
        IReadOnlySet<Guid> lost, string label)
    {
        var problems = new List<string>();
        var gaps = new List<InvoiceReadinessGap>();

        foreach (var (field, id) in new[]
                 {
                     (InvoiceRequisites.SupplierKey, invoice.SupplierId),
                     (InvoiceRequisites.PayerKey, invoice.PayerId),
                 })
            if (id is { } organization && lost.Contains(organization))
            {
                // Имя поля — из объявления типа, тем же источником, что у пункта «не заполнено»: ключ и
                // заголовок сегодня совпадают, и разойдись они, одно поле называлось бы двумя именами.
                var title = InvoiceRequisites.TitleOf(field);
                problems.Add($"организации из поля «{title}» нет в справочнике");
                gaps.Add(new InvoiceReadinessGap("reference", $"{title.ToLowerInvariant()}: записи нет в справочнике", Field: field));
            }

        if (names is not null)
        {
            var gone = lines
                .Where(l => l.NomenclatureId is { } position && !names.ContainsKey(position))
                .Select(l => l.Ordinal)
                .ToList();
            if (gone.Count > 0)
            {
                problems.Add($"позиции номенклатуры нет в справочнике у {Genitive(gone)}");
                gaps.Add(new InvoiceReadinessGap("position", $"позиции нет в справочнике: {Subject(gone)}", Lines: gone));
            }
        }

        if (problems.Count > 0)
            Add($"{label}: {string.Join("; ", problems)}. Так бывает, когда запись " +
                "удалили или перенесли в другой вид: ссылка осталась, а записи нет. Выберите её заново — " +
                "«разобран» означает, что счёт сведён со справочником, а ссылка в пустоту не сводится ни с чем.",
                conflict: false, [.. gaps]);

        // Вида «Номенклатура» нет вовсе — спрашивать не у кого, и это не «все позиции потеряны»:
        // состояние установки, а не счёта. Но и «сверено со справочником» без справочника не скажешь.
        // ⚠️ ПОСЛЕ ссылок шапки: потерянного поставщика человек починит сам, а справочник — нет, и
        // названное первым непочинимое заслоняло бы поле, которое в форме стоит выше (ревью PR #1301).
        if (names is null && lines.Any(l => l.NomenclatureId is not null))
            Add($"{label}: тип «{CostsRecordTypes.NomenclatureCode}» в системе не " +
                "заведён, и сверить позиции строк не с чем. «Разобран» означает, что строки сведены со " +
                "справочником, — пока его нет, счёт остаётся черновиком.",
                conflict: true, new InvoiceReadinessGap("catalog", "справочник номенклатуры не заведён"));
    }

    /// <summary>
    /// «Разнесён» (ТЗ COST-9, COST-13) — последнее условие. Отказ называет ВСЁ, чего не хватает разноске,
    /// а не первое: иначе человек разносил бы строку, чтобы узнать о расхождении суммы.
    ///
    /// <para>У каждой причины — свой пункт (ревью PR #1301): чинятся они в разных местах формы. Один
    /// пункт «разноска» вёл к выбору объекта и тогда, когда расходилась сумма, — выбор объекта её не
    /// чинит, а причину человек по-прежнему узнавал нажатием.</para>
    /// </summary>
    private void Allocation(AllocationSummaryView allocation, string label)
    {
        if (allocation.Allocated) return;

        var problems = new List<string>();
        var gaps = new List<InvoiceReadinessGap>();

        if (allocation.Unbalanced.Count > 0)
        {
            problems.Add($"{Subject(allocation.Unbalanced)} " +
                $"{(allocation.Unbalanced.Count == 1 ? "разнесена" : "разнесены")} по стройкам не полностью — " +
                "остаток виден строкой «не разнесено»");
            gaps.Add(new InvoiceReadinessGap("allocation", "разноска", Lines: allocation.Unbalanced));
        }

        if (allocation.Document.Pending)
        {
            problems.Add("разноска счёта суммой, сделанная до строк, ждёт пересчёта по строкам — пересчитайте её " +
                "в матрице разноски: пока она ждёт, деньги лежат на стройках суммой, а строки не разнесены никуда");
            gaps.Add(new InvoiceReadinessGap("allocation", "пересчёт разноски по строкам"));
        }

        if (allocation.Lost > 0)
        {
            problems.Add($"частей разноски на удалённую стройку или раздел: {allocation.Lost} — выберите цель заново");
            gaps.Add(new InvoiceReadinessGap("allocation", $"разноска на удалённую цель: {allocation.Lost}"));
        }

        if (!allocation.WithinTolerance && allocation.Discrepancy is { } gap)
        {
            problems.Add($"сумма строк расходится с суммой к оплате на {Math.Abs(gap):0.00} ₽ при допуске " +
                $"{allocation.Tolerance:0.00} ₽ — расхождение в пределах допуска уходит в последнюю часть " +
                "разноски, сверх него нужно разобраться: скидка, доставка или строка, которой нет в таблице");
            gaps.Add(new InvoiceReadinessGap("total",
                $"сумма строк расходится с суммой к оплате на {Math.Abs(gap):0.00} ₽", Field: InvoiceRequisites.TotalKey));
        }

        // «Не разнесён» без единой названной причины — счёт без строк: разносить ещё нечего.
        if (problems.Count == 0)
        {
            problems.Add("счёт не разнесён по стройкам");
            gaps.Add(new InvoiceReadinessGap("allocation", "разноска"));
        }

        Add($"{label}: {string.Join("; ", problems)}. «Разобран» означает, что " +
            "деньги счёта легли на стройки целиком: иначе затраты по стройке не сойдутся со счетами.",
            conflict: false, [.. gaps]);
    }

    /// <summary>
    /// «строка 3» / «строки 3, 7» — подлежащим. ⚠️ Падеж и число — не косметика: сообщение читает человек
    /// за формой. Первая версия склеивала «ждут строки» с «строка 1» и выдавала «ждут строки строка 1».
    /// </summary>
    internal static string Subject(IReadOnlyList<int> numbers) =>
        (numbers.Count == 1 ? "строка " : "строки ") + string.Join(", ", numbers);

    /// <summary>То же в родительном: «у строки 3», «у строк 3, 7».</summary>
    internal static string Genitive(IReadOnlyList<int> numbers) =>
        (numbers.Count == 1 ? "строки " : "строк ") + string.Join(", ", numbers);

    /// <summary>«1 строка», «3 строки», «12 строк».</summary>
    private static string Counted(int count) => (count % 100 is >= 11 and <= 14, count % 10) switch
    {
        (false, 1) => $"{count} строка",
        (false, >= 2 and <= 4) => $"{count} строки",
        _ => $"{count} строк",
    };
}
