using BHS.CRG.Domain.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    /// <summary>У шаблона — типа и названия — не больше одной действующей версии (D3).</summary>
    public const string OneActiveIndex = "UX_templates_one_active";

    /// <summary>Номер версии внутри шаблона не повторяется (D3).</summary>
    public const string VersionIndex = "UX_templates_version";

    public void Configure(EntityTypeBuilder<Template> b)
    {
        b.ToTable("templates");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(Template.NameMaxLength).IsRequired();
        b.Property(e => e.Content).IsRequired();
        b.Property(e => e.Parameters).HasColumnType("jsonb");
        b.Property(e => e.Comment).HasMaxLength(1000);
        b.Property(e => e.Version).IsRequired();
        b.Property(e => e.IsActive).IsRequired();
        b.Property(e => e.IsDefault).IsRequired();
        b.HasIndex(e => new { e.DocumentTypeId, e.IsActive });
        b.HasIndex(e => new { e.DocumentTypeId, e.IsDefault });

        // Целостность версий охраняет база, а не только обработчик (id-template-version-integrity, D3):
        // проигравший гонку получает отказ, а не вторую действующую или второй номер. Индексы охраняют
        // и пути мимо обработчиков — восстановление копии, переход на Код.Имя, засев модулей.
        // ⚠️ Частичный индекс проверяется на каждом операторе: погашение пишется раньше вставки (D4,
        // ITemplateVersionWriter).
        b.HasIndex(e => new { e.DocumentTypeId, e.Name })
            .IsUnique()
            .HasFilter("\"IsActive\"")
            .HasDatabaseName(OneActiveIndex);
        b.HasIndex(e => new { e.DocumentTypeId, e.Name, e.Version })
            .IsUnique()
            .HasDatabaseName(VersionIndex);

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
