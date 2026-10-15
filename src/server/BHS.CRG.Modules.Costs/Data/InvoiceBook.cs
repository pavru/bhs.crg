using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Кто и почему пишет запись: у исправления — причина и номер версии.</summary>
public sealed record BookAuthor(Guid? UserId, string? Reason = null, int? Version = null);

/// <summary>
/// Запись в книгу проводок счёта — единственная (E2, issue #1100, spec v3.3 п. 1.3). Считает
/// <see cref="InvoiceBookPosting" />; здесь — то, без чего посчитанное нельзя положить.
///
/// <para><b>Порядок замков — период, потом строка счёта</b> (п. 1.3). Замок периода держит
/// вызывающий (<see cref="OpenPeriodWrite.InOpenPeriodAsync{T}" />): границы, по которым посчитана
/// запись, взяты под ним. Строку счёта запирает этот метод — <c>FOR UPDATE</c> до чтения книги. Без неё
/// две записи, прочитавшие одно «до», обе прошли бы и дописали бы книгу дважды: дописанная книга
/// версию счёта (<c>xmin</c>) не двигает, и сверка версии формы этого не увидела бы.</para>
///
/// <para><b>Номер записи — в строке счёта</b> (<see cref="Invoice.BookSeq" />): правка строки двигает
/// её версию, поэтому форма, открытая до записи в книгу, получит «счёт тем временем изменили» (п. 1.1).</para>
///
/// <para><b>Сохраняет вызывающий</b>, одним сохранением с правкой счёта: книга, записанная отдельно от
/// того, что она проводит, при отказе второго сохранения осталась бы впереди документа.</para>
/// </summary>
public static class InvoiceBook
{
    /// <summary>Положить запись в книгу счёта (в контекст; сохраняет вызывающий).</summary>
    /// <param name="boundaries">Границы из замка периода, под которым идёт запись.</param>
    /// <param name="today">Сегодня по часам компании.</param>
    /// <returns>Записи, легшие в книгу: открытие, выравнивание и сама запись.</returns>
    public static async Task<IReadOnlyList<InvoiceBookEntry>> AppendAsync(
        CostsDbContext db, Invoice invoice, BookChange change, PeriodBoundaries boundaries, DateOnly today,
        BookAuthor author, CancellationToken ct)
    {
        await OpenPeriodWrite.EnsureHeldAsync(db, ct);
        await LockAsync(db, invoice.Id, ct);

        var stored = await ReadAsync(db, invoice.Id, ct);
        if ((stored.Count == 0 ? 0 : stored[^1].Seq) != invoice.BookSeq)
            throw new ConflictException(
                "Книгу счёта тем временем дописали, и эта правка не записана. Перечитайте счёт и " +
                "повторите: посчитанная от прежнего «до», она провела бы те же деньги второй раз.");

        var records = InvoiceBookPosting.Post([.. stored.Select(e => e.Record)], change, boundaries, today);

        var entries = new List<InvoiceBookEntry>();
        var seq = invoice.BookSeq;
        foreach (var record in records)
        {
            // Причина и версия — только у самой записи (она последняя): открытие и страховку пишет не человек.
            var own = ReferenceEquals(record, records[^1]);
            var entry = InvoiceBookEntry.Create(invoice.Id, ++seq, record, author.UserId,
                own ? author.Reason : null, own ? author.Version : null);
            db.InvoiceBookEntries.Add(entry);
            db.InvoiceBookLines.AddRange(record.Lines.Select((line, i) => InvoiceBookLine.Create(entry.Id, i, line)));
            entries.Add(entry);
        }

        invoice.RecordBook(seq);
        return entries;
    }

    /// <summary>Книга счёта, как она лежит, — по порядку записей.</summary>
    public static async Task<IReadOnlyList<(int Seq, BookRecord Record)>> ReadAsync(
        CostsDbContext db, Guid invoiceId, CancellationToken ct)
    {
        var entries = await db.InvoiceBookEntries.AsNoTracking()
            .Where(e => e.InvoiceId == invoiceId).OrderBy(e => e.Seq).ToListAsync(ct);
        var ids = entries.Select(e => e.Id).ToList();
        var lines = (await db.InvoiceBookLines.AsNoTracking()
                .Where(l => ids.Contains(l.EntryId)).OrderBy(l => l.Ordinal).ToListAsync(ct))
            .ToLookup(l => l.EntryId);

        return [.. entries.Select(e => (e.Seq, new BookRecord(e.Kind, e.Day, e.Tolerance,
            [.. lines[e.Id].Select(l => new BookLine(l.Cell, l.Day, l.Gross, l.Net, l.VatUnknown))])))];
    }

    /// <summary>
    /// Запереть строку счёта до конца транзакции. Вне транзакции — отказ: замок снялся бы тем же
    /// запросом, и вызов выглядел бы защитой, не будучи ею.
    /// </summary>
    public static async Task LockAsync(CostsDbContext db, Guid invoiceId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Строка счёта запирается только внутри транзакции: вне её замок снимается тем же запросом.");

        var found = await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM costs.invoices WHERE id = {invoiceId} FOR UPDATE")
            .ToListAsync(ct);
        if (found.Count == 0) throw new NotFoundException("Счёт не найден.");
    }
}
