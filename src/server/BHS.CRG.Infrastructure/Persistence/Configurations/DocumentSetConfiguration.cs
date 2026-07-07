using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class DocumentSetConfiguration : IEntityTypeConfiguration<DocumentSet>
{
    public void Configure(EntityTypeBuilder<DocumentSet> b)
    {
        b.ToTable("document_sets");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(512).IsRequired();
        b.HasMany(e => e.Instances)
         .WithOne()
         .HasForeignKey(i => i.DocumentSetId)
         .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => e.SectionId);
    }
}

public class DocumentInstanceConfiguration : IEntityTypeConfiguration<DocumentInstance>
{
    public void Configure(EntityTypeBuilder<DocumentInstance> b)
    {
        b.ToTable("document_instances");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(512);
        b.Property(e => e.Requisites).HasColumnType("jsonb");
        b.Property(e => e.PluginData).HasColumnType("jsonb");
        b.Property(e => e.TemplateParams).HasColumnType("jsonb");
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        b.HasMany(e => e.GeneratedFiles)
         .WithOne()
         .HasForeignKey(f => f.DocumentInstanceId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

public class GeneratedFileConfiguration : IEntityTypeConfiguration<GeneratedFile>
{
    public void Configure(EntityTypeBuilder<GeneratedFile> b)
    {
        b.ToTable("generated_files");
        b.HasKey(e => e.Id);
        b.Property(e => e.BlobPath).HasMaxLength(1024).IsRequired();
        b.Property(e => e.Format).HasConversion<string>().HasMaxLength(16);
    }
}
