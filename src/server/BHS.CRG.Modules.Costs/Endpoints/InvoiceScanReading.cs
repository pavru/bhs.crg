using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Files;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Recognition;
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
    IModuleCatalog catalog, IModuleBlobs blobs, IModuleRecognitionTargets targets)
{
    /// <summary>Сколько раз слияние повторяется, проиграв одновременной правке формы.</summary>
    private const int MergeAttempts = 3;

    /// <summary>
    /// Работа фоновой задачи: прочитать скан и разложить прочитанное по черновику. Любой отказ
    /// записывается причиной и пробрасывается — задача завершается отказом с тем же текстом.
    ///
    /// <para>⚠️ <b>Удалённый счёт — не отказ</b> (issue #1282): черновик удаляют и пока его файл читается,
    /// и пока задача ждёт в очереди. Прочитанное тогда выбрасывается, а задача завершается БЕЗ ошибки —
    /// красная задача «распознать нечего» в общем индикаторе говорила бы о сбое там, где человек сам
    /// убрал счёт. Запись об исходе ушла каскадом вместе со счётом: писать причину некуда и незачем.</para>
    /// </summary>
    public async Task RunAsync(Guid invoiceId, Guid jobId, string? scanBlobPath, CancellationToken ct)
    {
        var stored = await db.InvoiceRecognitions.FirstOrDefaultAsync(r => r.InvoiceId == invoiceId, ct);
        if (stored is null)
        {
            if (!await db.Invoices.AsNoTracking().AnyAsync(i => i.Id == invoiceId, ct))
            {
                await DroppedAsync(invoiceId, scanBlobPath);
                return;
            }
            throw new ConflictException("Распознавать нечего: распознавание по этому счёту не ставили.");
        }
        if (stored.Outcome != InvoiceRecognitionOutcome.Pending)
            throw new ConflictException("Распознавание по этому счёту уже завершено другой попыткой.");

        // Свой номер задача знает сама и вписывает его, если запись его не несёт: постановка сохраняет
        // номер ПОСЛЕ очереди, и между ними запись могли перезапустить вторым нажатием. Без номера
        // форма читала бы работающую задачу как «прервано».
        if (stored.JobId != jobId)
        {
            stored.Queued(jobId);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (ConflictException gone) when (gone.InnerException is DbUpdateConcurrencyException)
            {
                if (await db.Invoices.AsNoTracking().AnyAsync(i => i.Id == invoiceId, CancellationToken.None)) throw;
                await DroppedAsync(invoiceId, scanBlobPath);
                return;
            }
        }

        ModuleRecognitionResult read;
        (string Label, int Filled, int Lines, int Recalled, VatMode? ModeSet) merged;
        try
        {
            var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, ct);
            if (invoice is null)
            {
                await DroppedAsync(invoiceId, scanBlobPath);
                return;
            }
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
            // Куда ложится шапка (issue #1483) — тоже до замка: схема типа между попытками слияния не меняется.
            var header = InvoiceScanHeader.Of(read.Fields,
                await targets.ResolveAsync(CostsRecognitionProfiles.InvoiceCode, [.. read.Fields.Keys], ct));
            merged = await MergeAsync(invoiceId, scanBlobPath, read, matched, lines, header, readImage, ct);
        }
        catch (DomainException refusal)
        {
            if (!await FailAsync(invoiceId,
                refusal switch
                {
                    RecognitionRefusedException refused => refused.Reason.ToString(),
                    // Образа нет — читать не начинали. «Прочитанное не записалось» здесь было бы неправдой.
                    InvoiceImageRefusedException => InvoiceScanImage.NoImage,
                    _ => "Refused",
                }, refusal.Message))
            {
                await DroppedAsync(invoiceId, scanBlobPath, refusal);
                return;
            }
            throw;
        }
        catch (Exception crash) when (crash is not OperationCanceledException)
        {
            // Не наш отказ — сбой базы или хранилища. Исход всё равно обязан быть записан: иначе запись
            // ждала бы вечно, а форма показывала «прервано» без единого слова о том, что случилось.
            // Чужой текст человеку не отдаём — он в журнале сервера, вместе с самим исключением.
            if (!await FailAsync(invoiceId, "Refused",
                "Прочитанное не удалось записать в счёт: внутренняя ошибка. Запустите распознавание ещё раз; " +
                "если повторится — подробности в журнале сервера."))
            {
                await DroppedAsync(invoiceId, scanBlobPath, crash);
                return;
            }
            throw;
        }

        // Журнал — ПОСЛЕ исхода и вне его: счёт уже заполнен, и отказ журнала не вправе ни объявить
        // задачу упавшей, ни переписать «прочитано» на «не удалось».
        try
        {
            await log.RecordAsync(InvoiceActions.Recognized, invoiceId.ToString(), merged.Label,
                after: $"полей: {merged.Filled}, строк: {merged.Lines}"
                    + (merged.Recalled > 0 ? $", из них с позицией из запомненного: {merged.Recalled}" : string.Empty)
                    // Режим, поставленный распознаванием, — тем же словом, что у правки строк (ревью PR #1509):
                    // иначе журнал не знал бы, откуда у счёта «сверху», которого никто не отмечал.
                    + (merged.ModeSet is { } set ? $"; НДС: {VatModes.Shown(set)}" : string.Empty)
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
    /// Разложить прочитанное по счёту. Проиграв запись по версии строки, слияние ПОВТОРЯЕТСЯ: счёт
    /// перечитывается, и то же прочитанное раскладывается заново — уже мимо поля, которое человек
    /// только что заполнил. Выбросить прочитанное значило бы платить движку второй раз за тот же файл.
    ///
    /// <para>С E2 (issue #1100) связка держит строку счёта под блокировкой, и правка формы, пришедшая
    /// посреди слияния, ждёт его, а не перебивает: форма, открытая до исхода, получает «тем временем
    /// изменили». Повтор остаётся последней дверью — для записи, которая прошла бы мимо блокировки.</para>
    /// </summary>
    private async Task<(string Label, int Filled, int Lines, int Recalled, VatMode? ModeSet)> MergeAsync(
        Guid invoiceId, string? scanBlobPath, ModuleRecognitionResult read, InvoicePartiesView matched,
        ScanLines lines, InvoiceScanHeader.Plan header, string? readImage, CancellationToken ct)
    {
        // Правило срока — ДО замка (ревью PR #1328): чтение настройки под замком периода держало бы его
        // зря, а между попытками слияния правило не меняется.
        var rule = await desk.DueRuleAsync(ct);
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
                    var applied = await ApplyAsync(write.Invoice, stored, read, matched, lines, header, readImage, rule, ct);
                    await db.SaveChangesAsync(ct);
                    return (InvoiceEndpoints.Label(write.Invoice), applied.Filled, applied.Lines, applied.Recalled,
                        applied.ModeSet);
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
    /// <param name="VatNote">НДС строк снят, потому что прочитан не у всех (issue #1410), — почему.</param>
    /// <param name="StrayNote">Прочитанное, что в поля строк не легло, — по колонкам (issue #1480).</param>
    private sealed record ScanLines(
        IReadOnlyList<InvoiceScanLines.ScanLine> Rows, IReadOnlyDictionary<Guid, RecallAnswer> Recalled, bool RecallFailed,
        string? VatNote, string? StrayNote);

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
        var (rows, vatNote, strayNote) = InvoiceScanLines.Read(read.Rows);
        var recalled = new Dictionary<Guid, RecallAnswer>();
        if (rows.Count == 0) return new(rows, recalled, false, vatNote, strayNote);

        try
        {
            // Запомненное узнаёт строку по наименованию и артикулу — режим НДС ему не нужен.
            var keys = rows.Select(r => r.Values).ToList();
            foreach (var supplier in new[] { invoice.SupplierId, matched.Supplier?.Match }.OfType<Guid>().Distinct())
                recalled[supplier] = await SupplierMatching.RecallAsync(db, catalog, supplier, keys, ct);
            return new(rows, recalled, false, vatNote, strayNote);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            logs.CreateLogger<InvoiceScanReading>().LogWarning(failure,
                "Счёт {InvoiceId}: запомненные позиции для строк файла спросить не удалось — строки лягут без них.",
                invoice.Id);
            return new(rows, new Dictionary<Guid, RecallAnswer>(), true, vatNote, strayNote);
        }
    }

    /// <summary>
    /// Записать исход «не удалось». <c>false</c> — записывать некуда: запись ушла каскадом вместе со
    /// счётом, то есть счёт удалили, пока его файл читался (issue #1282). Раньше здесь стояло
    /// <c>FirstAsync</c>, и удаление посреди чтения роняло саму запись об отказе — причина терялась под
    /// «последовательность не содержит элементов».
    /// </summary>
    private async Task<bool> FailAsync(Guid invoiceId, string reason, string error)
    {
        // Отказ мог прийти из середины записи — несохранённое в счёт уйти не должно. И без токена
        // отмены: исход пишется и тогда, когда задачу остановили.
        db.ChangeTracker.Clear();
        var failed = await db.InvoiceRecognitions.FirstOrDefaultAsync(r => r.InvoiceId == invoiceId, CancellationToken.None);
        if (failed is null) return false;

        failed.Fail(reason, error);
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (ConflictException gone) when (gone.InnerException is DbUpdateConcurrencyException)
        {
            // Строки не стало между чтением и записью: счёт удалили в эту самую минуту.
            db.ChangeTracker.Clear();
            if (await db.InvoiceRecognitions.AsNoTracking().AnyAsync(r => r.InvoiceId == invoiceId, CancellationToken.None))
                throw;
            return false;
        }
        return true;
    }

    /// <param name="because">На чём задача остановилась. Пишется в журнал сервера всегда (ревью PR
    /// #1298): «счёт удалён» объясняет, почему исход некуда записать, но не то, что случилось до того, —
    /// сбой хранилища, совпавший с удалением, иначе пропал бы без следа.</param>
    ///
    /// <para>⚠️ Файл убирается ЕЩЁ РАЗ (ревью PR #1298). Удаление счёта задачу не останавливает — порта
    /// отмены у очереди нет, — и она могла построить читаемый образ офисного файла уже после того, как
    /// удаление сняло оригинал вместе с записью об образе. Такой образ держался бы за оригинал, которого
    /// нет, и не убрался бы никем. Повторное удаление безобидно, а запись и образ снимает само.</para>
    private async Task DroppedAsync(Guid invoiceId, string? scanBlobPath, Exception? because = null)
    {
        var logger = logs.CreateLogger<InvoiceScanReading>();
        logger.LogInformation(because,
            "Счёт {InvoiceId} удалён, пока его файл распознавался или ждал очереди: прочитанное выброшено.", invoiceId);
        if (scanBlobPath is null) return;

        try
        {
            await blobs.DeleteAsync(scanBlobPath, CancellationToken.None);
        }
        catch (Exception lost)
        {
            logger.LogWarning(lost, "Счёт {InvoiceId} удалён, но его файл {Path} убрать не удалось.", invoiceId, scanBlobPath);
        }
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
    /// <returns><c>ModeSet</c> — режим НДС, который поставило распознавание; <c>null</c> — не меняло.</returns>
    private async Task<(int Filled, int Lines, int Recalled, VatMode? ModeSet)> ApplyAsync(
        Invoice invoice, InvoiceRecognition stored, ModuleRecognitionResult read, InvoicePartiesView matched,
        ScanLines lines, InvoiceScanHeader.Plan header, string? readImage, InvoiceDueRule rule, CancellationToken ct)
    {
        var modeBefore = invoice.VatMode;
        var before = InvoiceRequisites.Merge(invoice);
        var after = before.DeepClone().AsObject();
        var filled = new List<string>();
        var offers = new JsonObject();
        var notes = new List<string>();

        // Шапка по правилу «ключ поля профиля = ключ поля счёта» (issue #1483): разобрано — в пустое поле,
        // занятое — в предложение, не разобранное — в предложение с замечанием. Предложение несёт и
        // разобранное значение: «Взять» кладёт его, а не разбирает текст второй раз на клиенте.
        notes.AddRange(header.Notes);
        var landed = new Dictionary<string, InvoiceScanHeader.Candidate>(StringComparer.Ordinal);
        foreach (var candidate in header.Candidates)
        {
            if (candidate.Value is null)
            {
                offers[candidate.Key] = InvoiceScanHeader.Offer(candidate.Text, null);
                notes.Add($"{candidate.Title}: «{candidate.Text}» не прочитано как значение поля — впишите вручную.");
                continue;
            }

            var was = before.TryGetPropertyValue(candidate.Key, out var node) ? node : null;
            if (!IsBlank(was))
            {
                // Занято: значение человека побеждает. Совпало с распознанным — и предлагать нечего.
                if (!JsonNode.DeepEquals(was, candidate.Value))
                    offers[candidate.Key] = InvoiceScanHeader.Offer(candidate.Text, candidate.Value);
                continue;
            }

            after[candidate.Key] = candidate.Value.DeepClone();
            filled.Add(candidate.Key);
            landed[candidate.Key] = candidate;
        }

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

        if (filled.Count > 0 && await GuardedAsync(invoice, before, after, filled, landed, offers, notes, ct)
                is (var columns, var rest, var termed))
        {
            // Срок оплаты распознавание не трогает, значит и «вписан рукой» ему ставить нечему.
            var dueBefore = invoice.DueDate;
            invoice.ApplyRequisites(columns, rest, dueDateByHand: false, rule);

            // Отсрочка из условий и срок, посчитанный от неё, — следствие узнанного поставщика, и метку
            // «распознано, не подтверждено» несут вместе с ним (ревью PR #1329). Без неё форма, открытая
            // во время распознавания, приняла бы итог распознавания за чужую правку, а запись в журнале
            // («полей: N») о них молчала бы.
            if (termed) filled.Add(InvoiceRequisites.DeferralKey);
            if (invoice.DueDate != dueBefore) filled.Add(InvoiceRequisites.DueDateKey);
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
            else if (await ModeAsync(invoice, read, rows, notes, ct) is not { } mode)
            {
                // Человек отметил «сверху», а бумага сходится «в том числе»: положить её строки сверху значило
                // бы прибавить НДС к суммам, где он уже есть. Режим человека распознавание не перезаписывает
                // (issue #1501) — строки предлагаются, как строки счёта, у которого свои уже есть.
                offeredLines = JsonSerializer.SerializeToDocument(read.Rows);
                notes.Add($"Распознано строк: {rows.Count}. В файле суммы строк уже с НДС и сходятся с суммой к " +
                          "оплате, а у счёта отмечено «Цены без НДС, НДС сверху» — распознанные не добавлены. " +
                          "Снимите отметку над строками и распознайте файл заново или введите строки вручную.");
            }
            else
            {
                // Запомненное для этого поставщика подставляется сразу (задача C3, issue #1079): счёт
                // со скана приходит в очередь «Разобрать» уже без строк, которые система знает. Поставщик
                // — тот, что стоит в счёте ПОСЛЕ слияния шапки: вписанный человеком либо найденный по ИНН
                // этим же сканом. Строки ложатся с пометкой «запомнено» — той же, что ставит форма.
                // Сам вопрос задан до замка (LinesAsync); здесь — только выбор ответа по поставщику.
                var answer = invoice.SupplierId is { } supplier ? lines.Recalled.GetValueOrDefault(supplier) : null;
                // Досчёт — после режима (issue #1501): «в том числе» и сверху считают НДС от суммы по-разному.
                var completed = InvoiceScanLines.Complete(rows, mode,
                    RecognizedValues.VatRate(read.Fields.GetValueOrDefault(CostsRecognitionProfiles.DocumentVatRate)));
                if (completed.RateNote is { } rateNote) notes.Add(rateNote);

                foreach (var (values, index) in completed.Lines.Select((values, index) => (values, index)))
                {
                    var known = answer?.Lines[index];
                    var line = InvoiceLine.Create(invoice.Id);
                    line.Apply(index + 1, CompletedLine.Scanned(known is null
                        ? values
                        : values with { NomenclatureId = known.Position, MatchedBy = known.Match }, mode));
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
                if (lines.VatNote is { } vatNote) notes.Add(vatNote);
                if (lines.StrayNote is { } strayNote) notes.Add(strayNote);
                invoice.ContentChanged();
                added = rows.Count;
            }
        }

        stored.Finish(read.Engine,
            JsonSerializer.SerializeToDocument(read.Fields),
            offers.Count > 0 ? JsonDocument.Parse(offers.ToJsonString()) : null,
            offeredLines, notes, readImage);

        return (filled.Count, added, recalled, invoice.VatMode != modeBefore ? invoice.VatMode : null);
    }

    /// <summary>
    /// Охрана записи — ПО ПОЛЯМ (issue #1483). Поле, которому охрана отказала (поле заказчика с шаблоном
    /// или длиной), уходит в предложение с её словами, остальное ложится. Отказ целиком, как у формы, здесь
    /// терял бы номер, суммы и строки из-за одного поля, о котором человек и не просил.
    ///
    /// <para>Охрана — та же, что у формы: фоновой правке её правила не уступают. Отказ не по заполняемому
    /// полю (отсрочка из условий поставщика, чужое поле) — ошибка кода, и распознавание падает, как
    /// раньше.</para>
    /// </summary>
    /// <returns>Что ложится в счёт; <c>null</c> — не легло ни одно поле.</returns>
    private async Task<(InvoiceColumns Columns, JsonDocument Extra, bool Termed)?> GuardedAsync(
        Invoice invoice, JsonObject before, JsonObject after, List<string> filled,
        IReadOnlyDictionary<string, InvoiceScanHeader.Candidate> read, JsonObject offers, List<string> notes,
        CancellationToken ct)
    {
        // Отсрочка из условий — один раз (ревью PR #1523): поставщика охрана по полям не снимает, и условия
        // между попытками те же — перечитывать их под замком счёта незачем.
        (int? Days, bool Termed)? terms = null;
        while (true)
        {
            using var incoming = JsonDocument.Parse(after.ToJsonString());
            // Охране — то, что ляжет в счёт, а не присланное: см. Split.
            var (columns, rest, landing) = InvoiceRequisites.Split(incoming.RootElement, before);
            // Узнанный поставщик приносит отсрочку из своих условий (ТЗ COST-9.2): скан её не читает.
            // До охраны — охрана судит то, что ляжет в счёт (ревью PR #1329).
            if (terms is null)
            {
                (columns, var substituted) = await desk.WithTermsAsync(columns, invoice, ct);
                terms = (columns.DeferralDays, substituted);
            }
            else if (terms.Value.Termed)
                columns = columns with { DeferralDays = terms.Value.Days, DeferralFromTerms = true };
            var termed = terms.Value.Termed;
            var refusals = await guard.RefusalsAsync(invoice.DocumentTypeId, before.ToJsonString(),
                InvoiceEndpoints.WithTerms(landing, termed, columns), ct);
            if (refusals.Count == 0) return (columns, rest, termed);

            var refused = refusals
                .Select(r => (Key: Root(r.Path), Refusal: r))
                .Where(p => p.Key is not null && read.ContainsKey(p.Key) && filled.Contains(p.Key))
                .ToList();
            if (refused.Count == 0) throw InvoiceEndpoints.Refused(refusals);

            foreach (var (key, refusal) in refused)
            {
                if (!filled.Remove(key!)) continue;
                // Как лежало: пустая строка остаётся пустой строкой, а не пропавшим ключом.
                if (before.TryGetPropertyValue(key!, out var was)) after[key!] = was?.DeepClone();
                else after.Remove(key!);
                var candidate = read[key!];
                offers[key!] = InvoiceScanHeader.Offer(candidate.Text, candidate.Value);
                notes.Add($"{candidate.Title}: «{candidate.Text}» не записано — охрана записи: {refusal.Message}");
            }
            if (filled.Count == 0) return null;
        }

        // «Работы[3].Количество» → «Работы»: отказ называет путь, а поле шапки — его начало.
        static string? Root(string? path) =>
            path is { Length: > 0 } ? path.Split('.', '[')[0] : null;
    }

    /// <summary>
    /// В каком режиме лягут строки скана (issue #1501) — после слияния шапки: сумма к оплате и НДС уже те,
    /// что стоят в счёте (прочитанные в пустые поля или вписанные человеком с той же бумаги).
    ///
    /// <para><b>Режим человека не перезаписывается.</b> «Задавал» — значит, у счёта стоит «сверху»:
    /// «в том числе» — умолчание, и у счёта без строк (а строки скана ложатся только в такой) оно ничего
    /// человеческого не значит (разбор Архитектора, §5.1). Стоит «сверху», а бумага сходится «в том числе»
    /// — <c>null</c>: строки не кладутся.</para>
    ///
    /// <para>Сверху, найденное распознаванием, помечается «распознано, не подтверждено»
    /// (<see cref="VatModes.Mark" />) и объясняется в примечаниях — прочитанными числами, чтобы «Верно»
    /// нажимали, сверив с файлом.</para>
    ///
    /// <para>Не определилось — ложится «в том числе», как до #1501, и МОЛЧА (ревью PR #1509). Строки не
    /// сходятся с суммой к оплате и у счёта со скидкой, доставкой или пропущенной строкой — замечание «если
    /// цены без НДС, отметьте» стояло бы почти на каждом таком скане, и его перестали бы читать. Расхождение
    /// и так видно в сверке под строками, а «похоже, цены без НДС» она говорит сама — по арифметике
    /// (issue #1500), только там, где это правда похоже.</para>
    /// </summary>
    private async Task<VatMode?> ModeAsync(
        Invoice invoice, ModuleRecognitionResult read, IReadOnlyList<InvoiceScanLines.ScanLine> rows,
        List<string> notes, CancellationToken ct)
    {
        var tolerance = await desk.ToleranceAsync(ct);
        var subtotal = read.Fields.GetValueOrDefault(CostsRecognitionProfiles.Subtotal) is { } text
            ? RecognizedValues.Money(text)
            : null;
        var found = InvoiceScanLines.ModeOf(rows, invoice.VatTotal, invoice.Total, subtotal, tolerance);

        if (invoice.VatMode == VatMode.OnTop) return found == VatMode.Included ? null : VatMode.OnTop;

        if (found == VatMode.OnTop)
        {
            invoice.ChangeVatMode(VatMode.OnTop);
            invoice.AddUnconfirmed([VatModes.Mark]);
            // Только прочитанное: «+ НДС = к оплате» с НДС, выведенным вычитанием, сходилось бы всегда и не
            // доказывало бы ничего. ModeOf «сверху» без суммы к оплате и сумм строк не отвечает.
            var paper = rows.Sum(r => r.Values.Amount!.Value);
            notes.Add($"Цены в строках файла без НДС, НДС начислен сверху: сумма строк {CostsValues.Shown(paper)}" +
                      (invoice.VatTotal is { } vat ? $", НДС {CostsValues.Shown(vat)}" : "") +
                      (subtotal is { } net ? $", итого без НДС {CostsValues.Shown(net)}" : "") +
                      $", к оплате {CostsValues.Shown(invoice.Total!.Value)} — отмечено «Цены без НДС, НДС сверху». " +
                      "Сверьте с файлом и нажмите «Верно» у отметки над строками.");
        }
        return invoice.VatMode;
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

    public Task RunAsync(ModuleJobRun run, CancellationToken ct) => scan.RunAsync(run.TargetId, run.JobId, run.Payload, ct);
}
