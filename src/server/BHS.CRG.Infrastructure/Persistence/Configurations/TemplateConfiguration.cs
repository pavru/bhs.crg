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
        b.Property(e => e.Comment).HasMaxLength(1000);
        b.Property(e => e.Version).IsRequired();
        b.Property(e => e.IsActive).IsRequired();
        b.Property(e => e.IsDefault).IsRequired();
        b.HasIndex(e => new { e.DocumentTypeId, e.IsActive });
        b.HasIndex(e => new { e.DocumentTypeId, e.IsDefault });

        // Внешний ключ на тип документа (issue #833). Его не было, и шаблоны переживали свой тип:
        // на рабочей базе таких сирот семь. Увидеть их в интерфейсе нельзя (список шаблонов идёт
        // от типа), а в резервную копию они попадали и при восстановлении отбрасывались с
        // предупреждением — то есть обнаруживались после аварии.
        //
        // Restrict, а не Cascade: удаление типа с шаблонами приложение и так запрещает
        // (DeleteDocumentTypeCommand: «Нельзя удалить тип — используется»). Каскад тихо удалял бы
        // шаблоны в обход этого запрета, если такой путь однажды появится; отказ базы — заметен.
        b.HasOne<BHS.CRG.Domain.Documents.DocumentType>()
            .WithMany()
            .HasForeignKey(e => e.DocumentTypeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
