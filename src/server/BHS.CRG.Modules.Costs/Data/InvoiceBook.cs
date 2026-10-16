using System.Linq.Expressions;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Settings;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Кто и почему пишет запись: у исправления — причина и номер версии.</summary>
/// <param name="Name">Имя автора на момент записи — его показывает история счёта (PR 6b-2 E2).</param>
public sealed record BookAuthor(Guid? UserId, string? Reason = null, int? Version = null, string? Name = null)
{
    /// <summary>
    /// Автор — тот, кто действует сейчас, с его нынешним именем. Только так в производственном коде: имя,
    /// забытое одним из мест записи, оставило бы в истории «—» у записей, автор которых известен.
    /// Конструктор вне этого файла модулю запрещает <c>InvoiceBookInventoryTests</c>.
    /// </summary>
    public static BookAuthor Of(IModuleUser user, string? reason = null, int? version = null) =>
        new(user.Id, reason, version, user.Name);
}

/// <summary>Запись книги со строками — как её показывает история счёта.</summary>
public sealed record BookHistoryEntry(InvoiceBookEntry Entry, IReadOnlyList<InvoiceBookLine> Lines);

/// <summary>Последняя запись книги счёта — пометка «исправлен после закрытия» в реестре (PR 6b-3 E2).</summary>
/// <param name="Day">День записи.</param>
/// <param name="Reason">Её причина; у служебной записи и у оплаты — <c>null</c>.</param>
public sealed record BookMark(DateOnly Day, string? Reason);

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
        return await AppendLockedAsync(db, invoice, change, boundaries, today, author, ct);
    }

    /// <summary>
    /// То же — внутри смены настройки (spec v3.3 п. 1.9). Замок записи в учёт держит ЯДРО, на своём
    /// соединении, и границы прочитаны под ним: проверить его отсюда нечем — он не на соединении модуля, —
    /// а взять свой нельзя (<see cref="IModuleSettingChange" />). Поэтому вход отдельный и принимает саму
    /// смену: книгу без замка на этом соединении пишет только тот, кому ядро её передало.
    /// </summary>
    public static Task<BookAppended> AppendAsync(
        CostsDbContext db, Invoice invoice, Func<decimal?, BookChange> change, ModuleSettingChange held,
        DateOnly today, BookAuthor author, CancellationToken ct) =>
        AppendLockedAsync(db, invoice, change, held.Boundaries, today, author, ct);

    private static async Task<BookAppended> AppendLockedAsync(
        CostsDbContext db, Invoice invoice, Func<decimal?, BookChange> change, PeriodBoundaries boundaries,
        DateOnly today, BookAuthor author, CancellationToken ct)
    {
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
            // Причина, версия и имя — только у самой записи (она последняя): открытие и страховку пишет не
            // человек, а следствие его записи, и «Кто: Иванова» у открытия читалось бы так, будто она
            // переложила деньги сама. Идентификатор остаётся у всех — по нему видно, чьей записью вызваны.
            var own = ReferenceEquals(record, records[^1]);
            var entry = InvoiceBookEntry.Create(invoice.Id, ++seq, record, author.UserId,
                own ? author.Name : null, own ? author.Reason : null, own ? author.Version : null);
            db.InvoiceBookEntries.Add(entry);
            db.InvoiceBookLines.AddRange(record.Lines.Select((line, i) => InvoiceBookLine.Create(entry.Id, i, line)));
            entries.Add(entry);
        }

        invoice.RecordBook(seq);
        return new BookAppended(entries, records);
    }

    /// <summary>Книга счёта, как она лежит, — по порядку записей.</summary>
    public static async Task<IReadOnlyList<(int Seq, BookRecord Record)>> ReadAsync(
        CostsDbContext db, Guid invoiceId, CancellationToken ct) =>
        [.. (await HistoryAsync(db, invoiceId, ct)).Select(h => (h.Entry.Seq,
            new BookRecord(h.Entry.Kind, h.Entry.Day, h.Entry.Tolerance, [.. h.Lines.Select(l => l.Value)])))];

    /// <summary>
    /// Книга счёта записями со строками — по порядку записей. Её читает и запись в книгу (под замками), и
    /// история счёта на форме (PR 6b-2 E2) — только показ, без отслеживания.
    /// </summary>
    public static async Task<IReadOnlyList<BookHistoryEntry>> HistoryAsync(
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
        return [.. rows.GroupBy(r => r.Entry.Id, (_, g) => g.ToList()).Select(g => new BookHistoryEntry(g[0].Entry,
            [.. g.Where(r => r.Line is not null).Select(r => r.Line!)]))];
    }

    /// <summary>
    /// День последней записи книги счёта — выражением для запроса: колонка реестра «Исправлен после закрытия»
    /// отбирает и сортирует по нему в базе (PR 6b-3 E2). Только день: денег книги реестр не читает — их
    /// считает <see cref="CostViews" />. Книги нет — <c>null</c>.
    /// </summary>
    public static Expression<Func<Invoice, DateOnly?>> LastDay(CostsDbContext db) =>
        i => db.InvoiceBookEntries.Where(e => e.InvoiceId == i.Id)
            .OrderByDescending(e => e.Seq).Select(e => (DateOnly?)e.Day).FirstOrDefault();

    /// <summary>Последние записи книг названных счетов — для клеток и боковой панели реестра; без книги счёта в ответе нет.</summary>
    public static async Task<IReadOnlyDictionary<Guid, BookMark>> MarksAsync(
        CostsDbContext db, IReadOnlyCollection<Guid> invoiceIds, CancellationToken ct) =>
        await db.InvoiceBookEntries.AsNoTracking()
            .Where(e => invoiceIds.Contains(e.InvoiceId)
                        && !db.InvoiceBookEntries.Any(n => n.InvoiceId == e.InvoiceId && n.Seq > e.Seq))
            .ToDictionaryAsync(e => e.InvoiceId, e => new BookMark(e.Day, e.Reason), ct);

    /// <summary>Допуск, которым посчитана последняя запись книги счёта; книги нет — <c>null</c>.</summary>
    public static Task<decimal?> LastToleranceAsync(CostsDbContext db, Guid invoiceId, CancellationToken ct) =>
        db.InvoiceBookEntries.AsNoTracking().Where(e => e.InvoiceId == invoiceId)
            .OrderByDescending(e => e.Seq).Select(e => (decimal?)e.Tolerance).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Счета, у которых последняя запись книги посчитана НЕ этим допуском И чьи деньги при этом допуске
    /// другие, — кого надо выровнять к действующему (spec п. 1.9). Обычно пусто: смена допуска пишет книги
    /// сама.
    ///
    /// <para>Второе условие — не украшение. Смена, не тронувшая денег счёта, ему ничего не пишет, и
    /// последняя запись остаётся при прежнем допуске навсегда; без условия каждый такой счёт выравнивался бы
    /// после каждой смены и при каждом старте — транзакцией, замками и чтением, кончающимися ничем. Деньги
    /// от допуска зависят только через «расхождение в допуске» (<see cref="AllocationMath.Of" />), и здесь
    /// оно проверяется в базе: <see cref="Moves" />. Под блокировкой строки счёт проверяется заново.</para>
    /// </summary>
    public static Task<List<Guid>> BehindAsync(CostsDbContext db, decimal tolerance, CancellationToken ct) =>
        db.InvoiceBookEntries.AsNoTracking()
            .Where(e => !db.InvoiceBookEntries.Any(n => n.InvoiceId == e.InvoiceId && n.Seq > e.Seq) && e.Tolerance != tolerance)
            .Join(db.Invoices, e => e.InvoiceId, i => i.Id, (e, i) => new
            {
                e.Tolerance,
                i.Id,
                Gap = Math.Abs(i.Total!.Value - db.InvoiceLines.Where(l => l.InvoiceId == i.Id).Sum(l => l.Amount ?? 0m)),
            })
            .Where(x => (x.Gap <= x.Tolerance) != (x.Gap <= tolerance))
            .Select(x => x.Id)
            .OrderBy(id => id)
            .ToListAsync(ct);

    /// <summary>
    /// Счета, чьи деньги при одном допуске могут быть не теми, что при другом, — отбор в базе, до чтения
    /// счетов в память. Допуск решает одно: лежит ли расхождение суммы к оплате со строками в нём
    /// (<see cref="AllocationMath.Of" />, <c>|расхождение| ≤ допуск</c>). Если ответ при обоих допусках один,
    /// деньги одни. Отбор шире точного: счёт без строк и счёт, разнесённый не весь, он пропускает, хотя
    /// расхождение у них в деньги не уходит, — и вызывающий проверяет каждого кандидата сам.
    /// </summary>
    public static IQueryable<Invoice> Moves(CostsDbContext db, IQueryable<Invoice> invoices, decimal from, decimal to) =>
        invoices
            .Select(i => new
            {
                Invoice = i,
                Gap = Math.Abs(i.Total!.Value - db.InvoiceLines.Where(l => l.InvoiceId == i.Id).Sum(l => l.Amount ?? 0m)),
            })
            .Where(x => (x.Gap <= from) != (x.Gap <= to))
            .Select(x => x.Invoice);

    /// <summary>
    /// Запереть строку счёта до конца транзакции. Вне транзакции — отказ: замок снялся бы тем же
    /// запросом, и вызов выглядел бы защитой, не будучи ею.
    /// </summary>
    public static async Task LockAsync(CostsDbContext db, Guid invoiceId, CancellationToken ct)
    {
        if (!await TryLockAsync(db, invoiceId, ct)) throw new NotFoundException("Счёт не найден.");
    }

    /// <summary>
    /// То же для того, кто держит идентификатор, найденный раньше, и отказом исчезнувшему счёту не отвечает
    /// (смена допуска, выравнивание): счёт может исчезнуть под любым кодом — его удалили черновиком.
    /// </summary>
    /// <returns><c>false</c> — счёта больше нет.</returns>
    public static async Task<bool> TryLockAsync(CostsDbContext db, Guid invoiceId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Строка счёта запирается только внутри транзакции: вне её замок снимается тем же запросом.");

        var found = await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM costs.invoices WHERE id = {invoiceId} FOR UPDATE")
            .ToListAsync(ct);
        return found.Count > 0;
    }
}
