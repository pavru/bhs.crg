using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Набор строк, положенный в счёт: строки по порядку, изменён ли он и что запомнить.</summary>
internal sealed record LinesPlaced(IReadOnlyList<InvoiceLine> Lines, bool Changed, IReadOnlyList<MatchToRemember> Worth);

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
    private const string Edit = CostsModule.InvoiceEdit;

    public static void MapInvoiceLines(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/costs/invoices/{id:guid}").WithTags("Счета на оплату");

        group.MapPut("/lines", ReplaceAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPost("/parsed", ParsedAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPost("/draft", DraftAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPost("/rejected", RejectedAsync).RequireAuthorization(AppPolicies.Permission(Edit));
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
        IModuleActivityLog log, IModuleUser user, ILoggerFactory logs, CancellationToken ct)
    {
        if (body.Lines is null)
            throw new InvalidRequestException(
                "Набор строк не прислан. Пустой набор — это «lines»: [], и он означает «строк нет» " +
                "(все удалены). Отсутствие поля прочитать как «строки не менять» нельзя: адрес заменяет " +
                "набор целиком, и «не менять» — это просто не звать его.");

        var incoming = body.Lines;

        var parsed = new List<(Guid? Id, InvoiceLineValues Values)>(incoming.Count);
        // Идентификаторы, выданные формой новым строкам (issue #1656), — тем же проходом: вид строки
        // проверяет `Id`, он идёт первым.
        var newIds = new List<Guid?>(incoming.Count);
        for (var index = 0; index < incoming.Count; index++)
        {
            parsed.Add((InvoiceLineRequests.Id(incoming[index], index + 1),
                InvoiceLineRequests.Values(incoming[index], index + 1)));
            newIds.Add(InvoiceLineRequests.NewId(incoming[index], index + 1));
        }

        // «Не запоминать» — слово человека про ЭТУ строку (ТЗ COST-7.1): «Доставка» у поставщика всякий
        // раз другая, и запомненная позиция подставлялась бы не туда.
        var remember = incoming.Select((line, index) => InvoiceLineRequests.Remember(line, index + 1)).ToList();

        EnsureNewIdsDistinct(parsed, newIds);
        EnsureIdsDistinct(parsed);
        VatMode? modeWas = null;
        var (invoice, changed, reason, worth) = await desk.WriteAsync(id, write => PlaceAsync(write.Invoice), ct);

        // Журнал правки строк — ДО запоминания: строки уже записаны, и запись о них не должна зависеть
        // от того, чем кончится запоминание (ревью PR #1262). Режим НДС — в КАЖДОМ «стало», а не только при
        // смене: ряд серии берёт «стало» последней записи (ТЗ CORE-28.3), и смена режима посреди серии
        // иначе пропала бы из ряда — «было» его называет, а «стало» молчит.
        if (changed)
            await log.RecordAsync(InvoiceActions.LinesChanged, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), before: modeWas is { } was ? VatModes.Shown(was) : null,
                after: $"строк: {parsed.Count}; НДС: {VatModes.Shown(invoice.VatMode)}", ct: ct);

        if (reason is not null)
            await log.RecordAsync(InvoiceActions.Draft, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), before: InvoiceEndpoints.WasParsed,
                after: $"правка строк: {reason}", ct: ct);

        // Запоминание — ПОСЛЕ записи счёта и вне её транзакции: см. SupplierMatching.RememberAsync. Его
        // отказ счёт не губит: строки записаны, и ответ обязан это сказать — с версией, иначе следующее
        // сохранение формы получило бы «счёт тем временем изменили» от собственной же правки.
        var memory = invoice.SupplierId is { } supplier
            ? await SupplierMatching.RememberSafelyAsync(db, supplier, worth, user,
                logs.CreateLogger(typeof(SupplierMatching)), ct)
            : InvoiceMatchMemory.Nothing;

        // Одной записью на сохранение, а не по записи на строку: счёт на сорок строк иначе давал бы сорок
        // записей, и настоящая правка в журнале потерялась бы. Замена запомненного названа отдельно —
        // это единственное, что здесь меняет поведение следующих счетов.
        if (memory.Remembered > 0)
            await log.RecordAsync(InvoiceActions.MatchesRemembered, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice),
                after: $"запомнено соответствий: {memory.Remembered}, из них заменено: {memory.Replaced}", ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct) with { Memory = memory });

        // Сама правка — под замком записи, по счёту, прочитанному после него.
        async Task<(Invoice Invoice, bool Changed, string? Reason, IReadOnlyList<MatchToRemember> Worth)> PlaceAsync(
            Invoice invoice)
        {
            // Режим — до строк: строки досчитываются по НОВОМУ режиму, в той же правке (issue #1497).
            var before = invoice.VatMode;
            var modeChanged = body.VatMode is { } mode && invoice.ChangeVatMode(mode);
            // Сменил человек — метку распознавания режима снимает его решение (issue #1501), как правка поля.
            if (modeChanged) { modeWas = before; invoice.Confirm([VatModes.Mark]); }

            var placed = await PlaceLinesAsync(db, catalog, invoice, parsed, remember, ct, newIds);

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
                // НДС у части строк (issue #1445) — по записанным строкам: НДС строки досчитан при размещении.
                : InvoiceReadiness.VatUnnamed(invoice.VatMode, placed.Lines).Count > 0
                    ? "НДС указан не у всех строк"
                : !await InvoiceAllocations.AllocatedAfterAsync(db, places, invoice,
                    [.. placed.Lines.Select(InvoiceAllocations.Line)],
                    await desk.ToleranceAsync(ct), ct)
                    ? "баланс разноски не сходится"
                : null;
            if (reason is not null) invoice.ReturnToDraft();

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException lost) when (lost.InnerException is Npgsql.PostgresException
                { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation, ConstraintName: LinesKey })
            {
                // Две записи в РАЗНЫЕ счета с одним «newId» разом (ревью PR #1811): сверка занятости идёт под
                // замком своего счёта, и второй её проходит — отказывает ключ таблицы. Те же слова, что у сверки,
                // а не 500. Только этот ключ: иной отказ базы под этими словами выдал бы себя за другое.
                throw new InvalidRequestException(BornTaken(), lost);
            }
            return (invoice, placed.Changed || modeChanged, reason, placed.Worth);
        }
    }

    /// <summary>
    /// Положить присланный набор строк в счёт — без сохранения и без возврата в черновик: их делает
    /// звавший. Одна функция на замену строк и на исправление счёта (E2, issue #1100).
    ///
    /// <para>Строки досчитываются ЗДЕСЬ, по режиму счёта, прочитанного под замком записи (issue #1497):
    /// режим известен только тут, и досчёт до замка взял бы режим, который тем временем сменили.</para>
    /// </summary>
    /// <param name="parsed">Строки, как в бумаге (<see cref="InvoiceLineRequests.Values" />).</param>
    /// <param name="newIds">Идентификаторы, выданные формой новым строкам (<see cref="InvoiceLineRequests.NewId" />),
    /// по порядку набора; <c>null</c> — их нет (исправление счёта заводит строки своими).</param>
    /// <returns>Строки после правки — по порядку набора; изменён ли набор; что запомнить после записи.</returns>
    internal static async Task<LinesPlaced> PlaceLinesAsync(
        CostsDbContext db, IModuleCatalog catalog, Invoice invoice,
        IReadOnlyList<(Guid? Id, InvoiceLineValues Values)> parsed, IReadOnlyList<bool> remember, CancellationToken ct,
        IReadOnlyList<Guid?>? newIds = null)
    {
        var existing = await db.InvoiceLines.Where(l => l.InvoiceId == invoice.Id).ToListAsync(ct);

        // Новая строка, чей идентификатор в счёте УЖЕ лежит, — та же строка, записанная прежде (повтор набора
        // с версией, прочитанной после записи, issue #1656; см. InvoiceLineRequests.NewId): она правится на
        // месте, как присланная с «id», а не заводится второй. Решается по строкам, прочитанным под замком записи, — до всех сверок ниже, чтобы и они
        // видели её лежащей.
        var born = new Guid?[parsed.Count];
        if (newIds is not null)
        {
            var laid = existing.Select(l => l.Id).ToHashSet();
            parsed = [.. parsed.Select((p, index) =>
            {
                if (p.Id is null && newIds[index] is { } given)
                {
                    if (laid.Contains(given)) return (Id: (Guid?)given, p.Values);
                    born[index] = given;
                }
                return p;
            })];
            await EnsureBornFreeAsync(db, born, ct);
        }

        // Позиции, уже стоящие в строках счёта, не перепроверяются (ТЗ CORE-34.4, issue #1184): старая
        // потеря в строке 3 не должна мешать поправить цену в строке 7. По строкам, прочитанным под
        // замком записи: набор, собранный до него, назвал бы «стоящей» позицию, которую уже убрали.
        await EnsureNomenclatureExistsAsync(catalog, parsed,
            [.. existing.Select(l => l.NomenclatureId).OfType<Guid>().Distinct()], ct);

        var kept = new HashSet<Guid>();

        // Снимок ДО правки — им отличается настоящая правка от повторной отправки того же набора.
        // Форма присылает строки целиком на каждое сохранение, и запись «строки изменены» без этой
        // сверки появлялась бы в журнале там, где не изменилось ничего: журнал заполнился бы шумом, а
        // настоящая правка в нём потерялась бы.
        var was = existing.OrderBy(l => l.Ordinal).Select(l => (l.Id, Values: l.Snapshot())).ToList();
        var now = new List<(Guid Id, CompletedLine Values)>(parsed.Count);
        var placed = new List<InvoiceLine>(parsed.Count);
        var stored = was.ToDictionary(l => l.Id, l => l.Values.Values);
        var completed = parsed.Select((p, index) => InvoiceLineRequests.Complete(p.Values, index + 1, invoice.VatMode)).ToList();
        InvoiceLineValues? Stored(Guid? lineId) => lineId is { } known ? stored.GetValueOrDefault(known) : null;

        // Пометка «запомнено» проверяется, только когда она НОВАЯ — у новой строки, с другим
        // соответствием, с другой позицией или с другим ключом строки. Стоявшую не перепроверяем:
        // соответствие с тех пор могли сменить, и это показывает состояние пометки, а не отказ
        // сохранить счёт. А вот строку, которую ПЕРЕПИСАЛИ («Кабель» → «Доставка»), прежняя пометка
        // называла бы подставленной из памяти, которой для нового текста не было (ревью PR #1262).
        await SupplierMatching.EnsureMarksAsync(db, invoice.SupplierId,
            [.. parsed.Select((p, index) => (Number: index + 1, p.Id, p.Values))
                .Where(p => p.Values.MatchedBy is not null
                    && (Stored(p.Id) is not { } before
                        || before.MatchedBy != p.Values.MatchedBy
                        || before.NomenclatureId != p.Values.NomenclatureId
                        || !SupplierMatching.SameKey(before, p.Values)))
                .Select(p => (p.Number, p.Values))], ct);

        // Что запомнить — по лежащему ДО правки: после неё «было» и «стало» совпадут.
        IReadOnlyList<MatchToRemember> worth = invoice.SupplierId is null
            ? []
            : await SupplierMatching.AliveAsync(catalog,
                SupplierMatching.Worth(parsed.Select((p, index) => (Stored(p.Id), p.Values, remember[index]))), ct);

        for (var index = 0; index < parsed.Count; index++)
        {
            var lineId = parsed[index].Id;
            var values = completed[index];

            var line = lineId is { } known
                ? existing.FirstOrDefault(l => l.Id == known)
                    ?? throw new InvalidRequestException(
                        $"Строка {index + 1}: строки {known} у этого счёта нет. Так бывает, когда форму " +
                        "оставили открытой, а строку тем временем удалили: присланное состояние опирается " +
                        "на то, чего уже нет. Перечитайте счёт и повторите правку — иначе удалённая " +
                        "строка вернулась бы молча.")
                : Added(db, invoice.Id, born[index]);

            // Пишем только то, что изменилось, — значения или место в наборе (issue #1171). Apply ставит
            // «когда правили» безусловно, и позови мы его для каждой присланной строки, правка одной
            // строки из ста давала бы сто обновлений, а время правки у девяноста девяти врало бы.
            // У новой строки место ещё нулевое, поэтому она сюда попадает всегда.
            if (line.Ordinal != index + 1 || line.Snapshot() != values) line.Apply(index + 1, values);
            kept.Add(line.Id);
            now.Add((line.Id, values));
            placed.Add(line);
        }

        db.InvoiceLines.RemoveRange(existing.Where(l => !kept.Contains(l.Id)));

        // Правка строк — правка счёта: время его правки обязано сдвинуться. Повторная отправка того же
        // набора правкой не является — ни для журнала, ни для времени.
        var changed = !was.SequenceEqual(now);
        if (changed) invoice.ContentChanged();

        return new LinesPlaced(placed, changed, worth);
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
        Guid id, CostsDbContext db, InvoiceDesk desk, IModuleActivityLog log, CancellationToken ct)
    {
        var (invoice, lines) = await desk.WriteAsync(id,
            write => MarkAsync(write.Invoice, db, desk, ct), ct);

        // Ноль строк — «уже был разобран»: решения не было, и в журнал писать нечего.
        if (lines > 0)
            await log.RecordAsync(InvoiceActions.Parsed, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), after: $"строк: {lines}", ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }

    private static async Task<(Invoice Invoice, int Lines)> MarkAsync(
        Invoice invoice, CostsDbContext db, InvoiceDesk desk, CancellationToken ct)
    {
        if (invoice.State == InvoiceState.Rejected)
            throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)} отклонён — разбирать его незачем. Верните счёт в " +
                "черновик, если решение изменилось.");

        // Уже разобран — подтверждать нечего, и в журнал не пишем: запись «счёт разобран» означает
        // решение человека, а повторное нажатие (или повтор запроса после обрыва связи) новым решением
        // не является. Так же молчит возврат в черновик у черновика.
        if (invoice.State == InvoiceState.Parsed) return (invoice, 0);

        // Условия перехода — в одном месте (issue #1291): тот же перечень ответ чтения несёт форме строкой
        // «осталось: …». Перечень пуст ⇔ переход проходит — по устройству, а не по совпадению двух проверок.
        var (readiness, lines) = await desk.ReadinessAsync(invoice, ct);
        readiness.EnsureReady();

        invoice.MarkParsed();
        await db.SaveChangesAsync(ct);
        return (invoice, lines);
    }

    /// <summary>Вернуть счёт в черновик — решением человека (см. <see cref="Invoice.ReturnToDraft" />).</summary>
    private static async Task<Ok<InvoiceView>> DraftAsync(
        Guid id, CostsDbContext db, InvoiceDesk desk, IModuleActivityLog log, CancellationToken ct)
    {
        var (invoice, from) = await desk.WriteAsync(id, async write =>
        {
            var was = write.Invoice.State;
            if (was == InvoiceState.Draft) return (write.Invoice, (InvoiceState?)null);

            write.Invoice.ReturnToDraft();
            await db.SaveChangesAsync(ct);
            return (write.Invoice, was);
        }, ct);

        // Откуда вернули — в записи: «разобранный вернули править» и «отклонённый решили всё же
        // платить» — разные решения, а действие в журнале у них одно.
        if (from is { } state)
            await log.RecordAsync(InvoiceActions.Draft, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), before: InvoiceRequisites.Label(state).ToLowerInvariant(),
                after: "решением человека", ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }

    /// <summary>
    /// «Отклонён»: по счёту не платим (ТЗ COST-9, issue #1284).
    ///
    /// <para>Отклоняют и черновик, и разобранный: решение «не платим» от того, разобран ли счёт, не
    /// зависит. <b>Оплаченный не отклоняют</b> — отказывает сама сущность (<see cref="Invoice.Reject" />);
    /// сначала отменяют оплату, и это отдельное решение со своей записью в журнале. Запертый закрытым
    /// периодом счёт сюда не доходит: он оплачен, и связка записи отказывает раньше — словами про
    /// период.</para>
    ///
    /// <para>Строки, разноска и файл остаются как были: отклонённый счёт возвращают в черновик, и
    /// разбирать его с нуля было бы наказанием за передуманное решение.</para>
    /// </summary>
    private static async Task<Ok<InvoiceView>> RejectedAsync(
        Guid id, CostsDbContext db, InvoiceDesk desk, IModuleActivityLog log, CancellationToken ct)
    {
        var (invoice, from) = await desk.WriteAsync(id, async write =>
        {
            var was = write.Invoice.State;
            // Уже отклонён — решения не было, и второй записи в журнале не будет.
            //
            // ⚠️ Повтор после оборванного ответа сюда НЕ доходит (ревью PR #1290): он называет версию,
            // которую видел до отклонения, и связка записи отказывает ему раньше — 409. Ослаблять ради
            // него сверку версии незачем: форма на такой отказ перечитывает счёт и, увидев «отклонён»,
            // говорит это, а не «не отклонён» (InvoiceMoreMenu). Сюда приходит тот, кто прочитал
            // отклонённый счёт и отклоняет его снова.
            if (was == InvoiceState.Rejected) return (write.Invoice, (InvoiceState?)null);

            write.Invoice.Reject();
            await db.SaveChangesAsync(ct);
            return (write.Invoice, was);
        }, ct);

        if (from is { } state)
            await log.RecordAsync(InvoiceActions.Rejected, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), before: InvoiceRequisites.Label(state).ToLowerInvariant(),
                after: "не платим", ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }

    /// <summary>Строки счёта, как они лежат, — в том виде, в каком их считает разноска.</summary>
    internal static async Task<IReadOnlyList<AllocationLine>> StoredLinesAsync(
        CostsDbContext db, Invoice invoice, CancellationToken ct) =>
        [.. (await db.InvoiceLines.AsNoTracking().Where(l => l.InvoiceId == invoice.Id).ToListAsync(ct))
            .Select(InvoiceAllocations.Line)];

    private static InvoiceLine Added(CostsDbContext db, Guid invoiceId, Guid? id)
    {
        var line = InvoiceLine.Create(invoiceId, id);
        db.InvoiceLines.Add(line);
        return line;
    }

    /// <summary>Первичный ключ строк счёта — его нарушение при заведении строки с «newId» и ловит запись.</summary>
    private const string LinesKey = "PK_invoice_lines";

    /// <summary>
    /// Отказ «идентификатор новой строки занят». Нейтрально, без «другого счёта» (ревью PR #1811): отказ не
    /// подтверждает, где лежит запись с этим идентификатором.
    /// </summary>
    private static string BornTaken(int? number = null) =>
        (number is { } n ? $"Строка {n}: идентификатор" : "Идентификатор") +
        " новой строки («newId») уже занят. Форма выдаёт новым строкам идентификаторы сама — перечитайте счёт и " +
        "добавьте строку заново.";

    /// <summary>
    /// Идентификатор новой строки не занят (лежащие этого счёта узнаны раньше и сюда не попадают). Без этой
    /// сверки отказала бы база, ответом 500 вместо слов; одновременную запись в другой счёт с тем же
    /// идентификатором она не видит — её ловит запись по ключу таблицы.
    /// </summary>
    private static async Task EnsureBornFreeAsync(CostsDbContext db, Guid?[] born, CancellationToken ct)
    {
        var ids = born.OfType<Guid>().ToList();
        if (ids.Count == 0) return;

        var taken = await db.InvoiceLines.AsNoTracking().Where(l => ids.Contains(l.Id)).Select(l => l.Id)
            .FirstOrDefaultAsync(ct);
        if (taken != Guid.Empty)
            throw new InvalidRequestException(BornTaken(Array.IndexOf(born, (Guid?)taken) + 1));
    }

    /// <summary>
    /// Один «newId» дважды — или «newId», равный «id» другой строки набора, — отказ своими словами (ревью
    /// PR #1811): общий отказ о повторе советует «присылать новые без id», а «id» здесь может не быть вовсе.
    /// </summary>
    private static void EnsureNewIdsDistinct(
        IReadOnlyList<(Guid? Id, InvoiceLineValues Values)> parsed, IReadOnlyList<Guid?> newIds)
    {
        var repeated = parsed
            .Select((p, index) => (Key: p.Id ?? newIds[index], Number: index + 1, New: newIds[index] is not null))
            .Where(p => p.Key is not null)
            .GroupBy(p => p.Key!.Value)
            .FirstOrDefault(g => g.Count() > 1 && g.Any(p => p.New));

        if (repeated is not null)
            throw new InvalidRequestException(
                $"Идентификатор {repeated.Key} прислан у строк {string.Join(" и ", repeated.Select(p => p.Number))}. " +
                "У каждой новой строки свой «newId», и он не совпадает с «id» строк, что уже лежат в счёте: набор " +
                "заменяет состояние целиком, и одна из двух строк исчезла бы без следа.");
    }

    /// <summary>
    /// Один и тот же идентификатор дважды — отказ. Прочитать это как «последняя побеждает» нельзя: набор
    /// заменяет состояние, и одна из двух строк исчезла бы без следа.
    /// </summary>
    internal static void EnsureIdsDistinct(IReadOnlyList<(Guid? Id, InvoiceLineValues Values)> parsed)
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
        IModuleCatalog catalog, IReadOnlyList<(Guid? Id, InvoiceLineValues Values)> parsed,
        IReadOnlyCollection<Guid> kept, CancellationToken ct)
    {
        var referenced = parsed.Select(p => p.Values.NomenclatureId)
            .OfType<Guid>()
            .Distinct()
            .Except(kept)
            .ToList();

        if (referenced.Count == 0) return;

        var verdicts = await NewReferences.JudgeAsync(catalog, CostsDirectories.Nomenclature, referenced, ct)
            ?? throw new ConflictException(
                CostsDirectories.NoNomenclature("ссылаться строкам не на что") +
                " Строки без позиции при этом сохраняются — счёт остаётся черновиком и ждёт в отборе «Разобрать».");

        var rows = parsed.Select((p, index) => (Number: index + 1, Id: p.Values.NomenclatureId)).ToList();
        var lost = NewReferences.Rows(rows, verdicts, NewReference.Missing);

        if (lost.Count > 0)
            throw new InvalidRequestException(
                $"Позиции номенклатуры нет в справочнике у {InvoiceReadiness.Genitive(lost)}" +
                ". Так бывает, когда позицию удалили или переместили в другой вид: ссылка осталась, а " +
                "записи нет. Выберите позицию заново — записать ссылку в пустоту значило бы получить " +
                "строку, которую потом никто не сведёт.");

        NewReferences.EnsureNoneArchived(rows, verdicts, "позиция номенклатуры");
    }
}
