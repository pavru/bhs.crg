using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class DocumentTypeConfiguration : IEntityTypeConfiguration<DocumentType>
{
    public void Configure(EntityTypeBuilder<DocumentType> b)
    {
        b.ToTable("document_types");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(256).IsRequired();
        b.Property(e => e.Code).HasMaxLength(64).IsRequired();
        b.HasIndex(e => e.Code).IsUnique();
        b.Property(e => e.Kind)
         .HasConversion<string>()
         .HasMaxLength(32)
         .HasDefaultValue(DocumentTypeKind.Document)
         .IsRequired();
        b.Property(e => e.IsAbstract).HasDefaultValue(false).IsRequired();
        b.Property(e => e.ParentId).IsRequired(false);
        b.HasOne<DocumentType>()
         .WithMany()
         .HasForeignKey(e => e.ParentId)
         .IsRequired(false)
         .OnDelete(DeleteBehavior.Restrict);
        b.Property(e => e.Schema).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.PluginBindings).HasColumnType("jsonb");
        b.Property(e => e.Group).HasMaxLength(256);
    }
}
