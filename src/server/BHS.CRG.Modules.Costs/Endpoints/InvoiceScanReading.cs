using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Costs.Data;
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
    CostsDbContext db, InvoiceDesk desk, IModuleRecognition recognition, IModuleBlobs blobs,
    IModuleWriteGuard guard, IModuleActivityLog log, ILoggerFactory logs, InvoiceParties parties)
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
        (string Label, int Filled, int Lines) merged;
        try
        {
            var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
                ?? throw new ConflictException("Счёт удалён, пока его скан ждал распознавания.");
            EnsureSameScan(invoice, scanBlobPath);

            byte[] content;
            await using (var scan = await blobs.OpenAsync(invoice.ScanBlobPath!, ct))
            using (var buffer = new MemoryStream())
            {
                await scan.CopyToAsync(buffer, ct);
                content = buffer.ToArray();
            }

            read = await recognition.RecognizeAsync(
                CostsRecognitionProfiles.InvoiceCode, content, invoice.ScanMimeType!, ct);
            // Стороны сопоставляются ДО слияния и один раз: обход справочника под замком счёта держал бы
            // замок зря и повторялся бы с каждой попыткой слияния. Отказом он не отвечает — справочник,
            // который не ответил, даёт состояние «сопоставить не удалось», и шапка со строками ложатся
            // в счёт без сторон.
            var matched = await parties.MatchAsync(read.Fields, ct);
            merged = await MergeAsync(invoiceId, scanBlobPath, read, matched, ct);
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
                after: $"полей: {merged.Filled}, строк: {merged.Lines}" + (read.Engine is { } engine ? $"; {engine}" : string.Empty),
                ct: ct);
        }
        catch (Exception lost) when (lost is not OperationCanceledException)
        {
            logs.CreateLogger<InvoiceScanReading>().LogError(lost,
                "Счёт {InvoiceId} заполнен из скана, но запись об этом в журнал действий не легла.", invoiceId);
        }
    }

    /// <summary>
    /// Разложить прочитанное по счёту. Проиграв одновременной правке формы, слияние ПОВТОРЯЕТСЯ: счёт
    /// перечитывается, и то же прочитанное раскладывается заново — уже мимо поля, которое человек
    /// только что заполнил. Выбросить прочитанное значило бы платить движку второй раз за тот же файл.
    /// </summary>
    private async Task<(string Label, int Filled, int Lines)> MergeAsync(
        Guid invoiceId, string? scanBlobPath, ModuleRecognitionResult read, InvoicePartiesView matched,
        CancellationToken ct)
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
                    var applied = await ApplyAsync(write.Invoice, stored, read, matched, ct);
                    await db.SaveChangesAsync(ct);
                    return (InvoiceEndpoints.Label(write.Invoice), applied.Filled, applied.Lines);
                }, ct);
            }
            catch (ConflictException lost) when (lost.InnerException is DbUpdateConcurrencyException && attempt < MergeAttempts)
            {
                // Несохранённое уходит целиком: следующая попытка читает счёт и запись заново.
                db.ChangeTracker.Clear();
            }
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
                $"{InvoiceEndpoints.Label(invoice)}: скан заменили, пока прежний распознавался. Прочитанное " +
                "относится к прежнему файлу и в счёт не записано — запустите распознавание ещё раз.");
        if (InvoiceScanRecognition.WhyNot(invoice) is { } whyNot)
            throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)}: распознанное не записано — {whyNot}.");
    }

    /// <summary>Шапка — в пустые поля, строки — в счёт без строк; остальное — предложением.</summary>
    private async Task<(int Filled, int Lines)> ApplyAsync(
        Invoice invoice, InvoiceRecognition stored, ModuleRecognitionResult read, InvoicePartiesView matched,
        CancellationToken ct)
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
            if (!IsBlank(before.TryGetPropertyValue(requisiteKey, out var was) ? was : null)) return;

            after[requisiteKey] = InvoiceRequisites.ReferenceNode(match);
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

        var rows = read.Rows.Select(Line).OfType<InvoiceLineValues>().ToList();
        if (read.RowsProblem is { } problem)
            notes.Add($"Строки счёта не прочитаны: {problem}. Введите их вручную или вставьте из буфера.");

        JsonDocument? offeredLines = null;
        var added = 0;
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
                foreach (var (values, index) in rows.Select((values, index) => (values, index)))
                {
                    var line = InvoiceLine.Create(invoice.Id);
                    line.Apply(index + 1, values);
                    db.InvoiceLines.Add(line);
                }
                invoice.ContentChanged();
                added = rows.Count;
            }
        }

        stored.Finish(read.Engine,
            JsonSerializer.SerializeToDocument(read.Fields),
            offers.Count > 0 ? JsonDocument.Parse(offers.ToJsonString()) : null,
            offeredLines, notes);

        return (filled.Count, added);
    }

    /// <summary>
    /// Строка скана → строка счёта. Номенклатуры нет: её выбирает человек, строка ждёт в очереди
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
            Note: unread.Count > 0 ? "В скане не прочитано: " + string.Join(", ", unread) : null);

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
