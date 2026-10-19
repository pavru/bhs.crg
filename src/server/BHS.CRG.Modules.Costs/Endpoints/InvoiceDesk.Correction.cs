using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Исправление записано (или посчитано предпросмотром).</summary>
/// <param name="Version">Номер версии, ушедшей под исправление.</param>
/// <param name="Records">Записи книги, которые ложатся исправлением: открытие, выравнивание, само исправление.</param>
/// <param name="Stamp">Отметка этих записей — та, что сверяется с предпросмотром.</param>
/// <param name="Changed">Изменённые поля шапки — для журнала, теми же событиями, что у правки шапки.</param>
/// <param name="DueWas">Срок оплаты до исправления — для события «срок изменён вручную».</param>
/// <param name="VatMode">Смена режима НДС (issue #1498); <c>null</c> — режим не менялся.</param>
internal sealed record CorrectionDone(
    Invoice Invoice, int Version, IReadOnlyList<BookRecord> Records, string Stamp,
    IReadOnlyList<string> Changed, (DateOnly? Due, bool Manual) DueWas, VatModeShift? VatMode);

/// <summary>
/// Исправление счёта, требующего исправления (E2, issue #1100, spec v3.3 п. 1.4), — свой вход связки
/// записи. Обычная правка такому счёту отказывает; исправление пишет его целиком, с версией и книгой.
///
/// <para><b>Порядок проверок</b> (решение владельца 15.10.2026): 403 <c>costs.invoice.edit</c> (ворота
/// адреса) → 409 «исправление не нужно» → 403 право исправления → 409 версия формы → 403
/// <c>costs.allocation.edit</c>, если разноска отличается от записанной → 400 «не хуже, чем было» → 409
/// отметка предпросмотра. Человек без права исправления на обычном счёте слышит «правьте как обычно», а
/// не «нет права»: путь у него есть.</para>
///
/// <para><b>Замки — как у всех пишущих</b>: период, потом строка счёта, и всё — до чтения. Допуск
/// читается под ними впервые в запросе (п. 1.3): порт настроек держит значение до конца запроса, и
/// прочитанный раньше допуск молча остался бы прежним, если его сменили, пока запрос ждал замка.</para>
///
/// <para><b>Предпросмотр — та же функция</b>: всё посчитано и записано в транзакцию, а затем она
/// откатывается. Посчитай предпросмотр отдельно — однажды он показал бы не те строки, что ляжут в книгу,
/// а человек подтвердил бы показанное.</para>
/// </summary>
public sealed partial class InvoiceDesk
{
    internal async Task<CorrectionDone> CorrectAsync(
        Guid id, CorrectionBody body, IModuleWriteGuard guard, bool preview, CancellationToken ct)
    {
        // Правило срока и «сегодня» — до замков, как у правки шапки: настройки срока допуск не касаются, а
        // полночь между этим чтением и записью поймает отметка предпросмотра.
        var rule = await DueRuleAsync(ct);
        var today = await clock.TodayAsync(ct);
        try
        {
            return await db.InOpenPeriodAsync(periods, async boundaries =>
            {
                await InvoiceBook.LockAsync(db, id, ct);
                var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);
                var label = InvoiceEndpoints.Label(invoice);

                // Разноска для «нужно ли исправление» — отдельным узким чтением, а не снимком: снимок спрашивает
                // ядро, и отказ «не нужно», «нет права» или «форма устарела» платил бы за опрос впустую.
                var stored = await db.InvoiceAllocations.AsNoTracking().Where(a => a.InvoiceId == id).ToListAsync(ct);
                var need = ClosedPeriodGuard.NeedOf(invoice, stored, boundaries)
                    ?? throw new ConflictException(
                        $"{label} исправления не требует: он не оплачен либо его деньги не лежат в закрытом периоде " +
                        "и не исправлялись. Правьте его как обычно — без причины и версии.");
                if (!await user.HasAsync(CostsModule.InvoiceCorrect, ct))
                    throw new ForbiddenException(
                        $"{label} заперт: {PaymentViews.Text(need, await places.LoadAsync(ct))}. Изменить его можно " +
                        "только исправлением, а права «Исправлять счета закрытого периода» у вас нет. Его выдаёт " +
                        "администратор.");
                EnsureSeen(label, db.VersionOf(invoice));

                // ДО — одним снимком под замками. Допуск в нём — первое чтение допуска в запросе.
                var was = await snapshots.OfAsync(invoice, ct);
                var tolerance = was.Tolerance;
                var head = (invoice.SupplierId, invoice.Total, invoice.VatTotal, invoice.RemainderAccountingOn);
                IReadOnlyList<Grain> MoneyAt(decimal t) => InvoiceBookPosting.Grains(invoice.Id, head, was.Lines, was.Parts, t);
                var before = MoneyAt(tolerance);
                var posted = PaymentPosting.Before(invoice, was.Parts,
                    PaymentPosting.Balance(was.Lines.Select(InvoiceAllocations.Line), was.Parts, invoice.Total, tolerance));
                var content = InvoiceVersion.Snapshot(invoice, was.Lines, was.Parts, before);
                var wasGaps = InvoiceCorrectionCheck.Gaps(invoice, was);
                var dueWas = (invoice.DueDate, invoice.DueDateManual);

                // Строки исправления — бумага ПРИСЛАННОГО режима (issue #1498): по нему они и досчитываются.
                if (Moved(was, body, body.VatMode) && !await user.HasAsync(AllocationEndpoints.Permission, ct))
                    throw new ForbiddenException(
                        $"{label}: исправление меняет разноску — цель, количество, сумму или порядок частей, — а " +
                        "права на разноску («costs.allocation.edit») у вас нет. Исправьте содержимое, не трогая " +
                        "разноску, или попросите того, у кого это право есть.");

                // Режим — до строк и после снимка версии: версия хранит режим, каким он был, а строки
                // досчитываются по новому, в той же правке — как у замены строк (issue #1497, #1498).
                var modeWas = invoice.VatMode;
                VatModeShift? shift = invoice.ChangeVatMode(body.VatMode) ? new(modeWas, body.VatMode) : null;

                var changed = await PlaceAsync(invoice, body, guard, rule, was, ct);
                await db.SaveChangesAsync(ct);

                var seen = await snapshots.OfAsync(invoice, ct);
                InvoiceCorrectionCheck.EnsureNoWorse(invoice, was, wasGaps, seen);

                // Состояние счёта исправление НЕ трогает (ревью PR #1364). Вернуть разобранный в черновик было бы
                // дорогой в один конец: переход «разобран» обычной правкой такому счёту закрыт. И незачем —
                // «не хуже» не пропускает ничего, чего у разобранного не было: ни пустого обязательного поля, ни
                // строки без позиции, ни неразнесённой строки.

                // Учётные дни — правилом правки оплаченного счёта (п. 0.3); сведённость проверило «не хуже».
                var parts = await PostAsync(invoice, posted, boundaries, ct, corrected: true);
                var after = InvoiceBookPosting.Grains(invoice.Id,
                    (invoice.SupplierId, invoice.Total, invoice.VatTotal, invoice.RemainderAccountingOn),
                    seen.Lines, parts, tolerance);

                var number = (await db.InvoiceVersions.Where(v => v.InvoiceId == id)
                    .MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;
                db.InvoiceVersions.Add(InvoiceVersion.Create(id, number, content, body.Reason, user.Id));

                var appended = await InvoiceBook.AppendAsync(db, invoice,
                    last => new BookChange(InvoiceBookKind.Correction, before, tolerance, after, tolerance,
                        last is { } t && t != tolerance ? MoneyAt(t) : null, shift),
                    boundaries, today, BookAuthor.Of(user, body.Reason, number), ct);

                var done = new CorrectionDone(invoice, number, appended.Records,
                    InvoiceBookPosting.Stamp(appended.Records), changed, dueWas, shift);
                if (preview) throw new PreviewOnly(done);

                if (body.Stamp != done.Stamp)
                    throw new ConflictException(
                        $"{label}: с предпросмотра изменилось то, что ляжет в книгу, — день записи или её строки " +
                        "(прошла полночь, закрыли период или сменили допуск). Пересчитайте предпросмотр и " +
                        "подтвердите исправление заново: записать не то, что вы видели, нельзя.");

                await db.SaveChangesAsync(ct);
                return done;
            }, ct);
        }
        catch (PreviewOnly shown)
        {
            // Транзакция откатилась вместе с исключением; посчитанное — в нём. Контекст запроса больше не
            // читается: отслеживаемое в нём «записано» в откаченную транзакцию.
            return shown.Done;
        }
    }

