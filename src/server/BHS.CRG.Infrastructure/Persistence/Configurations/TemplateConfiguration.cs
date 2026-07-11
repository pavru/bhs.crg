using BHS.CRG.Domain.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    public void Configure(EntityTypeBuilder<Template> b)
    {
        b.ToTable("templates");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(256).IsRequired();
        b.Property(e => e.Content).IsRequired();
        b.Property(e => e.Parameters).HasColumnType("jsonb");
        b.Property(e => e.Version).IsRequired();
        b.Property(e => e.IsActive).IsRequired();
        b.Property(e => e.IsDefault).IsRequired();
        b.HasIndex(e => new { e.DocumentTypeId, e.IsActive });
        b.HasIndex(e => new { e.DocumentTypeId, e.IsDefault });
    }
}
