using BHS.CRG.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class CatalogEntityConfiguration : IEntityTypeConfiguration<CatalogEntity>
{
    public void Configure(EntityTypeBuilder<CatalogEntity> b)
    {
        b.ToTable("catalog_entities");
        b.HasKey(e => e.Id);
        b.Property(e => e.EntityType).HasMaxLength(64).IsRequired();
        b.Property(e => e.DisplayName).HasMaxLength(512).IsRequired();
        b.Property(e => e.Data).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.OwnerId);
        b.HasIndex(e => e.EntityType);
        b.HasIndex(e => new { e.EntityType, e.OwnerId });
    }
}