    /// <summary>Положить шапку, строки и разноску — без сохранения: сохраняет вход исправления.</summary>
    private async Task<IReadOnlyList<string>> PlaceAsync(
        Invoice invoice, CorrectionBody body, IModuleWriteGuard guard, InvoiceDueRule rule, InvoiceSnapshot was,
        CancellationToken ct)
    {
        var changed = await InvoiceEndpoints.ApplyRequisitesAsync(
            invoice, body.Requisites, this, guard, targets, catalog, rule, ct);

        // «Запоминать» — нет: соответствие наименования позиции — память для следующих счетов, а не
        // исправление этого; его записывает обычная правка строк.
        var placed = await InvoiceLineEndpoints.PlaceLinesAsync(db, catalog, invoice,
            [.. body.Lines.Select(l => (l.Id, l.Values))], [.. body.Lines.Select(_ => false)], ct);

        // Цели, уже записанные у счёта, принимаются и потерянными (ТЗ CORE-34.4) — той же мерой, что у матрицы.
        var known = (await places.LoadAsync(ct)).Keeping(was.Parts);
        var existing = await db.InvoiceAllocations.Where(a => a.InvoiceId == invoice.Id).ToListAsync(ct);
        var now = new List<InvoiceAllocation>();
        for (var index = 0; index < placed.Lines.Count; index++)
            AllocationMatrixEndpoints.Place(db, invoice.Id, placed.Lines[index].Id,
                AllocationMatrixEndpoints.OfLine(placed.Lines[index], body.Lines[index].Parts, known), existing, now);
        AllocationMatrixEndpoints.Place(db, invoice.Id, null,
            AllocationMatrixEndpoints.ParseDocument(body.Document, invoice, placed.Lines.Count, known), existing, now);
        db.InvoiceAllocations.RemoveRange(existing.Except(now));

        invoice.ContentChanged();
        return changed;
    }

