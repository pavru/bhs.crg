using BHS.CRG.Domain.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class TemplateAssetConfiguration : IEntityTypeConfiguration<TemplateAsset>
{
    public void Configure(EntityTypeBuilder<TemplateAsset> b)
    {
        b.ToTable("template_assets");
        b.HasKey(e => e.Id);
        b.Property(e => e.Scope).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(e => e.ScopeId);
        b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(e => e.Name).HasMaxLength(256).IsRequired();
        b.Property(e => e.FileName).HasMaxLength(512).IsRequired();
        b.Property(e => e.MimeType).HasMaxLength(128).IsRequired();
        b.Property(e => e.BlobPath).HasMaxLength(1024).IsRequired();
        b.Property(e => e.FontFamilyName).HasMaxLength(256);
        b.HasIndex(e => new { e.Scope, e.ScopeId });
        // Уникальность Name только для Image — там это реальный ключ поиска (image("assets/{Name}.{ext}")).
        // Для Font Name — информационное поле (Typst резолвит по FontFamilyName из файла), уникальность не нужна.
        b.HasIndex(e => new { e.Scope, e.ScopeId, e.Kind, e.Name })
            .IsUnique()
            .HasFilter("\"Kind\" = 'Image'");
    }
}
