using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
        // Владелец типа (ТЗ CORE-18): колонка, а не поле схемы, потому что на него опирается код —
        // по нему отбираются типы включённых модулей (CORE-15).
        b.Property(e => e.Module).HasMaxLength(32).IsRequired();
        b.HasIndex(e => e.Module);

        // ⚠️ Ни у Storage, ни у Visibility НЕТ значения по умолчанию на уровне базы — нарочно.
        // Первое значение каждого перечисления (SharedObject, Closed) совпадает с нулём CLR, и EF
        // счёл бы его «не задано»: тип, заведённый закрытым, сохранился бы общим. Существующим
        // строкам значения проставляет миграция, новым — код, который их создаёт.
        b.Property(e => e.Storage).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(e => e.Visibility).HasConversion<string>().HasMaxLength(32).IsRequired();

        // Список каналов — строкой через запятую: коды каналов объявлены (TypeReadChannels), запятых
        // в них нет, а jsonb здесь дал бы массив ради трёх слов.
        b.Property(e => e.ReadChannels)
         .HasConversion(
             v => string.Join(',', v),
             s => s.Length == 0 ? new List<string>() : s.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
             new ValueComparer<IReadOnlyList<string>>(
                 (a, c) => a!.SequenceEqual(c!),
                 v => v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s.GetHashCode())),
                 v => v.ToList()))
         .HasMaxLength(512)
         .IsRequired();

        b.Property(e => e.Schema).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.PluginBindings).HasColumnType("jsonb");
        b.Property(e => e.Group).HasMaxLength(256);
    }
}
