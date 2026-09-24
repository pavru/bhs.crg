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

        // Пояс и внешний идентификатор — колонки стройки (ТЗ CORE-5), а не поля её профиля: по
        // поясу код считает сутки, а по внешнему коду стройку находит загрузка извне.
        b.Property(e => e.TimeZoneId).HasMaxLength(128);
        b.Property(e => e.ExternalSystem).HasMaxLength(128);
        b.Property(e => e.ExternalCode).HasMaxLength(256);

        // Пара уникальна — и только там, где она задана: строек без внешнего кода сколько угодно,
        // а вот две стройки с одним кодом одной системы означали бы, что следующая загрузка извне
        // выберет любую из них. Индекс частичный, потому что NULL в PostgreSQL уникальности не
        // нарушает, но полагаться на это молча не стоит: условие записано явно.
        b.HasIndex(e => new { e.ExternalSystem, e.ExternalCode })
            .IsUnique()
            .HasFilter("\"ExternalSystem\" IS NOT NULL AND \"ExternalCode\" IS NOT NULL");
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
