using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Settings;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Счёт, которому смена допуска записала книгу, — для журнала.</summary>
/// <param name="From">Допуск, от которого выровнена книга.</param>
internal sealed record ToleranceMoved(Guid Id, string Label, decimal From);

/// <summary>Что сделает смена допуска: у скольких оплаченных счетов сдвинутся деньги, сколько из них получат книгу.</summary>
internal sealed record ToleranceImpact(int Changed, int Booked);

/// <summary>
/// Смена допуска расхождения у записанных счетов (E2, issue #1100, spec v3.3 п. 1.9) — свой вход связки
/// записи. Допуск действует на все счета сразу: у оплаченного счёта с расхождением он перекладывает деньги
/// между последней частью разноски и остатком, в том числе в закрытом месяце. Счёт, который заперт или уже
/// имеет книгу, поэтому получает от смены запись книги — видом «выравнивание», как любая проводка (п. 1.3):
/// закрытые дни «на момент закрытия» остаются как были, разница ложится днём смены.
///
/// <para><b>Затронут</b> оплаченный счёт, чьи деньги при новом допуске не те, что при старом, И который
/// требует исправления (<see cref="ClosedPeriodGuard.NeedOf" />: заперт по границам на момент смены или
/// с книгой). Остальные не пишутся вовсе: незапертый счёт без книги пересчитывается, как до E2, — закрытых
/// дней у него нет.</para>
///
/// <para><b>День остатка.</b> Был остаток — его день остаётся, каким его читали; появился сменой — день
/// самой поздней части с деньгами (правило чтения, п. 0.3), в том числе закрытый; исчез — дня нет. Запись
/// смены штампует его в колонку: так держится инвариант «день в колонке ⇔ остаток есть» (разд. 3).</para>
///
/// <para>⚠️ <b>Замка периода здесь нет</b>, и это не забыто: смену зовёт ядро, держа его на своём
/// соединении (<see cref="IModuleSettingChange" />). Строки счетов запираются по одной, в порядке
/// идентификаторов, — после замка периода, как у всех пишущих.</para>
/// </summary>
public sealed partial class InvoiceDesk
{
    /// <summary>Что сделает смена — без записи и без замков.</summary>
    internal async Task<ToleranceImpact> ToleranceImpactAsync(decimal before, decimal after, CancellationToken ct)
    {
        var moved = await MovedByToleranceAsync(before, after, ct);
        var boundaries = await periods.BoundariesAsync(ct);
        return new(moved.Count, moved.Count(m => ClosedPeriodGuard.NeedOf(m.Invoice, m.Parts, boundaries) is not null));
    }

    /// <summary>
    /// Записать книги затронутым счетам и зафиксировать — своей транзакцией, ДО того, как ядро запишет
    /// значение. Отказ любого счёта — отказ всей смены: книга, записанная половине счетов, разошлась бы с
    /// настройкой, которая не изменилась.
    /// </summary>
    /// <param name="held">Смена от ядра: границы прочитаны под его замком.</param>
    internal async Task<IReadOnlyList<ToleranceMoved>> RetoleranceAsync(
        ModuleSettingChange held, decimal before, decimal after, string reason, CancellationToken ct)
    {
        // Кандидаты — без замков: так их ищет и предупреждение. Под блокировкой строки каждый перечитывается
        // и проверяется заново — между поиском и замком счёт могли исправить или оплатить.
        var candidates = (await MovedByToleranceAsync(before, after, ct))
            .Where(m => ClosedPeriodGuard.NeedOf(m.Invoice, m.Parts, held.Boundaries) is not null)
            .Select(m => m.Invoice.Id)
            .Order()
            .ToList();
        if (candidates.Count == 0) return [];

        var today = await clock.TodayAsync(ct);
        var moved = new List<ToleranceMoved>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var id in candidates)
            {
                // Счёт исчез между поиском и замком (отменили оплату, вернули в черновик, удалили): писать
                // нечего, и смене настройки это не отказ.
                if (!await InvoiceBook.TryLockAsync(db, id, ct)) continue;
                var (invoice, lines, parts) = await ReadForBookAsync(id, ct);
                if (ClosedPeriodGuard.NeedOf(invoice, parts, held.Boundaries) is null) continue;
                if (Retolerated(invoice, lines, parts, before, after) is not { } money) continue;

                // Страховка (п. 1.3 шаг 2) — если книгу этого счёта последней писали при другом допуске: окно
                // между фиксациями прошлой смены или сбой в нём.
                var moneyAt = MoneyOf(invoice, lines, parts);
                await InvoiceBook.AppendAsync(db, invoice,
                    last => new BookChange(InvoiceBookKind.Alignment, money.Before, before, money.After, after,
                        last is { } t && t != before ? moneyAt(t) : null),
                    held, today, new BookAuthor(user.Id, reason), ct);
                invoice.PostRemainder(money.Remainder);
                moved.Add(new(id, InvoiceEndpoints.Label(invoice), before));
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            // Записанное в откаченную транзакцию осталось бы в контексте — и ушло бы с выравниванием, которое
            // ядро зовёт следом тем же контекстом.
            db.ChangeTracker.Clear();
            throw;
        }

