using BHS.CRG.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class RenditionConfiguration : IEntityTypeConfiguration<RenditionRecord>
{
    /// <summary>Имя таблицы; его же называет уборка осиротевших, исключая колонку оригинала из отбора.</summary>
    public const string Table = "renditions";

    /// <summary>Колонка с путём оригинала — та, которую уборка держателем не считает.</summary>
    public const string OriginalColumn = nameof(RenditionRecord.OriginalBlobPath);

    public void Configure(EntityTypeBuilder<RenditionRecord> b)
    {
        b.ToTable(Table);
        b.HasKey(e => e.Id);

        b.Property(e => e.OriginalBlobPath).HasMaxLength(1024).IsRequired();
        b.Property(e => e.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(e => e.Mime).HasMaxLength(128);
        b.Property(e => e.ImageBlobPath).HasMaxLength(1024);
        b.Property(e => e.RefusalKind).HasMaxLength(32);
        b.Property(e => e.RefusalReason).HasMaxLength(1024);
        b.Property(e => e.Converter).HasMaxLength(64);

        // Запись у пути одна. Блокировка по пути живёт в процессе, а это — то, что останется верным
        // и при двух процессах: проигравший получит отказ вставки, а не вторую запись.
        b.HasIndex(e => e.OriginalBlobPath).IsUnique();
    }
}
