using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Адреса счёта на оплату (задача C1 этапа 2, issue #1076, ТЗ COST-6, COST-6.2).
///
/// <para><b>Ворота стоят снаружи.</b> Всю группу модуля ядро закрывает политикой
/// <c>module:costs</c> (<c>AppModuleExtensions.MapAppModules</c>), поэтому здесь объявляются только
/// ПРАВА — чтение отдельно от записи (ТЗ COST-28). Одного доступа к модулю мало: у бухгалтера есть
/// модуль и нет права заводить счета.</para>
///
/// <para>⚠️ <b>Чего здесь нет и почему.</b> Ни перехода «черновик → разобран», ни отметки оплаты, ни
/// подстановки срока: это C2, C4 и C5. Первый PR задачи кладёт ХРАНЕНИЕ и адреса — то, на чём стоит
/// форма, — и кладёт целиком, вместе с метками «распознано, не подтверждено», потому что метка,
/// приколоченная во втором PR, не пережила бы перезагрузку страницы и не стерегла бы ничего.</para>
/// </summary>
public static class InvoiceEndpoints
{
    private const string Read = "costs.invoice.read";
    private const string Edit = "costs.invoice.edit";

    /// <summary>Как называется счёт в журнале действий и в отказах.</summary>
    internal static string Label(Invoice invoice) =>
        $"Счёт {invoice.Number ?? "без номера"}" +
        (invoice.IssuedOn is { } date ? $" от {date:dd.MM.yyyy}" : string.Empty);

    public static void MapInvoices(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/costs/invoices").WithTags("Счета на оплату");

        group.MapGet("/", ListAsync).RequireAuthorization(AppPolicies.Permission(Read));
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(AppPolicies.Permission(Read));
        group.MapGet("/{id:guid}/scan", ScanAsync).RequireAuthorization(AppPolicies.Permission(Read));

        group.MapPost("/", CreateAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPost("/{id:guid}/confirmed", ConfirmAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPost("/{id:guid}/scan", AttachScanAsync)
            .RequireAuthorization(AppPolicies.Permission(Edit))
            .DisableAntiforgery();
    }

    /// <summary>
    /// Реестр счетов — плоским списком, без реквизитов (ТЗ COST-9.1: отбор по сроку и состоянию).
    ///
    /// <para>Названия поставщиков берутся ОДНИМ обращением к справочнику и раскладываются по
    /// идентификаторам: запрос на строку превратил бы открытие реестра в сотню обращений, и заметно
    /// это стало бы у заказчика, а не здесь.</para>
    ///
    /// <para>⚠️ Постраничности здесь пока нет, и это named-решение, а не недосмотр: экран реестра со
    /// своей сеткой, отбором и сохранёнными представлениями приезжает задачей G4 (issue #1097), и
    /// страницы обязаны совпасть с тем, что попросит сетка. Отдай мы сейчас произвольные первые сто,
    /// «счёт есть, а в списке его нет» стало бы поведением, на которое кто-нибудь успел бы
    /// опереться.</para>
    /// </summary>
    /// <param name="needsParsing">Отбор «Разобрать» (ТЗ COST-6.2): только счета, у которых есть строки
    /// без позиции номенклатуры. Отбором, а не сортировкой: это рабочая очередь снабженца, и счета,
    /// которые разбирать не надо, в ней мешают.
    ///
    /// <para>⚠️ Счёт БЕЗ строк вовсе в этот отбор НЕ попадает, хотя разбирать его тоже надо. Причина:
    /// «строк нет» и «строки ждут позиции» — разные работы, и вторую делают по скану, который уже
    /// разобран наполовину. Очередь «строк нет вовсе» — это отбор по состоянию «черновик», он приезжает
    /// с сеткой реестра (G4, issue #1097) вместе с остальными сохранёнными представлениями.</para>
    ///
    /// <para>⚠️ ОТКЛОНЁННЫЙ счёт в отбор тоже не попадает, сколько бы строк у него ни ждало позиции:
    /// «не платим» — значит и не разбираем, переход «разобран» на нём отвечает отказом. Очередь, в
    /// которой стоит то, что разобрать нельзя, перестаёт быть очередью (issue #1166).</para></param>
    private static async Task<Ok<IReadOnlyList<InvoiceListItem>>> ListAsync(
        CostsDbContext db, IModuleCatalog catalog, CancellationToken ct, bool needsParsing = false)
    {
        // Очередь «Разобрать» отбирает БАЗА (issue #1171), и запрос идёт от строк без позиции, а не от
        // счетов: так частичный индекс ix_invoice_lines_unmatched — он заведён ровно под этот отбор —
        // получает работу. Раньше реестр читал все счета и отбирал очередь в памяти: индекс при этом не
        // читал никто, он только удорожал запись строк.
        var selected = db.Invoices.AsNoTracking();
        if (needsParsing)
            selected = selected.Where(i => i.State != InvoiceState.Rejected
                && db.InvoiceLines.Where(l => l.NomenclatureId == null).Select(l => l.InvoiceId).Contains(i.Id));

        var invoices = await selected
            .OrderByDescending(i => i.IssuedOn)
            .ThenByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        // Счётчики строк — ОДНИМ группирующим запросом, без чтения самих строк: на экране нужны два
        // числа на счёт, а не строки. Запрос на счёт превратил бы открытие реестра в сотню обращений —
        // той же ценой, что уже названа у названий поставщиков. У очереди — только по её счетам:
        // группировать всю таблицу ради десятка счетов незачем.
        var counted = db.InvoiceLines.AsNoTracking();
        if (needsParsing)
        {
            var queued = invoices.Select(i => i.Id).ToList();
            counted = counted.Where(l => queued.Contains(l.InvoiceId));
        }

        var counters = await counted
            .GroupBy(l => l.InvoiceId)
            .Select(g => new
            {
                InvoiceId = g.Key,
                Count = g.Count(),
                Unmatched = g.Count(l => l.NomenclatureId == null),
            })
            .ToListAsync(ct);

        // Словарём «счёт → (строк, ждут позиции)». Сумм здесь нет НАРОЧНО: реестру они не нужны, а
        // подставленный нуль в поле суммы — это неправда, на которую кто-нибудь однажды сошлётся.
        var lines = counters.ToDictionary(c => c.InvoiceId, c => (Count: c.Count, Unmatched: c.Unmatched));

        var names = await SupplierNamesAsync(catalog, invoices, ct);

        return TypedResults.Ok<IReadOnlyList<InvoiceListItem>>(
            [.. invoices.Select(i => InvoiceViews.Item(
                i,
                i.SupplierId is { } id && names.TryGetValue(id, out var name) ? name : null,
                lines.TryGetValue(i.Id, out var total) ? total.Count : 0,
                lines.TryGetValue(i.Id, out var waiting) ? waiting.Unmatched : 0))]);
    }

    private static async Task<Ok<InvoiceView>> GetAsync(
        Guid id, CostsDbContext db, InvoiceDesk desk, CancellationToken ct)
    {
        var invoice = await FindAsync(db, id, ct);
        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }

    /// <summary>
    /// Завести счёт. Сохраняется ЧЕРНОВИКОМ и сохраняется всегда: ни строк, ни плательщика, ни суммы
    /// для этого не нужно (ТЗ COST-6.2).
    ///
    /// <para>Так требует жизнь документа: счёт приезжает сканом, его заводит фоновое распознавание, и
    /// человек открывает его потом. Запрети мы сохранение без строк — черновику негде было бы
    /// жить.</para>
    ///
    /// <para>Мимо связки записи (<see cref="InvoiceDesk.WriteAsync{T}" />) — и это названо в переписи
    /// путей: новый счёт не оплачен, а неоплаченный не принадлежит ни одному периоду.</para>
    /// </summary>
    private static async Task<Created<InvoiceView>> CreateAsync(
        InvoiceSaveRequest body, CostsDbContext db, IModuleTypes types, IModuleUser user,
        IModuleActivityLog log, IModuleWriteGuard guard, InvoiceDesk desk, IModuleReferenceTargets targets,
        CancellationToken ct)
    {
        var typeId = await types.FindAsync(CostsRecordTypes.InvoiceCode, ct)
            ?? throw new ConflictException(
                $"Тип «{CostsRecordTypes.InvoiceCode}» в системе не заведён, поэтому счёт завести " +
                "нечем: без типа у записи не было бы ни схемы полей, ни печати. Тип заводит запуск " +
                "приложения — причину пропуска он называет в журнале запуска (обычно код или имя " +
                "типа занял другой тип).");

        var (columns, rest) = InvoiceRequisites.Split(body.Requisites, stored: null);
        await EnsureAllowedAsync(guard, typeId, stored: null, body.Requisites.GetRawText(), ct);
        await EnsurePartiesExistAsync(targets, columns, supplierWas: null, payerWas: null, ct);

        var invoice = Invoice.Create(typeId, user.Id);
        var marks = body.Unconfirmed ?? [];

        // Срок, пришедший из распознавания, ручным не считается — и отличим он ровно меткой: «оплатить
        // до» стоит в бумаге поставщика, а правило подстановки (C4) обходит стороной только тот срок,
        // который вписал человек.
        invoice.Apply(columns, rest, dueDateByHand: !marks.Contains(InvoiceRequisites.DueDateKey));
        if (marks.Count > 0) invoice.MarkUnconfirmed(marks);

        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);

        // Журнал — ПОСЛЕ сохранения своего: общей транзакции у контекста модуля и таблиц ядра нет
        // (A2a, issue #1072). Порядок обязателен: запись в журнал о счёте, который не сохранился,
        // была бы свидетельством о том, чего не произошло.
        await log.RecordAsync(InvoiceActions.Created, invoice.Id.ToString(), Label(invoice), ct: ct);

        return TypedResults.Created(
            $"/api/costs/invoices/{invoice.Id}",
            await desk.ViewAsync(invoice, ct));
    }

    /// <summary>
    /// Правка счёта. Здесь же кончаются метки «распознано, не подтверждено» у тех полей, значение
    /// которых изменилось (решение владельца 29.09.2026).
    ///
    /// <para>⚠️ Метка снимается ПРАВКОЙ, а не сохранением. Разница видна только на фоновом сценарии:
    /// <c>D4</c> сохраняет черновик до того, как его увидел человек, — и если бы метки снимало
    /// сохранение, распознанный счёт приезжал бы уже «проверенным».</para>
    /// </summary>
    private static async Task<Ok<InvoiceView>> UpdateAsync(
        Guid id, InvoiceSaveRequest body, CostsDbContext db, IModuleActivityLog log,
        IModuleWriteGuard guard, InvoiceDesk desk, AllocationPlacesSource places,
        IModuleReferenceTargets targets, CancellationToken ct)
    {
        if (body.Unconfirmed is not null)
            throw new InvalidRequestException(
                "Метки «распознано, не подтверждено» правкой счёта не задаются: их ставит тот, кто " +
                "заполнил поля (распознавание), а снимает правка поля или действие «Всё верно» " +
                "(адрес «/confirmed»). Иначе форма, приславшая метки заново, возвращала бы снятые.");

        var (invoice, changed, reason) = await desk.WriteAsync(id, async write =>
        {
            var invoice = write.Invoice;
            var before = InvoiceRequisites.Merge(invoice);

            var (columns, rest) = InvoiceRequisites.Split(body.Requisites, before);
            await EnsureAllowedAsync(guard, invoice.DocumentTypeId, before.ToJsonString(),
                InvoiceRequisites.Resulting(body.Requisites, before).ToJsonString(), ct);

            await EnsurePartiesExistAsync(targets, columns, invoice.SupplierId, invoice.PayerId, ct);

            var changed = InvoiceRequisites.Changed(before, body.Requisites);

            // Правка — всегда человек: метки этот адрес не принимает (отказ выше), то есть фоновому
            // заполнению сюда дороги нет.
            invoice.Apply(columns, rest, dueDateByHand: true);
            invoice.Confirm(changed);

            // Разобранный счёт, переставший отвечать условию «разобран», САМ возвращается в черновик — как
            // при правке строк и разноски. Шапка задевает условие дважды: обязательным полем, которое
            // стёрли, и суммой к оплате — с F1 (#1085) от неё зависит, сходится ли разноска.
            var reason = invoice.State != InvoiceState.Parsed ? null
                : InvoiceRequisites.Missing(invoice) is { Count: > 0 } missing
                    ? "не заполнено обязательное — " + string.Join(", ", missing.Select(m => $"«{m}»"))
                : !await InvoiceAllocations.AllocatedAfterAsync(db, places, invoice,
                    await InvoiceLineEndpoints.StoredLinesAsync(db, invoice, ct), ct)
                    ? "баланс разноски не сходится"
                : null;
            if (reason is not null) invoice.ReturnToDraft();

            await db.SaveChangesAsync(ct);
            return (invoice, changed, reason);
        }, ct);

        if (changed.Count > 0)
            await log.RecordAsync(InvoiceActions.Changed, invoice.Id.ToString(), Label(invoice),
                before: string.Join(", ", changed), ct: ct);

        if (reason is not null)
            await log.RecordAsync(InvoiceActions.Draft, invoice.Id.ToString(), Label(invoice),
                after: $"правка счёта: {reason}", ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }

    /// <summary>
    /// «Всё верно»: снять метки с полей блока (решение владельца 29.09.2026).
    ///
    /// <para>Своим адресом, а не полем в правке: это отдельное действие человека, у него своя запись
    /// в журнале, и происходит оно БЕЗ изменения значений — то есть отличить его от правки по
    /// содержимому запроса было бы нечем.</para>
    /// </summary>
    private static async Task<Ok<InvoiceView>> ConfirmAsync(
        Guid id, InvoiceConfirmRequest body, CostsDbContext db, IModuleActivityLog log,
        InvoiceDesk desk, CancellationToken ct)
    {
        if (body.Fields is not { Count: > 0 })
            throw new InvalidRequestException(
                "«Всё верно» без перечня полей — отказ. Поля блока называет клиент: блок — это видимая " +
                "группа формы, и сервер о ней не знает. Пустой перечень прочитать как «снять все» " +
                "нельзя: метки исчезли бы разом, а вернуть их было бы нечем.");

        var (invoice, cleared) = await desk.WriteAsync(id, async write =>
        {
            var cleared = write.Invoice.Confirm(body.Fields);
            if (cleared > 0) await db.SaveChangesAsync(ct);
            return (write.Invoice, cleared);
        }, ct);

        if (cleared > 0)
            await log.RecordAsync(InvoiceActions.Confirmed, invoice.Id.ToString(), Label(invoice),
                after: string.Join(", ", body.Fields), ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }

    /// <summary>
    /// Приложить скан: счёт приходит и файлом, и сканом (ТЗ COST-5), и форма показывает его РЯДОМ с
    /// полями.
    ///
    /// <para>Файл уходит в хранилище ядра портом — своего хранилища у модуля нет и не будет: два
    /// хранилища в продукте означали бы две резервные копии, из которых сходится одна.</para>
    ///
    /// <para>⚠️ <b>К запертому счёту скан приложить можно, заменить — нельзя</b> (решение владельца
    /// 04.10.2026): приложить — не правка цифр, а замена удаляет прежний файл, то есть первичку
    /// закрытого периода.</para>
    ///
    /// <para>⚠️ Файл уходит в хранилище ДО связки записи: внутри неё выгрузка держала бы замок, и
    /// закрытие периода упиралось бы в свой срок ожидания на всё время передачи большого скана.
    /// Отказала запись — выгруженный файл убирается.</para>
    /// </summary>
    private static async Task<Ok<InvoiceView>> AttachScanAsync(
        Guid id, IFormFile file, CostsDbContext db, IModuleBlobs blobs, IModuleActivityLog log,
        InvoiceDesk desk, CancellationToken ct)
    {
        if (file.Length == 0)
            throw new InvalidRequestException(
                "Файл пуст. Пустой скан прикладывать не к чему: в форме он выглядел бы приложенным, а " +
                "показать было бы нечего.");

        // Счёта нет или форма устарела — отказ до выгрузки: иначе файл ушёл бы в хранилище целиком ради
        // ответа «не найден» или «счёт изменили». Решает по-прежнему связка ниже — это её ранний повтор.
        await desk.EnsureSeenAsync(id, ct);

        string path;
        await using (var content = file.OpenReadStream())
            path = await blobs.PutAsync(file.FileName, content, file.ContentType ?? "application/octet-stream", ct);

        Invoice invoice;
        string? replaced;
        try
        {
            (invoice, replaced) = await desk.WriteAsync(id, async write =>
            {
                var replaced = write.Invoice.ScanBlobPath;
                if (write.Locked is { } locked && replaced is not null)
                    throw new ConflictException(
                        $"{Label(write.Invoice)} заперт: {locked}. Заменить скан нельзя: прежний файл — документ " +
                        "закрытого периода, и замена его удалила бы.");

                write.Invoice.AttachScan(path, file.FileName, file.ContentType ?? "application/octet-stream", file.Length);
                await db.SaveChangesAsync(ct);
                return (write.Invoice, replaced);
            }, ct, evenLocked: true);
        }
        catch
        {
            // Уборка не вправе подменить причину: откажи здесь хранилище, человек получил бы его ошибку
            // вместо «заменить скан нельзя». Неубранный файл подберёт уборка осиротевших.
            try { await blobs.DeleteAsync(path, CancellationToken.None); }
            catch (Exception) { }
            throw;
        }

        // Прежний скан убираем из хранилища — ПОСЛЕ того, как запись о новом сохранилась. Порядок
        // обязателен: удали мы первым, отказ сохранения оставил бы счёт со ссылкой на файл, которого
        // больше нет. А не удалять вовсе значило бы, что каждая замена скана оставляет в хранилище
        // файл, на который никто не ссылается: заметно это стало бы по счёту за место, и объяснить
        // накопленное было бы нечем.
        if (replaced is not null && replaced != path)
            await blobs.DeleteAsync(replaced, ct);

        await log.RecordAsync(InvoiceActions.ScanAttached, invoice.Id.ToString(), Label(invoice),
            after: file.FileName, ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }

    /// <summary>
    /// Отдать скан. Потоком из хранилища, а не ссылкой на него: у файла в хранилище нет прав, а у
    /// этого адреса есть — <c>costs.invoice.read</c>.
    /// </summary>
    private static async Task<IResult> ScanAsync(
        Guid id, CostsDbContext db, IModuleBlobs blobs, CancellationToken ct)
    {
        var invoice = await FindAsync(db, id, ct);

        if (invoice.ScanBlobPath is not { } path)
            throw new NotFoundException($"{Label(invoice)}: скан не приложен.");

        var content = await blobs.OpenAsync(path, ct);
        return Results.File(content, invoice.ScanMimeType ?? "application/octet-stream",
            invoice.ScanFileName);
    }

    /// <summary>
    /// Спросить охрану записи ядра — ПЕРЕД сохранением (ТЗ CORE-20, порт issue #1069).
    ///
    /// <para>Зачем модулю спрашивать то, что ядро проверяет само: ядро проверяет пути, которые ведёт
    /// ОНО, а запись в таблице модуля оно не сохраняет и проверить не может. Правила при этом те же —
    /// вид значения и запертые поля, — и заказчик вправе дописать в этот тип свои поля (уровень
    /// «расширяемый»), значения которых иначе не проверял бы никто.</para>
    ///
    /// <para>⚠️ На правке охране уходит запись, какой она СТАНЕТ (<c>InvoiceRequisites.Resulting</c>), а
    /// не присланная часть: отсутствующее запертое поле охрана читает как стёртое, и правка счёта
    /// отказывала бы всегда — состояния форма не присылает и присылать не должна. При создании,
    /// наоборот, уходит присланное как есть: лежащего нет, всё считается внесённым впервые, а запертое
    /// поле нельзя заполнить даже впервые.</para>
    ///
    /// <para>⚠️ Отказы порт ВОЗВРАЩАЕТ, а не бросает: его отказ живёт в слое, на который модуль не
    /// ссылается. Решение — наше, и оно такое же, как у ядра: доменный отказ с перечислением полей,
    /// который приложение превратит в 400. Тихо пропустить их значило бы записать значение, которое
    /// форма потом не нарисует.</para>
    /// </summary>
    private static async Task EnsureAllowedAsync(
        IModuleWriteGuard guard, Guid typeId, string? stored, string incoming, CancellationToken ct)
    {
        var refusals = await guard.RefusalsAsync(typeId, stored, incoming, ct);
        if (refusals.Count == 0) return;

        throw new InvalidRequestException(
            "Счёт не сохранён — охрана записи: " + string.Join(" ", refusals.Select(
                r => r.Path is { Length: > 0 } path ? $"«{path}»: {r.Message}" : r.Message)));
    }

    /// <summary>
    /// Названия позиций номенклатуры одним обращением на весь счёт — либо <c>null</c>, если вида
    /// «Номенклатура» в системе нет вовсе.
    ///
    /// <para>⚠️ Пустой словарь и <c>null</c> — РАЗНОЕ. Пустой означает «спросили, ничего не нашлось»
    /// (позиции удалены), <c>null</c> — «спрашивать не у кого». Сведи их в одно, и в установке без
    /// типа «Номенклатура» каждая ссылка счёта выглядела бы битой.</para>
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, string?>?> NomenclatureNamesAsync(
        IModuleCatalog catalog, IReadOnlyList<InvoiceLine> lines, CancellationToken ct)
    {
        var ids = lines.Select(l => l.NomenclatureId).OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string?>();

        var refs = await catalog.RefsAsync(CostsRecordTypes.NomenclatureCode, ids, ct);
        return refs?.ToDictionary(r => r.Id, r => r.DisplayName);
    }

    /// <summary>
    /// Поставщик и плательщик, названные ЭТОЙ правкой, обязаны существовать (ТЗ CORE-34.4, issue #1184).
    ///
    /// <para>Только новые: ссылка, которая у счёта уже стояла, принимается и потерянной — иначе счёт с
    /// удалённым поставщиком нельзя было бы сохранить, поправив в нём что угодно другое. А новая ссылка
    /// в пустоту — это потеря, заведённая своими руками.</para>
    /// </summary>
    private static async Task EnsurePartiesExistAsync(
        IModuleReferenceTargets targets, InvoiceColumns columns, Guid? supplierWas, Guid? payerWas, CancellationToken ct)
    {
        var named = new[]
            {
                (Key: InvoiceRequisites.SupplierKey, Id: columns.SupplierId, Was: supplierWas),
                (Key: InvoiceRequisites.PayerKey, Id: columns.PayerId, Was: payerWas),
            }
            .Where(p => p.Id is not null && p.Id != p.Was)
            .ToList();
        if (named.Count == 0) return;

        var states = await targets.StatesAsync(ReferenceTarget.Record, [.. named.Select(p => p.Id!.Value)], ct);
        foreach (var party in named.Where(p => states[p.Id!.Value] == ReferenceState.Lost))
            throw new InvalidRequestException(
                $"«{party.Key}»: такой записи в справочнике нет. Так бывает, когда организацию удалили, пока " +
                "форма была открыта. Выберите организацию заново — записать ссылку в пустоту значило бы " +
                "получить счёт, у которого поставщика не узнать.");
    }

    internal static async Task<Invoice> FindAsync(CostsDbContext db, Guid id, CancellationToken ct) =>
        await db.Invoices.FirstOrDefaultAsync(i => i.Id == id, ct)
        ?? throw new NotFoundException("Счёт не найден.");

    /// <summary>
    /// Дубликаты по поставщику, номеру и дате (ТЗ COST-6.2) — оговорка, а не запрет.
    ///
    /// <para>Ищутся только когда все три части заполнены: у черновика без номера «дубликатом» оказался
    /// бы каждый второй черновик, и оговорка, звучащая без повода, перестаёт значить что-либо.</para>
    /// </summary>
    internal static async Task<IReadOnlyList<InvoiceDuplicate>> DuplicatesAsync(
        CostsDbContext db, Invoice invoice, CancellationToken ct)
    {
        if (invoice.SupplierId is not { } supplier || invoice.Number is not { Length: > 0 } number
            || invoice.IssuedOn is not { } issued)
            return [];

        var found = await db.Invoices
            .AsNoTracking()
            .Where(i => i.Id != invoice.Id && i.SupplierId == supplier && i.Number == number
                && i.IssuedOn == issued)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);

        return [.. found.Select(InvoiceViews.Duplicate)];
    }

    private static async Task<Dictionary<Guid, string>> SupplierNamesAsync(
        IModuleCatalog catalog, List<Invoice> invoices, CancellationToken ct)
    {
        if (!invoices.Any(i => i.SupplierId is not null)) return [];

        // Спрашиваем ТОЛЬКО когда есть что разрешать (условие выше). Это не экономия запроса: вида
        // «Организация» на чистой установке может не быть вовсе — тип заводит человек, — и порт на
        // неизвестный вид отвечает отказом. Счетов без этого типа тоже нет (их не завести), поэтому
        // пустой реестр остаётся пустым реестром, а не превращается в отказ.
        //
        // Вид записи — КОД ТИПА, тот же, которым названа цель поля «Поставщик». В списке приходят и
        // записи подтипов: подтип организации — организация, и живые поставщики заказчика лежат
        // именно подтипом.
        // Вида нет (ноль) — значит и организаций нет: реестр остаётся реестром, а не превращается в
        // отказ. Счёт со ссылкой на организацию при этом покажет «организация не найдена» — что
        // правда: тип, на который он ссылается, из системы исчез.
        var organizations = await catalog.ListAsync(CostsRecordTypes.OrganizationCode, ct);
        return organizations?.ToDictionary(o => o.Id, o => o.DisplayName) ?? [];
    }
}
