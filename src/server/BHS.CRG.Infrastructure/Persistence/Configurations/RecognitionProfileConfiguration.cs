using BHS.CRG.Domain.Recognition;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class RecognitionProfileConfiguration : IEntityTypeConfiguration<RecognitionProfile>
{
    public void Configure(EntityTypeBuilder<RecognitionProfile> b)
    {
        b.ToTable("recognition_profiles");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(256).IsRequired();
        // Код есть только у встроенных профилей — уникальный индекс с фильтром, чтобы множество
        // пользовательских профилей с NULL не конфликтовало между собой.
        b.Property(e => e.Code).HasMaxLength(64);
        b.HasIndex(e => e.Code).IsUnique().HasFilter("\"Code\" IS NOT NULL");
        // Enum'ы храним строками (конвенция проекта — см. архитектурный отчёт, пункт 1).
        b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(e => e.Fields).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.RowColumns).HasColumnType("jsonb");
        b.Property(e => e.Shape).HasColumnType("jsonb");
        b.Property(e => e.IsBuiltIn).IsRequired();
        b.Property(e => e.IsModified).IsRequired();
        b.Property(e => e.BuiltInHash).HasMaxLength(64);
        b.Property(e => e.BuiltInOutdated).IsRequired();
        b.HasIndex(e => e.Kind);
    }
}
