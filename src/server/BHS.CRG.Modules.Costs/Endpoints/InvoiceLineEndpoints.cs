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
        IModuleConstructions sites,
        IModuleActivityLog log, CancellationToken ct)
    {
        if (body.Lines is null)
            throw new InvalidRequestException(
                "Набор строк не прислан. Пустой набор — это «lines»: [], и он означает «строк нет» " +
                "(все удалены). Отсутствие поля прочитать как «строки не менять» нельзя: адрес заменяет " +
                "набор целиком, и «не менять» — это просто не звать его.");

        var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);
        var incoming = body.Lines;

        var parsed = new List<(Guid? Id, InvoiceLineValues Values)>(incoming.Count);
        for (var index = 0; index < incoming.Count; index++)
            parsed.Add((InvoiceLineRequests.Id(incoming[index], index + 1),
                InvoiceLineRequests.Values(incoming[index], index + 1)));

        EnsureIdsDistinct(parsed);
        await EnsureNomenclatureExistsAsync(catalog, parsed, ct);

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

            line.Apply(index + 1, values);
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
            : !await AllocatedAfterAsync(db, sites, invoice, now, ct)
                ? "баланс разноски не сходится"
            : null;
        if (reason is not null) invoice.ReturnToDraft();

        await db.SaveChangesAsync(ct);

        if (!was.SequenceEqual(now))
            await log.RecordAsync(InvoiceActions.LinesChanged, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), after: $"строк: {count}", ct: ct);

        if (reason is not null)
            await log.RecordAsync(InvoiceActions.Draft, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), after: $"правка строк: {reason}", ct: ct);

        return TypedResults.Ok(await InvoiceEndpoints.ViewAsync(db, catalog, sites, invoice, ct));
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
        Guid id, CostsDbContext db, IModuleCatalog catalog, IModuleConstructions sites,
        IModuleActivityLog log, CancellationToken ct)
    {
        var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);

        if (invoice.State == InvoiceState.Rejected)
            throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)} отклонён — разбирать его незачем. Верните счёт в " +
                "черновик, если решение изменилось.");

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

        EnsureAllocated(invoice, (await InvoiceAllocations.ReadAsync(db, sites, invoice, lines, ct)).Summary);

        invoice.MarkParsed();
        await db.SaveChangesAsync(ct);
        await log.RecordAsync(InvoiceActions.Parsed, invoice.Id.ToString(),
            InvoiceEndpoints.Label(invoice), after: $"строк: {lines.Count}", ct: ct);

        return TypedResults.Ok(await InvoiceEndpoints.ViewAsync(db, catalog, sites, invoice, ct));
    }

    /// <summary>Вернуть счёт в черновик — решением человека (см. <see cref="Invoice.ReturnToDraft" />).</summary>
    private static async Task<Ok<InvoiceView>> DraftAsync(
        Guid id, CostsDbContext db, IModuleCatalog catalog, IModuleConstructions sites,
        IModuleActivityLog log, CancellationToken ct)
    {
        var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);

        if (invoice.State != InvoiceState.Draft)
        {
            invoice.ReturnToDraft();
            await db.SaveChangesAsync(ct);
            await log.RecordAsync(InvoiceActions.Draft, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), after: "решением человека", ct: ct);
        }

        return TypedResults.Ok(await InvoiceEndpoints.ViewAsync(db, catalog, sites, invoice, ct));
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
    /// Сойдётся ли разноска после правки строк — по ПРИСЛАННЫМ строкам и частям, которые у них уже
    /// есть. Уменьшили количество строки после разноски, убрали его вовсе, поменяли цену так, что
    /// сумма строк ушла от суммы к оплате сверх допуска, — «разобран» перестаёт быть правдой.
    ///
    /// <para>Части удалённых строк в счёт не идут: их унесёт каскад вместе со строкой.</para>
    /// </summary>
    private static async Task<bool> AllocatedAfterAsync(
        CostsDbContext db, IModuleConstructions sites, Invoice invoice,
        IReadOnlyList<(Guid Id, InvoiceLineValues Values)> lines, CancellationToken ct)
    {
        var kept = lines.Select(l => l.Id).ToHashSet();
        var parts = (await db.InvoiceAllocations.AsNoTracking()
                .Where(a => a.InvoiceId == invoice.Id)
                .ToListAsync(ct))
            .Where(a => kept.Contains(a.LineId))
            .ToList();

        var known = parts.Count == 0 ? [] : await sites.ListAsync(ct);
        var shape = lines.Select((l, index) =>
            new AllocationLine(l.Id, index + 1, l.Values.Quantity, l.Values.Amount));

        return InvoiceAllocations.Read(invoice, shape, parts, known).Summary.Allocated;
    }

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
