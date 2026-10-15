using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Кто и почему пишет запись: у исправления — причина и номер версии.</summary>
public sealed record BookAuthor(Guid? UserId, string? Reason = null, int? Version = null);

/// <summary>Что легло в книгу: записи и их значения — открытие, выравнивание и сама запись.</summary>
/// <param name="Records">Посчитанное проводкой — по нему отметка предпросмотра (<see cref="InvoiceBookPosting.Stamp" />).</param>
public sealed record BookAppended(IReadOnlyList<InvoiceBookEntry> Entries, IReadOnlyList<BookRecord> Records);

/// <summary>
/// Запись в книгу проводок счёта — единственная (E2, issue #1100, spec v3.3 п. 1.3). Считает
/// <see cref="InvoiceBookPosting" />; здесь — то, без чего посчитанное нельзя положить.
///
/// <para><b>Порядок замков — период, потом строка счёта</b> (п. 1.3). Замок периода держит
/// вызывающий (<see cref="OpenPeriodWrite.InOpenPeriodAsync{T}" />): границы, по которым посчитана
/// запись, взяты под ним. Строку счёта запирает этот метод — <c>FOR UPDATE</c> до чтения книги.</para>
///
/// <para><b>От двух записей от одного «до» защищают три вещи, и главная — версия строки счёта.</b>
/// Номер записи лежит в строке счёта (<see cref="Invoice.BookSeq" />), запись его правит, а строка
/// сверяет версию (<c>xmin</c>) на сохранении — вторая запись от того же «до» упадёт там отказом 409.
/// Сверка номера ниже говорит то же раньше и своими словами, а уникальный ключ (счёт, номер) — последняя
/// дверь для кода, который обошёл бы обе. <c>FOR UPDATE</c> ставит записи в очередь: вторая ждёт первую
/// и отказывает ДО того, как посчитает проводку по чужому «до», — а связка записи (PR 2 E2) читает под
/// этим же замком весь счёт, строки и разноску одним срезом (spec v3.3 п. 1.3). Он же двигает версию
/// формы: открытая до записи в книгу, она получит «счёт тем временем изменили» (п. 1.1).</para>
///
/// <para><b>Сохраняет вызывающий</b>, одним сохранением с правкой счёта: книга, записанная отдельно от
/// того, что она проводит, при отказе второго сохранения осталась бы впереди документа.</para>
/// </summary>
public static class InvoiceBook
{
    /// <summary>Положить запись в книгу счёта (в контекст; сохраняет вызывающий).</summary>
    /// <param name="boundaries">Границы из замка периода, под которым идёт запись.</param>
    /// <param name="today">Сегодня по часам компании.</param>
    public static Task<BookAppended> AppendAsync(
        CostsDbContext db, Invoice invoice, BookChange change, PeriodBoundaries boundaries, DateOnly today,
        BookAuthor author, CancellationToken ct) =>
        AppendAsync(db, invoice, _ => change, boundaries, today, author, ct);

    /// <summary>
    /// То же — с «до», которое вызывающий собирает, уже зная допуск последней записи книги: деньги при
    /// нём нужны страховке (spec п. 1.3 шаг 2), а прочитать его можно только здесь, под блокировкой строки.
    /// </summary>
    /// <param name="change">Что сдвигает запись — по допуску последней записи книги; книги нет — <c>null</c>.</param>
    public static async Task<BookAppended> AppendAsync(
        CostsDbContext db, Invoice invoice, Func<decimal?, BookChange> change, PeriodBoundaries boundaries,
        DateOnly today, BookAuthor author, CancellationToken ct)
    {
        await OpenPeriodWrite.EnsureHeldAsync(db, ct);
        await LockAsync(db, invoice.Id, ct);

        var stored = await ReadAsync(db, invoice.Id, ct);
        if ((stored.Count == 0 ? 0 : stored[^1].Seq) != invoice.BookSeq)
            throw new ConflictException(
                "Книгу счёта тем временем дописали, и эта правка не записана. Перечитайте счёт и " +
                "повторите: посчитанная от прежнего «до», она провела бы те же деньги второй раз.");

        var records = InvoiceBookPosting.Post([.. stored.Select(e => e.Record)],
            change(stored.Count == 0 ? null : stored[^1].Record.Tolerance), boundaries, today);

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
        return new BookAppended(entries, records);
    }

    /// <summary>Книга счёта, как она лежит, — по порядку записей.</summary>
    public static async Task<IReadOnlyList<(int Seq, BookRecord Record)>> ReadAsync(
        CostsDbContext db, Guid invoiceId, CancellationToken ct)
    {
        // Одним запросом (ревью PR #1357): запись в книгу читает её под двумя замками, и лишний проход с
        // растущим списком номеров записей держал бы их дольше. Запись без строк — левым соединением.
        var rows = await (
                from e in db.InvoiceBookEntries.AsNoTracking().Where(e => e.InvoiceId == invoiceId)
                join l in db.InvoiceBookLines.AsNoTracking() on e.Id equals l.EntryId into lines
                from l in lines.DefaultIfEmpty()
                orderby e.Seq, l == null ? 0 : l.Ordinal
                select new { Entry = e, Line = l })
            .ToListAsync(ct);

        // Группы — в порядке первого появления, то есть по номеру записи.
        return [.. rows.GroupBy(r => r.Entry.Id, (_, g) => g.ToList()).Select(g => (g[0].Entry.Seq,
            new BookRecord(g[0].Entry.Kind, g[0].Entry.Day, g[0].Entry.Tolerance,
                [.. g.Where(r => r.Line is not null).Select(r => r.Line!.Value)])))];
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
