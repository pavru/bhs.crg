using BHS.CRG.Domain.DataSets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class DataSetFileConfiguration : IEntityTypeConfiguration<DataSetFile>
{
    public void Configure(EntityTypeBuilder<DataSetFile> b)
    {
        b.ToTable("dataset_files");
        b.Property(e => e.PreprocessingProfile).HasMaxLength(64);
        b.Property(e => e.Grouping).HasColumnType("jsonb");
        b.Property(e => e.RecognitionStale).HasDefaultValue(false);
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(512).IsRequired();
        b.Property(e => e.Format).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(e => e.BlobPath).HasMaxLength(1024).IsRequired();
        b.Property(e => e.Scope).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(e => e.ScopeId);
        b.HasMany(e => e.Sources)
         .WithOne(s => s.File)
         .HasForeignKey(s => s.FileId)
         .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => new { e.Scope, e.ScopeId });
    }
}

public class DataSetSourceConfiguration : IEntityTypeConfiguration<DataSetSource>
{
    public void Configure(EntityTypeBuilder<DataSetSource> b)
    {
        b.ToTable("dataset_sources");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(256).IsRequired();
        b.Property(e => e.SheetOrPath).HasMaxLength(1024).IsRequired();
        b.Property(e => e.ColumnExpressions).HasColumnType("jsonb");
        b.Property(e => e.CachedSchema).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.CachedRowCount).IsRequired();
        b.Property(e => e.RowFilter).HasColumnType("jsonb");
        b.Property(e => e.ComputedColumns).HasColumnType("jsonb");
        b.Property(e => e.SortSpec).HasColumnType("jsonb");
        b.Property(e => e.CachedData).HasColumnType("jsonb");
        b.Property(e => e.Tags).HasColumnType("jsonb");
        b.Property(e => e.MaterializeTypeId);
        b.Property(e => e.MaterializeMapping).HasColumnType("jsonb");
        b.HasMany(e => e.Bindings)
         .WithOne(b => b.Source)
         .HasForeignKey(b => b.SourceId)
         .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(e => e.FileId);
    }
}

public class DataSetProcessingTemplateConfiguration : IEntityTypeConfiguration<DataSetProcessingTemplate>
{
    public void Configure(EntityTypeBuilder<DataSetProcessingTemplate> b)
    {
        b.ToTable("dataset_processing_templates");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(256).IsRequired();
        b.Property(e => e.SheetOrPath).HasMaxLength(1024);
        b.Property(e => e.ColumnExpressions).HasColumnType("jsonb");
        b.Property(e => e.RowFilter).HasColumnType("jsonb");
        b.Property(e => e.ComputedColumns).HasColumnType("jsonb");
        b.Property(e => e.SortSpec).HasColumnType("jsonb");
    }
}

public class DataSetBindingConfiguration : IEntityTypeConfiguration<DataSetBinding>
{
    public void Configure(EntityTypeBuilder<DataSetBinding> b)
    {
        b.ToTable("dataset_bindings");
        b.HasKey(e => e.Id);
        b.Property(e => e.TargetFieldKey).HasMaxLength(256);
        b.Property(e => e.Mapping).HasColumnType("jsonb").IsRequired();
        b.HasIndex(e => e.InstanceId);
        b.HasIndex(e => e.CommonDataEntryId);
        b.HasIndex(e => e.SourceId);
    }
}

public class DataSetBindingTemplateConfiguration : IEntityTypeConfiguration<DataSetBindingTemplate>
{
    public void Configure(EntityTypeBuilder<DataSetBindingTemplate> b)
    {
        b.ToTable("dataset_binding_templates");
        b.HasKey(e => e.Id);
        b.Property(e => e.DocumentTypeId).IsRequired();
        b.Property(e => e.Name).HasMaxLength(256).IsRequired();
        b.Property(e => e.TargetFieldKey).HasMaxLength(256);
        b.Property(e => e.ColumnMappings).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.SortOrder).IsRequired();
        b.HasIndex(e => e.DocumentTypeId);
    }
}
