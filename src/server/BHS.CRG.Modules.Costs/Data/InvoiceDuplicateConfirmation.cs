using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Решение по двум редакциям счетов и данные для повторной доставки события в журнал.</summary>
public sealed class InvoiceDuplicateConfirmation
{
    public Guid EventId { get; init; }
    public Guid FirstInvoiceId { get; init; }
    public Guid FirstRevision { get; init; }
    public Guid SecondInvoiceId { get; init; }
    public Guid SecondRevision { get; init; }
    public required string FirstLabel { get; init; }
    public required string SecondLabel { get; init; }
    public required string AuthorId { get; init; }
    public required string AuthorName { get; init; }
    public DateTimeOffset ConfirmedAt { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset? JournaledAt { get; private set; }

    public const int ReasonLimit = 500;

    internal void MarkJournaled(DateTimeOffset at) => JournaledAt = at;

    internal static (Invoice First, Invoice Second) Ordered(Invoice one, Invoice other) =>
        one.Id.CompareTo(other.Id) < 0 ? (one, other) : (other, one);

    internal static void Configure(ModelBuilder builder, EntityTypeBuilder<Invoice> invoice)
    {
        invoice.Property(i => i.DuplicateRevision).HasColumnName("duplicate_revision")
            .HasDefaultValueSql("gen_random_uuid()").ValueGeneratedOnAddOrUpdate();

        var pair = builder.Entity<InvoiceDuplicateConfirmation>();
        pair.ToTable("invoice_duplicate_confirmations", table =>
            table.HasCheckConstraint("ck_invoice_duplicate_order", "first_invoice_id < second_invoice_id"));
        pair.HasKey(x => x.EventId);
        pair.Property(x => x.EventId).HasColumnName("event_id").ValueGeneratedNever();
        pair.Property(x => x.FirstInvoiceId).HasColumnName("first_invoice_id");
        pair.Property(x => x.FirstRevision).HasColumnName("first_revision");
        pair.Property(x => x.SecondInvoiceId).HasColumnName("second_invoice_id");
        pair.Property(x => x.SecondRevision).HasColumnName("second_revision");
        pair.Property(x => x.FirstLabel).HasColumnName("first_label");
        pair.Property(x => x.SecondLabel).HasColumnName("second_label");
        pair.Property(x => x.AuthorId).HasColumnName("author_id");
        pair.Property(x => x.AuthorName).HasColumnName("author_name");
        pair.Property(x => x.ConfirmedAt).HasColumnName("confirmed_at");
        pair.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(ReasonLimit);
        pair.Property(x => x.JournaledAt).HasColumnName("journaled_at");
        // Без внешних ключей: удаление черновика не стирает состоявшееся решение.
        pair.HasIndex(x => new { x.FirstInvoiceId, x.FirstRevision, x.SecondInvoiceId, x.SecondRevision })
            .IsUnique().HasDatabaseName("ix_invoice_duplicate_pair");
        pair.HasIndex(x => new { x.SecondInvoiceId, x.SecondRevision }).HasDatabaseName("ix_invoice_duplicate_second");
        pair.HasIndex(x => x.ConfirmedAt).HasFilter("journaled_at IS NULL").HasDatabaseName("ix_invoice_duplicate_pending");
    }
}
