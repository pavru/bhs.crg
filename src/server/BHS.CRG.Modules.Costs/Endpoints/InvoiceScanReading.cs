using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

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
    IModuleWriteGuard guard, IModuleActivityLog log)
{
    /// <summary>
    /// Работа фоновой задачи: прочитать скан и разложить прочитанное по черновику. Любой НАШ отказ
    /// записывается причиной и пробрасывается — задача завершается отказом с тем же текстом.
    /// </summary>
    public async Task RunAsync(Guid invoiceId, string? scanBlobPath, CancellationToken ct)
    {
        var stored = await db.InvoiceRecognitions.FirstOrDefaultAsync(r => r.InvoiceId == invoiceId, ct)
            ?? throw new ConflictException("Распознавать нечего: счёт удалён или распознавание по нему не ставили.");
        if (stored.Outcome != InvoiceRecognitionOutcome.Pending)
            throw new ConflictException("Распознавание по этому счёту уже завершено другой попыткой.");

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

            var read = await recognition.RecognizeAsync(
                CostsRecognitionProfiles.InvoiceCode, content, invoice.ScanMimeType!, ct);

            var (label, filled, lines) = await desk.MergeAsync(invoiceId, async write =>
            {
                // Счёт пришёл СВЕЖИМ, под замком: пока скан читался, его могли править, разобрать,
                // заменить ему скан. Условие проверяется заново — и против того же файла.
                EnsureSameScan(write.Invoice, scanBlobPath);
                var applied = await ApplyAsync(write.Invoice, stored, read, ct);
                await db.SaveChangesAsync(ct);
                return (InvoiceEndpoints.Label(write.Invoice), applied.Filled, applied.Lines);
            }, ct);

            await log.RecordAsync(InvoiceActions.Recognized, invoiceId.ToString(), label,
                after: $"полей: {filled}, строк: {lines}" + (read.Engine is { } engine ? $"; {engine}" : string.Empty), ct: ct);
        }
        catch (DomainException refusal)
        {
            // Отказ мог прийти из середины записи — несохранённое в счёт уйти не должно.
            db.ChangeTracker.Clear();
            var failed = await db.InvoiceRecognitions.FirstAsync(r => r.InvoiceId == invoiceId, ct);
            failed.Fail(refusal is RecognitionRefusedException refused ? refused.Reason.ToString() : "Refused", refusal.Message);
            await db.SaveChangesAsync(ct);
            throw;
        }
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
        Invoice invoice, InvoiceRecognition stored, ModuleRecognitionResult read, CancellationToken ct)
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

        var rows = read.Rows.Select(Line).ToList();
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
    /// «Разобрать». Число, которое не прочиталось однозначно, не угадывается — уходит в примечание.
    /// </summary>
    private static InvoiceLineValues Line(IReadOnlyDictionary<string, string?> row)
    {
        var unread = new List<string>();

        decimal? Number(string key, string title, Func<string?, decimal?> parse)
        {
            var text = row.GetValueOrDefault(key);
            var value = parse(text);
            if (text is not null && value is null) unread.Add($"{title} «{text}»");
            return value;
        }

        var quantity = Number(CostsRecognitionProfiles.LineQuantity, "количество", RecognizedValues.Quantity);
        var price = Number(CostsRecognitionProfiles.LinePrice, "цена", RecognizedValues.Quantity);
        var amount = Number(CostsRecognitionProfiles.LineAmount, "сумма", RecognizedValues.Money);

        return new InvoiceLineValues(
            NomenclatureId: null,
            SupplierText: row.GetValueOrDefault(CostsRecognitionProfiles.LineName),
            SupplierCode: null,
            Unit: row.GetValueOrDefault(CostsRecognitionProfiles.LineUnit),
            Quantity: quantity, Price: price, VatRate: null, VatAmount: null, Amount: amount,
            Note: unread.Count > 0 ? "В скане не прочитано: " + string.Join(", ", unread) : null);
    }

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

    public Task RunAsync(ModuleJobRun run, CancellationToken ct) => scan.RunAsync(run.TargetId, run.Payload, ct);
}
