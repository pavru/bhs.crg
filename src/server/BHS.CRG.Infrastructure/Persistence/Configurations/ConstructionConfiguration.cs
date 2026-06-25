using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class ConstructionConfiguration : IEntityTypeConfiguration<Construction>
{
    public void Configure(EntityTypeBuilder<Construction> b)
    {
        b.ToTable("constructions");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(512).IsRequired();
        b.HasMany(e => e.Sections)
         .WithOne()
         .HasForeignKey(s => s.ConstructionId)
         .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => e.CreatedByUserId);
    }
}

public class SectionConfiguration : IEntityTypeConfiguration<Section>
{
    public void Configure(EntityTypeBuilder<Section> b)
    {
        b.ToTable("sections");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(512).IsRequired();
        b.HasMany(e => e.DocumentSets)
         .WithOne()
         .HasForeignKey(ds => ds.SectionId)
         .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => e.ConstructionId);
    }
}