        return moved;
    }

    /// <summary>
    /// Выровнять к действующему допуску книги, последняя запись которых посчитана другим, — идемпотентно.
    /// Так чинится сбой между двумя фиксациями смены (модуль записал, ядро — нет) и окно между ними (счёт
    /// исправили, пока ядро ещё не записало значение). Каждый счёт — своей транзакцией под обычными замками:
    /// ядро в это время замков не держит.
    /// </summary>
    /// <param name="reason">Причина записи по допуску, которым посчитана книга.</param>
    /// <param name="failed">Счёт выровнять не удалось: остальные это не останавливает — ни старт приложения,
    /// ни уже записанную смену. Его следующая запись откажет и назовёт расхождение.</param>
    internal async Task<IReadOnlyList<ToleranceMoved>> AlignToleranceAsync(
        decimal current, Func<decimal, string> reason, Action<Guid, Exception> failed, CancellationToken ct)
    {
        var behind = await InvoiceBook.BehindAsync(db, current, ct);
        if (behind.Count == 0) return [];

        var today = await clock.TodayAsync(ct);
        var moved = new List<ToleranceMoved>();
        foreach (var id in behind)
        {
            try
            {
                var done = await db.InOpenPeriodAsync(periods, async boundaries =>
                {
                    if (!await InvoiceBook.TryLockAsync(db, id, ct)) return null;
                    if (await InvoiceBook.LastToleranceAsync(db, id, ct) is not { } last || last == current) return null;
                    var (invoice, lines, parts) = await ReadForBookAsync(id, ct);
                    // Деньги при том и при другом допуске одни — писать нечего: смена, не тронувшая денег счёта,
                    // ему ничего не пишет (п. 1.3).
                    if (Retolerated(invoice, lines, parts, last, current) is not { } money) return null;

                    await InvoiceBook.AppendAsync(db, invoice,
                        new BookChange(InvoiceBookKind.Alignment, money.Before, last, money.After, current),
                        boundaries, today, new BookAuthor(user.Id, reason(last)), ct);
                    invoice.PostRemainder(money.Remainder);
                    await db.SaveChangesAsync(ct);
                    return new ToleranceMoved(id, InvoiceEndpoints.Label(invoice), last);
                }, ct);
                if (done is not null) moved.Add(done);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Записанное в откаченную транзакцию осталось бы в контексте и ушло бы со следующим счётом.
                db.ChangeTracker.Clear();
                failed(id, e);
            }
        }

        return moved;
    }

    /// <summary>
    /// Деньги счёта до и после смены и день остатка, который ляжет в колонку; <c>null</c> — деньги те же
    /// (по суммам пар с НДС и без).
    /// </summary>
    private static (IReadOnlyList<Grain> Before, IReadOnlyList<Grain> After, DateOnly? Remainder)? Retolerated(
        Invoice invoice, IReadOnlyList<InvoiceLine> lines, IReadOnlyList<InvoiceAllocation> parts, decimal from, decimal to)
    {
        var head = (invoice.SupplierId, invoice.Total, invoice.VatTotal, RemainderOn: invoice.RemainderAccountingOn);
        var before = InvoiceBookPosting.Grains(invoice.Id, head, lines, parts, from);
        // День остатка — тот, каким его читали до смены (снимок Before: нет остатка — нет и дня, даже записанного);
        // без него — правилом чтения по деньгам после смены.
        var kept = PaymentPosting.Before(invoice, parts,
            PaymentPosting.Balance(lines.Select(InvoiceAllocations.Line), parts, invoice.Total, from)).Remainder;
        var after = InvoiceBookPosting.Grains(invoice.Id, head with { RemainderOn = kept }, lines, parts, to);

        return InvoiceBookPosting.Same(before, after)
            ? null
            : (before, after, after.FirstOrDefault(g => g.Cell.Target is null)?.Day);
    }

    /// <summary>Оплаченные счета, чьи деньги смена сдвинет, — без отслеживания и без замков.</summary>
    private async Task<List<(Invoice Invoice, List<InvoiceLine> Lines, List<InvoiceAllocation> Parts)>> MovedByToleranceAsync(
        decimal before, decimal after, CancellationToken ct)
    {
        if (before == after) return [];

        // Тремя запросами на кандидатов: смена редкая, а запрос на каждый счёт — по их числу. Кандидаты
        // отобраны в базе (InvoiceBook.Moves): смена идёт под замком записи в учёт, и читать в память все
        // оплаченные счета ради горстки с расхождением значило бы держать закрытие периода всё это время.
        var paid = InvoiceBook.Moves(db,
            db.Invoices.AsNoTracking().Where(i => i.Payment == InvoicePaymentState.Paid), before, after);
        var invoices = await paid.ToListAsync(ct);
        var lines = (await db.InvoiceLines.AsNoTracking().Where(l => paid.Any(i => i.Id == l.InvoiceId))
            .OrderBy(l => l.Ordinal).ToListAsync(ct)).ToLookup(l => l.InvoiceId);
        var parts = (await db.InvoiceAllocations.AsNoTracking().Where(a => paid.Any(i => i.Id == a.InvoiceId))
            .ToListAsync(ct)).ToLookup(a => a.InvoiceId);

        return
        [
            .. invoices
                .Select(i => (Invoice: i, Lines: lines[i.Id].ToList(), Parts: parts[i.Id].ToList()))
                .Where(m => Retolerated(m.Invoice, m.Lines, m.Parts, before, after) is not null),
        ];
    }

    /// <summary>Счёт под блокировкой строки — отслеживаемым, строки и доли — как лежат.</summary>
    private async Task<(Invoice Invoice, List<InvoiceLine> Lines, List<InvoiceAllocation> Parts)> ReadForBookAsync(
        Guid id, CancellationToken ct)
    {
        var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);
        var lines = await db.InvoiceLines.AsNoTracking().Where(l => l.InvoiceId == id).OrderBy(l => l.Ordinal).ToListAsync(ct);
        var parts = await db.InvoiceAllocations.AsNoTracking().Where(a => a.InvoiceId == id).ToListAsync(ct);
        return (invoice, lines, parts);
    }
}
