using BHS.CRG.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class CommonDataEntryConfiguration : IEntityTypeConfiguration<CommonDataEntry>
{
    public void Configure(EntityTypeBuilder<CommonDataEntry> b)
    {
        b.ToTable("common_data_entries");
        b.HasKey(e => e.Id);
        b.Property(e => e.DisplayName).HasMaxLength(512).IsRequired();
        // Алиасы (issue #74) — Postgres text[]; сопоставление идёт в памяти, индексация не требуется.
        b.Property(e => e.Aliases).HasColumnType("text[]").IsRequired();
        b.Property(e => e.CompositeTypeId).IsRequired();
        b.Property(e => e.Data).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.Scope)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        b.Property(e => e.ScopeId);
        b.HasIndex(e => new { e.Scope, e.ScopeId });
        b.HasIndex(e => e.CompositeTypeId);
    }
}
