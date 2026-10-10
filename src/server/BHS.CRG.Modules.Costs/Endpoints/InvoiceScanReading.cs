using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Files;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Работа фоновой задачи распознавания: прочитать скан и разложить прочитанное по черновику (ТЗ COST-8,
/// задача B1b, issue #1077).
///
/// <para><b>Распознанное — предложение.</b> В счёт ложится только то, что человеку не мешает: пустые
/// поля шапки и строки — если своих строк у счёта нет. Занятое поле не затирается: человек мог начать
/// заполнять черновик, пока скан читался, и его значение побеждает. Всё, что легло, помечено «не
/// подтверждено»; всё, что не легло, сохранено рядом и показывается предложением.</para>
///
/// <para><b>Неудача — не пустой черновик.</b> Отказ порта распознавания записывается причиной, а в
/// счёт не пишется ничего: ни поля, ни метки.</para>
///
/// <para>⚠️ Отдельно от <see cref="InvoiceScanRecognition" /> не ради порядка: та ставит задачу в
/// очередь, а очередь знает своих исполнителей. Живи чтение в ней же, исполнитель зависел бы от
/// очереди, которая зависит от него, — и приложение не собрало бы ни одного из них.</para>
/// </summary>
public sealed class InvoiceScanReading(
    CostsDbContext db, InvoiceDesk desk, IModuleRecognition recognition, InvoiceScanImage image,
    IModuleWriteGuard guard, IModuleActivityLog log, ILoggerFactory logs, InvoiceParties parties,
    IModuleCatalog catalog)
{
    /// <summary>Сколько раз слияние повторяется, проиграв одновременной правке формы.</summary>
    private const int MergeAttempts = 3;

    /// <summary>
    /// Работа фоновой задачи: прочитать скан и разложить прочитанное по черновику. Любой отказ
    /// записывается причиной и пробрасывается — задача завершается отказом с тем же текстом.
    /// </summary>
    public async Task RunAsync(Guid invoiceId, Guid jobId, string? scanBlobPath, CancellationToken ct)
    {
        var stored = await db.InvoiceRecognitions.FirstOrDefaultAsync(r => r.InvoiceId == invoiceId, ct)
            ?? throw new ConflictException("Распознавать нечего: счёт удалён или распознавание по нему не ставили.");
        if (stored.Outcome != InvoiceRecognitionOutcome.Pending)
            throw new ConflictException("Распознавание по этому счёту уже завершено другой попыткой.");

        // Свой номер задача знает сама и вписывает его, если запись его не несёт: постановка сохраняет
        // номер ПОСЛЕ очереди, и между ними запись могли перезапустить вторым нажатием. Без номера
        // форма читала бы работающую задачу как «прервано».
        if (stored.JobId != jobId)
        {
            stored.Queued(jobId);
            await db.SaveChangesAsync(ct);
        }

        ModuleRecognitionResult read;
        (string Label, int Filled, int Lines, int Recalled) merged;
        try
        {
            var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
                ?? throw new ConflictException("Счёт удалён, пока его файл ждал распознавания.");
            EnsureSameScan(invoice, scanBlobPath);

            // Что читать, говорит ядро — по самому файлу, а не по записи счёта (issue #1265, #1270):
            // PDF и изображение уходят движку как есть, у Excel и Word — их читаемый образ, а отказ
            // построить его становится исходом распознавания, со словами службы. Запись счёта здесь
            // не годится: у давно приложенного файла это заголовок клиента.
            var (content, kind, readImage) = await image.ReadableAsync(invoice.ScanBlobPath!, ct);

            read = await recognition.RecognizeAsync(CostsRecognitionProfiles.InvoiceCode, content, kind, ct);
            // Стороны сопоставляются ДО слияния и один раз: обход справочника под замком счёта держал бы
            // замок зря и повторялся бы с каждой попыткой слияния. Отказом он не отвечает — справочник,
            // который не ответил, даёт состояние «сопоставить не удалось», и шапка со строками ложатся
            // в счёт без сторон.
            var matched = await parties.MatchAsync(read.Fields, ct);
            var lines = await LinesAsync(invoice, read, matched, ct);
            merged = await MergeAsync(invoiceId, scanBlobPath, read, matched, lines, readImage, ct);
        }
        catch (DomainException refusal)
        {
            await FailAsync(invoiceId,
                refusal is RecognitionRefusedException refused ? refused.Reason.ToString() : "Refused", refusal.Message);
            throw;
        }
        catch (Exception crash) when (crash is not OperationCanceledException)
        {
            // Не наш отказ — сбой базы или хранилища. Исход всё равно обязан быть записан: иначе запись
            // ждала бы вечно, а форма показывала «прервано» без единого слова о том, что случилось.
            // Чужой текст человеку не отдаём — он в журнале сервера, вместе с самим исключением.
            await FailAsync(invoiceId, "Refused",
                "Прочитанное не удалось записать в счёт: внутренняя ошибка. Запустите распознавание ещё раз; " +
                "если повторится — подробности в журнале сервера.");
            throw;
        }

        // Журнал — ПОСЛЕ исхода и вне его: счёт уже заполнен, и отказ журнала не вправе ни объявить
        // задачу упавшей, ни переписать «прочитано» на «не удалось».
        try
        {
            await log.RecordAsync(InvoiceActions.Recognized, invoiceId.ToString(), merged.Label,
                after: $"полей: {merged.Filled}, строк: {merged.Lines}"
                    + (merged.Recalled > 0 ? $", из них с позицией из запомненного: {merged.Recalled}" : string.Empty)
                    + (read.Engine is { } engine ? $"; {engine}" : string.Empty),
                ct: ct);
        }
        catch (Exception lost) when (lost is not OperationCanceledException)
        {
            logs.CreateLogger<InvoiceScanReading>().LogError(lost,
                "Счёт {InvoiceId} заполнен из файла, но запись об этом в журнал действий не легла.", invoiceId);
        }
    }

    /// <summary>
    /// Разложить прочитанное по счёту. Проиграв одновременной правке формы, слияние ПОВТОРЯЕТСЯ: счёт
    /// перечитывается, и то же прочитанное раскладывается заново — уже мимо поля, которое человек
    /// только что заполнил. Выбросить прочитанное значило бы платить движку второй раз за тот же файл.
    /// </summary>
    private async Task<(string Label, int Filled, int Lines, int Recalled)> MergeAsync(
        Guid invoiceId, string? scanBlobPath, ModuleRecognitionResult read, InvoicePartiesView matched,
        ScanLines lines, string? readImage, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await desk.MergeAsync(invoiceId, async write =>
                {
                    // Счёт пришёл СВЕЖИМ, под замком: пока скан читался, его могли править, разобрать,
                    // заменить ему скан. Условие проверяется заново — и против того же файла.
                    EnsureSameScan(write.Invoice, scanBlobPath);
                    var stored = await db.InvoiceRecognitions.FirstAsync(r => r.InvoiceId == invoiceId, ct);
                    var applied = await ApplyAsync(write.Invoice, stored, read, matched, lines, readImage, ct);
                    await db.SaveChangesAsync(ct);
                    return (InvoiceEndpoints.Label(write.Invoice), applied.Filled, applied.Lines, applied.Recalled);
                }, ct);
            }
            catch (ConflictException lost) when (lost.InnerException is DbUpdateConcurrencyException && attempt < MergeAttempts)
            {
                // Несохранённое уходит целиком: следующая попытка читает счёт и запись заново.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Строки скана и запомненное для них — по каждому поставщику, который может встать в счёт.</summary>
    /// <param name="Recalled">Ответ по поставщику. Пусто — спрашивать было не о ком или нечем.</param>
    /// <param name="RecallFailed">Спросить запомненное НЕ УДАЛОСЬ — это не «ничего не запомнено».</param>
    private sealed record ScanLines(
        List<InvoiceLineValues> Rows, IReadOnlyDictionary<Guid, RecallAnswer> Recalled, bool RecallFailed);

    /// <summary>
    /// Разобрать строки скана и спросить запомненное для них (задача C3, issue #1079) — ДО слияния и
    /// один раз, как и стороны: обход справочника под замком счёта держал бы замок зря и повторялся бы
    /// с каждой попыткой.
    ///
    /// <para>Кто встанет поставщиком, до замка неизвестно: поле могут заполнить, пока скан читается.
    /// Поэтому спрашиваем обоих возможных — стоящего в счёте сейчас и найденного сканом по ИНН; слияние
    /// возьмёт ответ того, кто в счёте окажется. Третий (человек успел выбрать иного) останется без
    /// подстановки — его строкам её даст кнопка формы.</para>
    ///
    /// <para>⚠️ <b>Отказ здесь не смеет стать отказом распознавания.</b> Запомненное — справка к счёту:
    /// прочитанное движком оплачено, и терять шапку со строками из-за того, что не ответил справочник,
    /// нельзя (ревью PR #1263). Отказ записывается в журнал сервера и называется в примечаниях.</para>
    /// </summary>
    private async Task<ScanLines> LinesAsync(
        Invoice invoice, ModuleRecognitionResult read, InvoicePartiesView matched, CancellationToken ct)
    {
        var rows = read.Rows.Select(Line).OfType<InvoiceLineValues>().ToList();
        var recalled = new Dictionary<Guid, RecallAnswer>();
        if (rows.Count == 0) return new(rows, recalled, false);

        try
        {
            foreach (var supplier in new[] { invoice.SupplierId, matched.Supplier?.Match }.OfType<Guid>().Distinct())
                recalled[supplier] = await SupplierMatching.RecallAsync(db, catalog, supplier, rows, ct);
            return new(rows, recalled, false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            logs.CreateLogger<InvoiceScanReading>().LogWarning(failure,
                "Счёт {InvoiceId}: запомненные позиции для строк файла спросить не удалось — строки лягут без них.",
                invoice.Id);
            return new(rows, new Dictionary<Guid, RecallAnswer>(), true);
        }
    }

    private async Task FailAsync(Guid invoiceId, string reason, string error)
    {
        // Отказ мог прийти из середины записи — несохранённое в счёт уйти не должно. И без токена
        // отмены: исход пишется и тогда, когда задачу остановили.
        db.ChangeTracker.Clear();
        var failed = await db.InvoiceRecognitions.FirstAsync(r => r.InvoiceId == invoiceId, CancellationToken.None);
        failed.Fail(reason, error);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static void EnsureSameScan(Invoice invoice, string? scanBlobPath)
    {
        if (invoice.ScanBlobPath != scanBlobPath)
            throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)}: файл заменили, пока прежний распознавался. Прочитанное " +
                "относится к прежнему файлу и в счёт не записано — запустите распознавание ещё раз.");
        if (InvoiceScanRecognition.WhyNot(invoice) is { } whyNot)
            throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)}: распознанное не записано — {whyNot}.");
    }

    /// <summary>Шапка — в пустые поля, строки — в счёт без строк; остальное — предложением.</summary>
    private async Task<(int Filled, int Lines, int Recalled)> ApplyAsync(
        Invoice invoice, InvoiceRecognition stored, ModuleRecognitionResult read, InvoicePartiesView matched,
        ScanLines lines, string? readImage, CancellationToken ct)
    {
        var before = InvoiceRequisites.Merge(invoice);
        var after = before.DeepClone().AsObject();
        var filled = new List<string>();
        var offers = new JsonObject();
        var notes = new List<string>();

        void Put(string profileKey, string requisiteKey, string title, Func<string, JsonNode?> parse)
        {
            // Поле убрали из профиля — его не спрашивали, и говорить «в скане нет» не о чем.
            if (!read.Fields.TryGetValue(profileKey, out var text) || text is null) return;

            var value = parse(text);
            if (value is null)
            {
                offers[requisiteKey] = text;
                notes.Add($"{title}: «{text}» не прочитано как значение поля — впишите вручную.");
                return;
            }

            var was = before.TryGetPropertyValue(requisiteKey, out var node) ? node : null;
            if (!IsBlank(was))
            {
                // Занято: значение человека побеждает. Совпало с распознанным — и предлагать нечего.
                if (!JsonNode.DeepEquals(was, value)) offers[requisiteKey] = text;
                return;
            }

            after[requisiteKey] = value;
            filled.Add(requisiteKey);
        }

        Put(CostsRecognitionProfiles.Number, InvoiceRequisites.NumberKey, "Номер счёта",
            text => text.Length <= Invoice.NumberLength ? JsonValue.Create(text) : null);
        Put(CostsRecognitionProfiles.Date, InvoiceRequisites.DateKey, "Дата счёта",
            text => RecognizedValues.Date(text) is { } date
                ? JsonValue.Create(date.ToString(CostsValues.DateFormat, System.Globalization.CultureInfo.InvariantCulture))
                : null);
        Put(CostsRecognitionProfiles.Basis, InvoiceRequisites.PurposeKey, "Основание", text => JsonValue.Create(text));
        Put(CostsRecognitionProfiles.Total, InvoiceRequisites.TotalKey, "Сумма к оплате",
            text => RecognizedValues.Money(text) is { } money ? JsonValue.Create(money) : null);
        Put(CostsRecognitionProfiles.VatTotal, InvoiceRequisites.VatTotalKey, "В том числе НДС",
            text => RecognizedValues.Money(text) is { } money ? JsonValue.Create(money) : null);

        // Стороны — по ИНН. В поле ложится только НАЙДЕННАЯ организация (одна действующая либо
        // организация среди своих ролей), и только в пустое: между разными записями выбирает
        // человек, а архивную в новый счёт не ставят. Прочие
        // исходы не хранятся — их считает вид при каждом чтении (см. InvoiceParties).
        void PutParty(InvoicePartyView? party, string requisiteKey)
        {
            if (party?.Match is not { } match) return;

            var found = InvoiceRequisites.ReferenceNode(match);
            if (!IsBlank(before.TryGetPropertyValue(requisiteKey, out var was) ? was : null))
            {
                // Занято: выбор человека побеждает. Но скан назвал ДРУГУЮ организацию — это остаётся
                // предложением, как у номера и суммы: иначе расхождение видно, только пока счёт
                // черновик, а после разбора о нём не узнать ниоткуда (ревью PR #1256).
                //
                // У ссылочного поля предложение — объект: текст скана И найденная запись. Одним текстом
                // его было бы нечем применить, а после разбора найденную пришлось бы искать по ИНН руками.
                if (!JsonNode.DeepEquals(was, found))
                    offers[requisiteKey] = new JsonObject
                    {
                        ["text"] = string.Join(", ",
                            new[] { party.Name, party.TaxId is { } taxId ? $"ИНН {taxId}" : null }.OfType<string>()),
                        ["entryId"] = match,
                    };
                return;
            }

            after[requisiteKey] = found;
            filled.Add(requisiteKey);
        }

        PutParty(matched.Supplier, InvoiceRequisites.SupplierKey);
        PutParty(matched.Payer, InvoiceRequisites.PayerKey);

        if (filled.Count > 0)
        {
            using var incoming = JsonDocument.Parse(after.ToJsonString());
            var (columns, rest) = InvoiceRequisites.Split(incoming.RootElement, before);
            // Охрана записи ядра — та же, что у формы: фоновой правке её правила не уступают.
            await InvoiceEndpoints.EnsureAllowedAsync(
                guard, invoice.DocumentTypeId, before.ToJsonString(), after.ToJsonString(), ct);
            // Срок оплаты распознавание не трогает, значит и «вписан рукой» ему ставить нечему.
            invoice.ApplyRequisites(columns, rest, dueDateByHand: false);
            invoice.AddUnconfirmed(filled);
        }

        var rows = lines.Rows;
        if (read.RowsProblem is { } problem)
            notes.Add($"Строки счёта не прочитаны: {problem}. Введите их вручную или вставьте из буфера.");

        JsonDocument? offeredLines = null;
        var added = 0;
        var recalled = 0;
        if (rows.Count > 0)
        {
            if (await db.InvoiceLines.AnyAsync(l => l.InvoiceId == invoice.Id, ct))
            {
                // Свои строки у счёта уже есть — распознанные сами не добавляются: какие из них дубль,
                // знает только человек.
                offeredLines = JsonSerializer.SerializeToDocument(read.Rows);
                notes.Add($"Распознано строк: {rows.Count}. У счёта уже есть свои строки — распознанные не добавлены.");
            }
            else
            {
                // Запомненное для этого поставщика подставляется сразу (задача C3, issue #1079): счёт
                // со скана приходит в очередь «Разобрать» уже без строк, которые система знает. Поставщик
                // — тот, что стоит в счёте ПОСЛЕ слияния шапки: вписанный человеком либо найденный по ИНН
                // этим же сканом. Строки ложатся с пометкой «запомнено» — той же, что ставит форма.
                // Сам вопрос задан до замка (LinesAsync); здесь — только выбор ответа по поставщику.
                var answer = invoice.SupplierId is { } supplier ? lines.Recalled.GetValueOrDefault(supplier) : null;

                foreach (var (values, index) in rows.Select((values, index) => (values, index)))
                {
                    var known = answer?.Lines[index];
                    var line = InvoiceLine.Create(invoice.Id);
                    line.Apply(index + 1, known is null
                        ? values
                        : values with { NomenclatureId = known.Position, MatchedBy = known.Match });
                    db.InvoiceLines.Add(line);
                    if (known is not null) recalled++;
                }

                // Молчать нельзя ни о том, ни о другом: строка без позиции выглядела бы незнакомой, и
                // человек запомнил бы её заново, не узнав, что прежний выбор в архиве, — или что
                // запомненное просто не спросили.
                if (lines.RecallFailed && invoice.SupplierId is not null)
                    notes.Add("Запомненные позиции строкам не подставлены: справочник не ответил. Нажмите " +
                              "«Подставить запомненное» над строками счёта.");
                else if (answer is { Unusable: > 0 })
                    notes.Add($"Строк, для которых запомнена позиция в архиве или удалённая: {answer.Unusable}. " +
                              "Она не подставлена — выберите действующую, и она заменит запомненное.");
                invoice.ContentChanged();
                added = rows.Count;
            }
        }

        stored.Finish(read.Engine,
            JsonSerializer.SerializeToDocument(read.Fields),
            offers.Count > 0 ? JsonDocument.Parse(offers.ToJsonString()) : null,
            offeredLines, notes, readImage);

        return (filled.Count, added, recalled);
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

        var quantity = Number(CostsRecognitionProfiles.LineQuantity, "количество", RecognizedValues.Quantity, 3);
        // Цена — деньгами, а не количеством: «1.250» в графе цены бывает и тысячей двести пятьюдесятью.
        var price = Number(CostsRecognitionProfiles.LinePrice, "цена", RecognizedValues.Money, 2);
        var amount = Number(CostsRecognitionProfiles.LineAmount, "сумма", RecognizedValues.Money, 2);
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
            Quantity: quantity, Price: price, VatRate: null, VatAmount: null, Amount: amount,
            Note: unread.Count > 0 ? "В файле не прочитано: " + string.Join(", ", unread) : null);

        // Сумму досчитываем, только когда в скане её НЕТ. Стояла, но не прочиталась — оставляем пустой:
        // посчитанная выглядела бы прочитанной, а на бумаге написано другое.
        return string.IsNullOrWhiteSpace(row.GetValueOrDefault(CostsRecognitionProfiles.LineAmount)) ? values.Completed() : values;
    }

    private static string Short(string text) => text.Length > 40 ? text[..40] + "…" : text;

    private static bool IsBlank(JsonNode? node) =>
        node is null || (node is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text));
}

/// <summary>
/// Фоновая операция «распознать скан счёта». Тонкая: вся работа — в <see cref="InvoiceScanReading" />,
/// чтобы её можно было проверить без очереди.
/// </summary>
public sealed class InvoiceRecognitionJob(InvoiceScanReading scan) : IModuleJobHandler
{
    public string Operation => InvoiceScanRecognition.Operation;

    public Task RunAsync(ModuleJobRun run, CancellationToken ct) => scan.RunAsync(run.TargetId, run.JobId, run.Payload, ct);
}
