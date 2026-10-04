using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Строки счёта и переход «черновик → разобран» (задача C2 этапа 2, issue #1078, ТЗ COST-7, COST-9).
///
/// <para>Отдельным файлом от адресов счёта, а не дописано к ним: адреса счёта уже держат шапку, скан и
/// метки, и файл-склад читают ЦЕЛИКОМ ради одной правки (храповик размера, issue #1041).</para>
///
/// <para><b>Строки присылаются НАБОРОМ</b> — см. <see cref="InvoiceLinesRequest" />. Ни сохранение
/// счёта, ни этот адрес строками ничего не запрещают: черновик со строками без позиции — штатное
/// состояние, он живёт в реестре и попадает в отбор «Разобрать» (ТЗ COST-6.2). Единственное место, где
/// строки что-то решают, — переход «разобран».</para>
/// </summary>
public static class InvoiceLineEndpoints
{
    private const string Edit = "costs.invoice.edit";

    public static void MapInvoiceLines(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/costs/invoices/{id:guid}").WithTags("Счета на оплату");

        group.MapPut("/lines", ReplaceAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPost("/parsed", ParsedAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPost("/draft", DraftAsync).RequireAuthorization(AppPolicies.Permission(Edit));
    }

    /// <summary>
    /// Заменить строки счёта присланным набором.
    ///
    /// <para>⚠️ Строки с <c>id</c> правятся НА МЕСТЕ, а не заводятся заново: на строку будет ссылаться
    /// разноска по количеству (F1), и пересоздание набора рвало бы эти ссылки на каждом сохранении
    /// формы — виноватой при этом выглядела бы разноска.</para>
    ///
    /// <para>⚠️ Разобранный счёт, у которого после правки строк не осталось позиций, САМ возвращается в
    /// черновик. Иначе «разобран» осталось бы утверждением, перестав быть правдой, — и отбор «Разобрать»
    /// такой счёт не показал бы. Молчанием это не делается: возврат пишется в журнал, а состояние
    /// уезжает в ответе.</para>
    /// </summary>
    private static async Task<Ok<InvoiceView>> ReplaceAsync(
        Guid id, InvoiceLinesRequest body, CostsDbContext db, IModuleCatalog catalog,
        AllocationPlacesSource places, InvoiceDesk desk,
        IModuleActivityLog log, CancellationToken ct)
    {
        if (body.Lines is null)
            throw new InvalidRequestException(
                "Набор строк не прислан. Пустой набор — это «lines»: [], и он означает «строк нет» " +
                "(все удалены). Отсутствие поля прочитать как «строки не менять» нельзя: адрес заменяет " +
                "набор целиком, и «не менять» — это просто не звать его.");

        var incoming = body.Lines;

        var parsed = new List<(Guid? Id, InvoiceLineValues Values)>(incoming.Count);
        for (var index = 0; index < incoming.Count; index++)
            parsed.Add((InvoiceLineRequests.Id(incoming[index], index + 1),
                InvoiceLineRequests.Values(incoming[index], index + 1)));

        EnsureIdsDistinct(parsed);
        await EnsureNomenclatureExistsAsync(catalog, parsed, ct);

        var (invoice, changed, reason) = await desk.WriteAsync(id, write => PlaceAsync(write.Invoice), ct);

        if (changed)
            await log.RecordAsync(InvoiceActions.LinesChanged, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), after: $"строк: {parsed.Count}", ct: ct);

        if (reason is not null)
            await log.RecordAsync(InvoiceActions.Draft, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), after: $"правка строк: {reason}", ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));

        // Сама правка — под замком записи, по счёту, прочитанному после него.
        async Task<(Invoice Invoice, bool Changed, string? Reason)> PlaceAsync(Invoice invoice)
        {
            var existing = await db.InvoiceLines.Where(l => l.InvoiceId == invoice.Id).ToListAsync(ct);
            var kept = new HashSet<Guid>();

            // Снимок ДО правки — им отличается настоящая правка от повторной отправки того же набора.
            // Форма присылает строки целиком на каждое сохранение, и запись «строки изменены» без этой
            // сверки появлялась бы в журнале там, где не изменилось ничего: журнал заполнился бы шумом, а
            // настоящая правка в нём потерялась бы.
            var was = existing.OrderBy(l => l.Ordinal).Select(l => (l.Id, Values: l.Snapshot())).ToList();
            var now = new List<(Guid Id, InvoiceLineValues Values)>(parsed.Count);

            for (var index = 0; index < parsed.Count; index++)
            {
                var (lineId, values) = parsed[index];

                var line = lineId is { } known
                    ? existing.FirstOrDefault(l => l.Id == known)
                        ?? throw new InvalidRequestException(
                            $"Строка {index + 1}: строки {known} у этого счёта нет. Так бывает, когда форму " +
                            "оставили открытой, а строку тем временем удалили: присланное состояние опирается " +
                            "на то, чего уже нет. Перечитайте счёт и повторите правку — иначе удалённая " +
                            "строка вернулась бы молча.")
                    : Added(db, invoice.Id);

                // Пишем только то, что изменилось, — значения или место в наборе (issue #1171). Apply ставит
                // «когда правили» безусловно, и позови мы его для каждой присланной строки, правка одной
                // строки из ста давала бы сто обновлений, а время правки у девяноста девяти врало бы.
                // У новой строки место ещё нулевое, поэтому она сюда попадает всегда.
                if (line.Ordinal != index + 1 || line.Snapshot() != values) line.Apply(index + 1, values);
                kept.Add(line.Id);
                now.Add((line.Id, values));
            }

            db.InvoiceLines.RemoveRange(existing.Where(l => !kept.Contains(l.Id)));

            // Возврат в черновик — ДО сохранения: одна запись в базу на всю правку.
            //
            // ⚠️ Условие повторяет условие перехода «разобран», и повторяется оно нарочно: здесь его
            // проверяют по ПРИСЛАННОМУ (строки ещё не в базе), а там — по лежащему. Свести их в один
            // помощник значило бы читать базу дважды на одно сохранение; расхождение же ловится тестами с
            // двух сторон — «разобран отказывает, пока строка ждёт позиции» и «правка строк возвращает
            // счёт в черновик».
            var count = parsed.Count;
            var reason = invoice.State != InvoiceState.Parsed ? null
                : count == 0 || parsed.Any(p => p.Values.NomenclatureId is null)
                    ? "позиция номенклатуры есть не у всех строк"
                : !await InvoiceAllocations.AllocatedAfterAsync(db, places, invoice,
                    [.. now.Select((l, index) => new AllocationLine(l.Id, index + 1, l.Values.Quantity, l.Values.Amount))],
                    ct)
                    ? "баланс разноски не сходится"
                : null;
            if (reason is not null) invoice.ReturnToDraft();

            // Правка строк — правка счёта: время его правки обязано сдвинуться. Повторная отправка того же
            // набора правкой не является — ни для журнала, ни для времени.
            var changed = !was.SequenceEqual(now);
            if (changed) invoice.ContentChanged();

            await db.SaveChangesAsync(ct);
            return (invoice, changed, reason);
        }
    }

    /// <summary>
    /// «Разобран» (ТЗ COST-9): строки есть, у всех позиция номенклатуры, обязательные поля заполнены.
    ///
    /// <para>Отдельным действием, а не следствием сохранения: это утверждение ЧЕЛОВЕКА о том, что счёт
    /// сверен с бумагой. Случись оно само — «разобран» означало бы «поля заполнились», и сверять было бы
    /// нечего.</para>
    ///
    /// <para>С F1 (issue #1085) в условие входит и <b>«разнесён»</b>: каждая строка разнесена по стройкам
    /// полностью, цели на месте, расхождение суммы строк с суммой к оплате — в пределах допуска.</para>
    /// </summary>
    private static async Task<Ok<InvoiceView>> ParsedAsync(
        Guid id, CostsDbContext db, IModuleCatalog catalog, AllocationPlacesSource places, InvoiceDesk desk,
        IModuleActivityLog log, CancellationToken ct)
    {
        var (invoice, lines) = await desk.WriteAsync(id, write => MarkAsync(write.Invoice, db, catalog, places, ct), ct);

        // Ноль строк — «уже был разобран»: решения не было, и в журнал писать нечего.
        if (lines > 0)
            await log.RecordAsync(InvoiceActions.Parsed, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), after: $"строк: {lines}", ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }

    private static async Task<(Invoice Invoice, int Lines)> MarkAsync(
        Invoice invoice, CostsDbContext db, IModuleCatalog catalog, AllocationPlacesSource places, CancellationToken ct)
    {
        if (invoice.State == InvoiceState.Rejected)
            throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)} отклонён — разбирать его незачем. Верните счёт в " +
                "черновик, если решение изменилось.");

        // Уже разобран — подтверждать нечего, и в журнал не пишем: запись «счёт разобран» означает
        // решение человека, а повторное нажатие (или повтор запроса после обрыва связи) новым решением
        // не является. Так же молчит возврат в черновик у черновика.
        if (invoice.State == InvoiceState.Parsed) return (invoice, 0);

        var missing = InvoiceRequisites.Missing(invoice);
        if (missing.Count > 0)
            throw new InvalidRequestException(
                $"{InvoiceEndpoints.Label(invoice)}: не заполнено обязательное — " +
                string.Join(", ", missing.Select(m => $"«{m}»")) + ". Черновик так живёт, а разобранный " +
                "счёт — нет: с него считаются затраты, и незаполненное поле там означало бы пропуск в " +
                "отчёте, а не пустую клетку в форме.");

        var lines = await db.InvoiceLines.AsNoTracking()
            .Where(l => l.InvoiceId == invoice.Id)
            .OrderBy(l => l.Ordinal)
            .ToListAsync(ct);

        if (lines.Count == 0)
            throw new InvalidRequestException(
                $"{InvoiceEndpoints.Label(invoice)}: строк нет, а «разобран» означает «строки есть, и у " +
                "каждой своя позиция номенклатуры». Счёт без строк живёт черновиком сколько нужно — " +
                "именно для этого черновик и есть.");

        var unmatched = lines.Where(l => l.NomenclatureId is null).Select(l => l.Ordinal).ToList();
        if (unmatched.Count > 0)
            throw new InvalidRequestException(
                $"{InvoiceEndpoints.Label(invoice)}: {Subject(unmatched)} " +
                $"{(unmatched.Count == 1 ? "ждёт" : "ждут")} позиции номенклатуры. Сопоставьте их со " +
                "справочником — свободный текст позицию не заменяет: без ссылки нельзя ни свести " +
                "затраты, ни связать материал с документом качества. Пока они ждут, счёт остаётся " +
                "черновиком и виден в отборе «Разобрать».");

        await EnsureReferencesAliveAsync(catalog, invoice, lines, ct);

        EnsureAllocated(invoice, (await InvoiceAllocations.ReadAsync(db, places, invoice, lines, ct)).Summary);

        invoice.MarkParsed();
        await db.SaveChangesAsync(ct);
        return (invoice, lines.Count);
    }

    /// <summary>Вернуть счёт в черновик — решением человека (см. <see cref="Invoice.ReturnToDraft" />).</summary>
    private static async Task<Ok<InvoiceView>> DraftAsync(
        Guid id, CostsDbContext db, InvoiceDesk desk, IModuleActivityLog log, CancellationToken ct)
    {
        var (invoice, returned) = await desk.WriteAsync(id, async write =>
        {
            if (write.Invoice.State == InvoiceState.Draft) return (write.Invoice, false);

            write.Invoice.ReturnToDraft();
            await db.SaveChangesAsync(ct);
            return (write.Invoice, true);
        }, ct);

        if (returned)
            await log.RecordAsync(InvoiceActions.Draft, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), after: "решением человека", ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }

    /// <summary>
    /// «Разнесён» (ТЗ COST-9, COST-13) — последнее условие перехода «разобран». Отказ называет ВСЁ, чего
    /// не хватает, а не первое: иначе человек разносил бы строку, чтобы узнать о расхождении суммы.
    /// </summary>
    private static void EnsureAllocated(Invoice invoice, AllocationSummaryView allocation)
    {
        if (allocation.Allocated) return;

        var problems = new List<string>();

        if (allocation.Unbalanced.Count > 0)
            problems.Add($"{Subject(allocation.Unbalanced)} " +
                $"{(allocation.Unbalanced.Count == 1 ? "разнесена" : "разнесены")} по стройкам не полностью — " +
                "остаток виден строкой «не разнесено»");

        if (allocation.Document.Pending)
            problems.Add("разноска счёта суммой, сделанная до строк, ждёт пересчёта по строкам — пересчитайте её " +
                "в матрице разноски: пока она ждёт, деньги лежат на стройках суммой, а строки не разнесены никуда");

        if (allocation.Lost > 0)
            problems.Add($"частей разноски на удалённую стройку или раздел: {allocation.Lost} — выберите цель заново");

        if (!allocation.WithinTolerance && allocation.Discrepancy is { } gap)
            problems.Add($"сумма строк расходится с суммой к оплате на {Math.Abs(gap):0.00} ₽ при допуске " +
                $"{allocation.Tolerance:0.00} ₽ — расхождение в пределах допуска уходит в последнюю часть " +
                "разноски, сверх него нужно разобраться: скидка, доставка или строка, которой нет в таблице");

        throw new InvalidRequestException(
            $"{InvoiceEndpoints.Label(invoice)}: {string.Join("; ", problems)}. «Разобран» означает, что " +
            "деньги счёта легли на стройки целиком: иначе затраты по стройке не сойдутся со счетами.");
    }

    /// <summary>
    /// Ссылки счёта на справочник ядра обязаны вести к записям: позиции строк и организации шапки.
    ///
    /// <para>⚠️ «Позиция указана» и «позиция есть» — разное, и разводит их не опечатка, а устройство:
    /// внешнего ключа между схемой модуля и справочником ядра нет, и ядро строк счёта ссылающимися не
    /// видит — позицию можно удалить. Переход проверял только первое, и счёт становился «разобран» со
    /// строкой, которую ответ ТЕМ ЖЕ запросом называл потерянной (<c>nomenclatureLost</c>): сверен, а
    /// свести затраты не с чем (находка ревью, issue #1166). Сохранение строк такую ссылку отвергает —
    /// переход обязан быть не мягче него.</para>
    ///
    /// <para>Организации шапки — та же дверь: обязательность поля отвечает на «заполнено», а не на
    /// «запись на месте».</para>
    ///
    /// <para>Названо ВСЁ разом, как у разноски: иначе человек чинил бы поставщика, чтобы узнать о
    /// строке.</para>
    ///
    /// <para>Чего здесь НЕТ: позицию, удалённую ПОСЛЕ перехода, эта проверка не видит — модуль об
    /// удалении не узнаёт, и счёт остаётся разобранным с пометкой «позиция не найдена» в форме. Это
    /// вопрос к удалению в ядре, а не к переходу.</para>
    /// </summary>
    private static async Task EnsureReferencesAliveAsync(
        IModuleCatalog catalog, Invoice invoice, IReadOnlyList<InvoiceLine> lines, CancellationToken ct)
    {
        var problems = new List<string>();

        foreach (var (field, id) in new[]
                 {
                     (InvoiceRequisites.SupplierKey, invoice.SupplierId),
                     (InvoiceRequisites.PayerKey, invoice.PayerId),
                 })
            if (id is { } organization && await catalog.GetAsync(organization, ct) is null)
                problems.Add($"организации из поля «{field}» нет в справочнике");

        // Вида «Номенклатура» нет вовсе — спрашивать не у кого, и это не «все позиции потеряны»:
        // состояние установки, а не счёта. Но и «сверено со справочником» без справочника не скажешь.
        var names = await InvoiceEndpoints.NomenclatureNamesAsync(catalog, lines, ct)
            ?? throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)}: тип «{CostsRecordTypes.NomenclatureCode}» в системе не " +
                "заведён, и сверить позиции строк не с чем. «Разобран» означает, что строки сведены со " +
                "справочником, — пока его нет, счёт остаётся черновиком.");

        var lost = lines
            .Where(l => l.NomenclatureId is { } position && !names.ContainsKey(position))
            .Select(l => l.Ordinal)
            .ToList();
        if (lost.Count > 0)
            problems.Add($"позиции номенклатуры нет в справочнике у {Genitive(lost)}");

        if (problems.Count > 0)
            throw new InvalidRequestException(
                $"{InvoiceEndpoints.Label(invoice)}: {string.Join("; ", problems)}. Так бывает, когда запись " +
                "удалили или перенесли в другой вид: ссылка осталась, а записи нет. Выберите её заново — " +
                "«разобран» означает, что счёт сведён со справочником, а ссылка в пустоту не сводится ни с чем.");
    }

    /// <summary>Строки счёта, как они лежат, — в том виде, в каком их считает разноска.</summary>
    internal static async Task<IReadOnlyList<AllocationLine>> StoredLinesAsync(
        CostsDbContext db, Invoice invoice, CancellationToken ct) =>
        [.. (await db.InvoiceLines.AsNoTracking().Where(l => l.InvoiceId == invoice.Id).ToListAsync(ct))
            .Select(InvoiceAllocations.Line)];

    private static InvoiceLine Added(CostsDbContext db, Guid invoiceId)
    {
        var line = InvoiceLine.Create(invoiceId);
        db.InvoiceLines.Add(line);
        return line;
    }

    /// <summary>
    /// Один и тот же идентификатор дважды — отказ. Прочитать это как «последняя побеждает» нельзя: набор
    /// заменяет состояние, и одна из двух строк исчезла бы без следа.
    /// </summary>
    private static void EnsureIdsDistinct(List<(Guid? Id, InvoiceLineValues Values)> parsed)
    {
        var repeated = parsed.Where(p => p.Id is not null)
            .GroupBy(p => p.Id!.Value)
            .FirstOrDefault(g => g.Count() > 1);

        if (repeated is not null)
            throw new InvalidRequestException(
                $"Строка {repeated.Key} прислана дважды. Набор заменяет состояние целиком, поэтому " +
                "повтор означал бы, что одна из двух строк исчезнет без следа. Новые строки присылайте " +
                "без «id».");
    }

    /// <summary>
    /// Позиции, на которые ссылаются строки, обязаны существовать — и быть номенклатурой.
    ///
    /// <para>Одним обращением на весь набор: вставка из буфера приносит десятки строк, и запрос на
    /// строку означал бы десятки обращений к ядру на одно сохранение.</para>
    ///
    /// <para>⚠️ «Вида нет вовсе» и «записи нет» — разные отказы. Первое — состояние установки (тип
    /// «Номенклатура» заводит ядро миграцией там, где был «Материал»), и говорить о нём надо иначе:
    /// человек за формой ничего не исправит правкой строки.</para>
    /// </summary>
    private static async Task EnsureNomenclatureExistsAsync(
        IModuleCatalog catalog, List<(Guid? Id, InvoiceLineValues Values)> parsed, CancellationToken ct)
    {
        var referenced = parsed.Select(p => p.Values.NomenclatureId)
            .OfType<Guid>()
            .Distinct()
            .ToList();

        if (referenced.Count == 0) return;

        var known = await catalog.RefsAsync(CostsRecordTypes.NomenclatureCode, referenced, ct)
            ?? throw new ConflictException(
                $"Тип «{CostsRecordTypes.NomenclatureCode}» в системе не заведён, поэтому ссылаться " +
                "строкам не на что. Этот тип появляется вместе со справочником материалов: он либо " +
                "приехал миграцией ядра, либо его заводит человек в разделе типов. Строки без позиции " +
                "при этом сохраняются — счёт остаётся черновиком и ждёт в отборе «Разобрать».");

        var found = known.Select(r => r.Id).ToHashSet();
        var lost = parsed
            .Select((p, index) => (Number: index + 1, p.Values.NomenclatureId))
            .Where(p => p.NomenclatureId is { } value && !found.Contains(value))
            .Select(p => p.Number)
            .ToList();

        if (lost.Count > 0)
            throw new InvalidRequestException(
                $"Позиции номенклатуры нет в справочнике у {Genitive(lost)}" +
                ". Так бывает, когда позицию удалили или переместили в другой вид: ссылка осталась, а " +
                "записи нет. Выберите позицию заново — записать ссылку в пустоту значило бы получить " +
                "строку, которую потом никто не сведёт.");
    }

    /// <summary>
    /// Номера строк словами, именительный падеж: «строка 3», «строки 3, 7».
    ///
    /// <para>Названы ВСЕ, а не первая: человек правит таблицу, и отказ по одной строке за раз превратил
    /// бы вставку из буфера в двадцать заходов.</para>
    ///
    /// <para>⚠️ Падеж и число — не косметика: сообщение читает человек за формой. Первая версия
    /// склеивала «ждут строки» с «строка 1» и выдавала «ждут строки строка 1» — поймал живой прогон,
    /// сторож на живом хосте этого не заметил (он искал подстроку «строка 2», а она в кашице есть).</para>
    /// </summary>
    private static string Subject(IReadOnlyList<int> numbers) =>
        (numbers.Count == 1 ? "строка " : "строки ") + string.Join(", ", numbers);

    /// <summary>То же в родительном: «у строки 3», «у строк 3, 7».</summary>
    private static string Genitive(IReadOnlyList<int> numbers) =>
        (numbers.Count == 1 ? "строки " : "строк ") + string.Join(", ", numbers);
}