    /// <summary>
    /// Отличается ли присланная разноска от записанной — целью, количеством, суммой или порядком частей.
    /// Производные деньги (округление, расхождение в последней части) не в счёт: их не присылают.
    /// </summary>
    /// <param name="vat">Режим НДС ПРИСЛАННЫЙ, а не лежащий у счёта (issue #1498): присланные строки — бумага
    /// этого режима, и досчитать их по прежнему значило бы прочитать «без НДС» как «с НДС» (или отказать
    /// 400 на чужое поле суммы). Вид разноски строки зависит от её суммы С НДС (issue #1497). У строки
    /// сверху без ставки суммы с НДС нет, как и у записанной.</param>
    private static bool Moved(InvoiceSnapshot was, CorrectionBody body, VatMode vat)
    {
        var stored = was.Parts.OrderBy(a => a.Ordinal).ToLookup(a => a.LineId, a => a.Snapshot());
        var sent = body.Lines.Where(l => l.Id is not null).Select(l => l.Id!.Value).ToHashSet();

        // Строка, убранная исправлением, уносит свои части; новая — приносит.
        if (stored.Any(g => g.Key is { } line && !sent.Contains(line) && g.Any())) return true;

        // Часть, которую не разобрать, записанной не равна — значит, разноска меняется. Отказ о её виде
        // услышит тот, кому разноску менять можно: без права ответ — 403, а не 400 о чужой ему разноске.
        try
        {
            foreach (var (line, number) in body.Lines.Select((line, index) => (line, index + 1)))
            {
                var done = InvoiceLineRequests.Complete(line.Values, number, vat).Values;
                var mode = AllocationMath.ModeOf(done.Quantity, done.Amount);
                var parts = line.Parts.Select((p, index) => InvoiceAllocations.Values(p, index + 1, mode)).ToList();
                if (!(line.Id is { } known ? stored[known] : Enumerable.Empty<AllocationValues>()).SequenceEqual(parts)) return true;
            }

            var document = body.Document
                .Select((p, index) => InvoiceAllocations.Values(p, index + 1, AllocationMode.Amount)).ToList();
            return !stored[null].SequenceEqual(document);
        }
        catch (InvalidRequestException)
        {
            return true;
        }
    }

    /// <summary>Предпросмотр: посчитанное уносится исключением, а транзакция откатывается.</summary>
    private sealed class PreviewOnly(CorrectionDone done) : Exception("Предпросмотр исправления: записи нет.")
    {
        public CorrectionDone Done { get; } = done;
    }
}
