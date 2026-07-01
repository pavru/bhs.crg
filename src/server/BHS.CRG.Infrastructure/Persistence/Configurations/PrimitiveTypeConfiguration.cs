using BHS.CRG.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class PrimitiveTypeConfiguration : IEntityTypeConfiguration<PrimitiveType>
{
    public void Configure(EntityTypeBuilder<PrimitiveType> b)
    {
        b.ToTable("primitive_types");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(256).IsRequired();
        b.Property(e => e.Code).HasMaxLength(64).IsRequired();
        b.HasIndex(e => e.Code).IsUnique();
        b.Property(e => e.BaseType).HasMaxLength(16).IsRequired();
        b.Property(e => e.Description).HasMaxLength(512);
        b.Property(e => e.Constraints).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.AllowedTags).HasColumnType("text[]").IsRequired().HasDefaultValueSql("'{}'::text[]");
        b.Property(e => e.Group).HasMaxLength(256);
    }
}
