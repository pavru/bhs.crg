using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Версия счёта, ушедшая под исправление (E2, issue #1100, spec v3.3 п. 1.4): содержимое счёта и его
/// деньги такими, какими они были ДО исправления, с причиной, автором и временем.
///
/// <para><b>Номер — номер ушедшей версии</b>: первое исправление пишет версию 1 (то, что было до него),
/// второе — версию 2. Тот же номер стоит в заголовке записи книги, которую исправление дописало
/// (<see cref="InvoiceBookEntry.Version" />): по нему «что было» и «как это провели» читаются вместе.</para>
///
/// <para><b>Только дописывается</b> (<see cref="IAppendOnly" />), как и книга: версия — свидетельство о
/// том, что было у бухгалтерии в руках, и правленая версия свидетельствовала бы о другом.</para>
///
/// <para>⚠️ Содержимое лежит одним документом JSON, а не строками и долями в своих таблицах: версия
/// только читается, целиком, и ни один отбор по её полям не нужен. Идентификаторы позиций и строек в
/// нём записей ядра не держат — это история, как и книга (<c>CostsReferences</c>).</para>
/// </summary>
public sealed class InvoiceVersion : IAppendOnly
{
    /// <summary>Для EF.</summary>
    private InvoiceVersion() { }

    public Guid Id { get; private set; }

    public Guid InvoiceId { get; private set; }

    /// <summary>Номер версии в счёте: 1, 2, … Уникален в счёте.</summary>
    public int Number { get; private set; }

    /// <summary>Содержимое и деньги счёта до исправления — документом JSON (<c>InvoiceVersionContent</c>).</summary>
    public string Content { get; private set; } = "{}";

    /// <summary>Причина исправления — от человека, обязательна.</summary>
    public string Reason { get; private set; } = string.Empty;

    public Guid? AuthorId { get; private set; }

    public DateTimeOffset At { get; private set; }

    public static InvoiceVersion Create(Guid invoiceId, int number, string content, string reason, Guid? author) => new()
    {
        Id = Guid.CreateVersion7(),
        InvoiceId = invoiceId,
        Number = number,
        Content = content,
        Reason = reason,
        AuthorId = author,
        At = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Содержимое версии документом JSON: шапка — тем видом, каким её читает форма; строки и части
    /// разноски — значениями с учётными днями; деньги — зёрнами, от которых исправление считало книгу
    /// (<c>M_before</c>). Зёрна лежат здесь, а не выводятся из строк заново: при другом допуске они
    /// вышли бы другими, а версия обязана сказать, от чего считали тогда.
    /// </summary>
    public static string Snapshot(
        Invoice invoice, IReadOnlyList<InvoiceLine> lines, IReadOnlyList<InvoiceAllocation> parts,
        IReadOnlyList<Grain> money) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            requisites = InvoiceRequisites.Merge(invoice),
            total = invoice.Total,
            vatTotal = invoice.VatTotal,
            paidOn = invoice.PaidOn,
            remainderOn = invoice.RemainderAccountingOn,
            lines = lines.OrderBy(l => l.Ordinal).Select(l => new
            {
                id = l.Id, ordinal = l.Ordinal, nomenclature = l.NomenclatureId, supplierText = l.SupplierText,
                supplierCode = l.SupplierCode, unit = l.Unit, quantity = l.Quantity, price = l.Price,
                vatRate = l.VatRate, vatAmount = l.VatAmount, amount = l.Amount, note = l.Note,
            }),
            parts = parts.OrderBy(p => p.LineId).ThenBy(p => p.Ordinal).Select(p => new
            {
                id = p.Id, line = p.LineId, ordinal = p.Ordinal, constructionId = p.ConstructionId,
                sectionId = p.SectionId, articleId = p.ArticleId, quantity = p.Quantity, amount = p.Amount,
                accountingOn = p.AccountingOn,
            }),
            money = money.Select(g => new
            {
                constructionId = g.Cell.Target?.ConstructionId, sectionId = g.Cell.Target?.SectionId,
                articleId = g.Cell.Target?.ArticleId, supplierId = g.Cell.SupplierId, day = g.Day,
                gross = g.Gross, net = g.Net, vatUnknown = g.VatUnknown,
            }),
        });

    internal static void Map(ModelBuilder builder)
    {
        var version = builder.Entity<InvoiceVersion>();
        version.ToTable("invoice_versions");
        version.HasKey(v => v.Id);

        version.Property(v => v.Id).HasColumnName("id");
        version.Property(v => v.InvoiceId).HasColumnName("invoice_id");
        version.Property(v => v.Number).HasColumnName("number");
        version.Property(v => v.Content).HasColumnName("content").HasColumnType("jsonb");
        version.Property(v => v.Reason).HasColumnName("reason");
        version.Property(v => v.AuthorId).HasColumnName("author_id");
        version.Property(v => v.At).HasColumnName("at");

        // Два исправления с одним номером — гонка мимо блокировки строки счёта: отказ ключом.
        version.HasIndex(v => new { v.InvoiceId, v.Number }).IsUnique().HasDatabaseName("ux_invoice_versions_number");

        // RESTRICT, как у книги: счёт с версиями не удаляется (spec п. 1.5), каскад снёс бы их молча.
        version.HasOne<Invoice>().WithMany().HasForeignKey(v => v.InvoiceId).OnDelete(DeleteBehavior.Restrict);
    }
}
