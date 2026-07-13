using BHS.CRG.Domain.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class DomainObjectConfiguration : IEntityTypeConfiguration<DomainObject>
{
    public void Configure(EntityTypeBuilder<DomainObject> b)
    {
        b.ToTable("domain_objects");
        b.HasKey(e => e.Id);
        b.Property(e => e.DisplayName).HasMaxLength(512);
        // Алиасы (issue #74) — Postgres text[]; сопоставление в памяти, индекс не нужен.
        b.Property(e => e.Aliases).HasColumnType("text[]").IsRequired();
        b.Property(e => e.CompositeTypeId).IsRequired();
        b.Property(e => e.Data).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.ScopeLevel).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(e => e.ScopeId);
        b.HasIndex(e => new { e.ScopeLevel, e.ScopeId });
        b.HasIndex(e => e.CompositeTypeId);

        // Passthrough-коллекция документной фасеты — НЕ навигация объекта (иначе EF заведёт лишний
        // теневой FK generated_files.DomainObjectId). Файлы висят на фасете (см. DocumentFacetConfiguration).
        b.Ignore(e => e.GeneratedFiles);

        // Документная фасета — 1:1 dependent (есть ⟺ объект-документ), каскад при удалении объекта.
        b.HasOne(e => e.Facet)
            .WithOne()
            .HasForeignKey<DocumentFacet>(f => f.ObjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class DocumentFacetConfiguration : IEntityTypeConfiguration<DocumentFacet>
{
    public void Configure(EntityTypeBuilder<DocumentFacet> b)
    {
        b.ToTable("document_facets");
        b.HasKey(e => e.ObjectId);
        b.Property(e => e.ObjectId).ValueGeneratedNever();
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.PluginData).HasColumnType("jsonb");
        b.Property(e => e.TemplateParams).HasColumnType("jsonb");
        b.Property(e => e.TemplateIds).HasColumnType("jsonb");
        b.HasMany(e => e.GeneratedFiles)
            .WithOne()
            .HasForeignKey(f => f.ObjectId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(e => e.GeneratedFiles)
            .HasField("Files")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
