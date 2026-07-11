using BHS.CRG.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class EnumTypeConfiguration : IEntityTypeConfiguration<EnumType>
{
    public void Configure(EntityTypeBuilder<EnumType> b)
    {
        b.ToTable("enum_types");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(256).IsRequired();
        b.Property(e => e.Code).HasMaxLength(64).IsRequired();
        b.HasIndex(e => e.Code).IsUnique();
        b.Property(e => e.Description).HasMaxLength(512);
        b.Property(e => e.Values).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.Group).HasMaxLength(256);
    }
}
