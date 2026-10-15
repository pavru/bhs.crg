using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Книга проводок счёта в базе (E2, issue #1100, PR 1): то, чего чистая проводка не видит, — замки,
/// номер записи у счёта, «только дописывается» и запрет удалить счёт с историей.
///
/// <para>Производственного вызова у записи в книгу в PR 1 нет — он придёт с адресом исправления (PR 2).
/// Поэтому здесь запись зовётся напрямую, ровно так, как её позовёт связка: в транзакции, под замком
/// периода, одним сохранением с правкой счёта.</para>
/// </summary>
[Collection("Integration")]
public class InvoiceBookTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private static readonly DateOnly Paid = new(2026, 9, 15);
    private static readonly DateOnly Today = new(2026, 10, 5);

    private static BookChange Change(Guid site, decimal from, decimal to) => new(InvoiceBookKind.Correction,
        [new Grain(new BookCell(AllocationTarget.Site(site), null), Paid, from, from, false)], 1.00m,
        [new Grain(new BookCell(AllocationTarget.Site(site), null), Paid, to, to, false)], 1.00m);

    private async Task<Guid> InvoiceAsync()
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var invoice = Invoice.Create(Guid.NewGuid(), null);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.Id;
    }

    /// <summary>Записать в книгу так, как это сделает связка: транзакция, замок периода, одно сохранение.</summary>
    private async Task<IReadOnlyList<InvoiceBookEntry>> AppendAsync(Guid id, BookChange change, string? version = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var periods = scope.ServiceProvider.GetRequiredService<IModulePeriods>();
        return await db.InOpenPeriodAsync(periods, async boundaries =>
        {
            var invoice = await db.Invoices.SingleAsync(i => i.Id == id);
            if (version is not null) Assert.Equal(version, db.VersionOf(invoice));
            var entries = await InvoiceBook.AppendAsync(db, invoice, change, boundaries, Today, new BookAuthor(null, "опечатка"), default);
            await db.SaveChangesAsync();
            return entries.Entries;
        });
    }

    [Fact]
    public async Task Запись_ложится_в_книгу_двигает_номер_и_версию_счёта()
    {
        var site = Guid.NewGuid();
        var id = await InvoiceAsync();
        string before;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
            before = db.VersionOf(await db.Invoices.SingleAsync(i => i.Id == id));
        }

        var first = await AppendAsync(id, Change(site, 120m, 132m), before);
        Assert.Equal([(1, InvoiceBookKind.Opening, (string?)null), (2, InvoiceBookKind.Correction, "опечатка")],
            first.Select(e => (e.Seq, e.Kind, e.Reason)));

        using var check = host.Services.CreateScope();
        var costs = check.ServiceProvider.GetRequiredService<CostsDbContext>();
        var invoice = await costs.Invoices.SingleAsync(i => i.Id == id);
        Assert.Equal(2, invoice.BookSeq);
        Assert.NotEqual(before, costs.VersionOf(invoice));

        // Книга читается обратно тем же значением, каким её посчитали.
        var stored = await InvoiceBook.ReadAsync(costs, id, default);
        Assert.Equal([Today, Today], stored.Select(s => s.Record.Day));
        Assert.Equal([120m, 12m], stored.Select(s => s.Record.Lines.Sum(l => l.Gross)));
        Assert.Equal(site, stored[1].Record.Lines.Single().Cell.Target?.ConstructionId);

        // Вторая запись продолжает нумерацию и открытия уже не пишет.
        var second = await AppendAsync(id, Change(site, 132m, 144m));
        Assert.Equal([3], second.Select(e => e.Seq));
    }

    /// <summary>
    /// Книгу дописали, пока эта запись держала прежний счёт: продолжение от прежнего «до» провело бы
    /// те же деньги второй раз — отказ, а не вторая ветка книги.
    /// </summary>
    [Fact]
    public async Task Запись_от_прежнего_номера_книги_отказывает()
    {
        var site = Guid.NewGuid();
        var id = await InvoiceAsync();

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var periods = scope.ServiceProvider.GetRequiredService<IModulePeriods>();
        var stale = await db.Invoices.SingleAsync(i => i.Id == id);

        await AppendAsync(id, Change(site, 120m, 132m));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => db.InOpenPeriodAsync(periods, boundaries =>
            InvoiceBook.AppendAsync(db, stale, Change(site, 120m, 132m), boundaries, Today, new BookAuthor(null), default)));
        Assert.Contains("тем временем дописали", refusal.Message);
    }

    /// <summary>
    /// Запись берёт строку счёта FOR UPDATE: вторая транзакция ждёт её — и, не дождавшись, отказывает.
    /// Без этого две записи, прочитавшие одно «до», прошли бы обе: дописанная книга версию счёта не
    /// двигает до сохранения.
    /// </summary>
    [Fact]
    public async Task Строка_счёта_заперта_до_конца_записи()
    {
        var id = await InvoiceAsync();

        using var first = host.Services.CreateScope();
        var holder = first.ServiceProvider.GetRequiredService<CostsDbContext>();
        await using var held = await holder.Database.BeginTransactionAsync();
        await InvoiceBook.LockAsync(holder, id, default);

        using var second = host.Services.CreateScope();
        var waiter = second.ServiceProvider.GetRequiredService<CostsDbContext>();
        await using var waiting = await waiter.Database.BeginTransactionAsync();
        await waiter.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'");

        var timeout = await Assert.ThrowsAnyAsync<Exception>(() => InvoiceBook.LockAsync(waiter, id, default));
        Assert.Contains("55P03", timeout.ToString());

        await held.CommitAsync();
    }

    /// <summary>
    /// Обычная правка счёта тоже запирает строку счёта — до сверки версии (E2, PR 2, spec v3.3 п. 1.3):
    /// порядок замков у всех пишущих один, период → строка. Пока строку держит другая транзакция, правка
    /// ждёт; отпустили — проходит.
    /// </summary>
    [Fact]
    public async Task Обычная_правка_ждёт_блокировку_строки_счёта()
    {
        var (client, _) = await SignInAsync("Admin");
        var id = await CreateAsync(client);

        using var scope = host.Services.CreateScope();
        var holder = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var held = await holder.Database.BeginTransactionAsync();
        await InvoiceBook.LockAsync(holder, id, default);

        var pending = System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(
            client, $"/api/costs/invoices/{id}/confirmed", new { fields = new[] { "Номер" } });
        await Task.Delay(700);
        Assert.False(pending.IsCompleted, "Правка прошла, пока строка счёта заперта: связка её не запирает.");

        await held.CommitAsync();
        await held.DisposeAsync();
        await OkAsync(await pending);
    }

    /// <summary>
    /// Вне транзакции и без замка периода — отказ: границы, по которым посчитана запись, могли
    /// устареть, а закрытие — пройти посреди неё.
    /// </summary>
    [Fact]
    public async Task Без_транзакции_или_замка_периода_запись_отказывает()
    {
        var id = await InvoiceAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var invoice = await db.Invoices.SingleAsync(i => i.Id == id);
        Task Append() => InvoiceBook.AppendAsync(db, invoice, Change(Guid.NewGuid(), 120m, 132m),
            PeriodBoundaries.None, Today, new BookAuthor(null), default);

        await Assert.ThrowsAsync<InvalidOperationException>(Append);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(Append);
        Assert.Contains("без замка", refusal.Message);

        await OpenPeriodWrite.HoldAsync(db);
        await Append();
    }

    /// <summary>Строки книги не правятся и не удаляются — отказ на сохранении, а не тихая перепись истории.</summary>
    [Fact]
    public async Task Книга_только_дописывается()
    {
        var id = await InvoiceAsync();
        await AppendAsync(id, Change(Guid.NewGuid(), 120m, 132m));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var entries = await db.InvoiceBookEntries.Where(e => e.InvoiceId == id).ToListAsync();
        var ids = entries.Select(e => e.Id).ToList();
        var line = await db.InvoiceBookLines.FirstAsync(l => ids.Contains(l.EntryId));

        db.Entry(line).Property(l => l.Gross).CurrentValue = 1m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        db.InvoiceBookEntries.Remove(await db.InvoiceBookEntries.FirstAsync(e => e.InvoiceId == id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// Счёт с книгой не удаляется даже мимо связки: внешний ключ книги — RESTRICT. Каскад снёс бы
    /// историю молча — отказ дописываемого набора видит только отслеживаемые строки.
    /// </summary>
    [Fact]
    public async Task Счёт_с_книгой_база_удалить_не_даёт()
    {
        var id = await InvoiceAsync();
        await AppendAsync(id, Change(Guid.NewGuid(), 120m, 132m));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"DELETE FROM costs.invoices WHERE id = {id}"));
        Assert.Equal("FK_invoice_book_entries_invoices_invoice_id", error.ConstraintName);
    }
}
