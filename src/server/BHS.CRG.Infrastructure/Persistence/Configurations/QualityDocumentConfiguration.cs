using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class QualityDocumentConfiguration : IEntityTypeConfiguration<QualityDocument>
{
    public void Configure(EntityTypeBuilder<QualityDocument> b)
    {
        b.ToTable("quality_documents");
        b.HasKey(e => e.Id);
        b.Property(e => e.DocumentTypeId).IsRequired();
        b.Property(e => e.DisplayName).HasMaxLength(512).IsRequired();
        b.Property(e => e.Requisites).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.ScanBlobPath).HasMaxLength(1024);
        b.Property(e => e.ScanFileName).HasMaxLength(512);
        b.Property(e => e.ScanMimeType).HasMaxLength(256);
        b.Property(e => e.Source).HasConversion<int>().IsRequired();
        b.Property(e => e.SourceUrl).HasMaxLength(2048);
        b.Property(e => e.Scope).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.HasIndex(e => new { e.Scope, e.ScopeId });
        b.HasIndex(e => e.DocumentTypeId);
    }
}

public class MaterialQualityLinkConfiguration : IEntityTypeConfiguration<MaterialQualityLink>
{
    public void Configure(EntityTypeBuilder<MaterialQualityLink> b)
    {
        b.ToTable("material_quality_links");
        b.HasKey(e => e.Id);
        b.Property(e => e.MaterialKey).HasMaxLength(512).IsRequired();
        b.Property(e => e.Scope).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(e => e.QualityDocumentId).IsRequired();
        b.HasIndex(e => new { e.Scope, e.ScopeId, e.MaterialKey });
        b.HasIndex(e => e.QualityDocumentId);
    }
}
