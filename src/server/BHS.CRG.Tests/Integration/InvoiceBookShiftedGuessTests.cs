using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Migrations;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Догадка миграции <c>InvoiceBookShifted</c> о строках книги, записанных до PR 6d (ревью PR #1395): «да» —
/// только там, где уверены. С тех пор признак участвует в проводке, и ошибочное «да» уводило бы днём записи
/// пару, которую прежнее правило не уводило; ошибочное «нет» — прежнее поведение.
///
/// <para>Догадка прогоняется тем же текстом, что у миграции (<see cref="InvoiceBookShifted.Guess" />), на живой
/// базе — в транзакции, которая откатывается: текст трогает все строки книги, и чужие тоже.</para>
/// </summary>
[Collection("Integration")]
public class InvoiceBookShiftedGuessTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private static readonly DateOnly Paid = new(2026, 9, 15);
    private static readonly DateOnly Opened = new(2026, 10, 5);
    private static readonly DateOnly Later = new(2026, 10, 6);

    private static Grain Line(Guid site, DateOnly day, decimal gross) =>
        new(new BookCell(AllocationTarget.Site(site), null), day, gross, gross, false);

    [Fact]
    public async Task Строка_дня_записи_перенос_только_без_открытия_или_оплаты_того_же_дня()
    {
        var site = Guid.NewGuid();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();

        // Как лежали до PR 6d: признака нет ни у одной строки.
        var unpaid = Invoice.Create(Guid.NewGuid(), null);
        var repaid = Invoice.Create(Guid.NewGuid(), null);
        db.Invoices.AddRange(unpaid, repaid);
        var lines = new Dictionary<string, Guid>();
        void Write(Invoice invoice, int seq, InvoiceBookKind kind, DateOnly day, string name, Grain grain)
        {
            var entry = InvoiceBookEntry.Create(invoice.Id, seq, new BookRecord(kind, day, 1.00m, [grain]), null, null, null, null);
            var line = InvoiceBookLine.Create(entry.Id, 0, grain);
            db.InvoiceBookEntries.Add(entry);
            db.InvoiceBookLines.Add(line);
            lines[name] = line.Id;
        }

        // Отмена оплаты запертого: строка дня записи, а денег у документа этим днём не было — перенос.
        Write(unpaid, 1, InvoiceBookKind.Opening, Opened, "открытие", Line(site, Paid, 120m));
        Write(unpaid, 2, InvoiceBookKind.Unpayment, Later, "отмена", Line(site, Later, -120m));
        // Оплата днём 06.10, затем исправление того же дня: у документа деньги этим днём были, строка
        // исправления может быть своей — «нет». Оплата — «нет» всегда.
        Write(repaid, 1, InvoiceBookKind.Opening, Opened, "открытие 2", Line(site, Paid, 120m));
        Write(repaid, 2, InvoiceBookKind.Payment, Later, "оплата", Line(site, Later, 50m));
        Write(repaid, 3, InvoiceBookKind.Correction, Later, "исправление", Line(site, Later, 10m));
        await db.SaveChangesAsync();

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(InvoiceBookShifted.Guess);
        var ids = lines.Values.ToList();
        var shifted = await db.InvoiceBookLines.AsNoTracking().Where(l => ids.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.Shifted);
        await tx.RollbackAsync();

        Assert.Equal(
            new Dictionary<string, bool>
            {
                ["открытие"] = false, ["отмена"] = true,
                ["открытие 2"] = false, ["оплата"] = false, ["исправление"] = false,
            },
            lines.ToDictionary(p => p.Key, p => shifted[p.Value]));
    }
}
