using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class DocumentSetPlanConfiguration : IEntityTypeConfiguration<DocumentSetPlanItem>
{
    public void Configure(EntityTypeBuilder<DocumentSetPlanItem> b)
    {
        b.ToTable("document_set_plans");
        b.HasKey(e => e.Id);

        // Строка на тип: замена плана целиком и так не даёт дублей, но опираться на дисциплину
        // вызывающего в вопросе «сколько документов должно быть» — значит однажды получить план,
        // где один тип посчитан дважды, и процент, который никто не сойдётся объяснить.
        b.HasIndex(e => new { e.DocumentSetId, e.DocumentTypeId }).IsUnique();

        b.HasOne<DocumentSet>()
            .WithMany()
            .HasForeignKey(e => e.DocumentSetId)
            .OnDelete(DeleteBehavior.Cascade);

        // К типу документа внешнего ключа НЕТ, и это осознанно: удаление типа отдельно защищено
        // проверкой занятости (она теперь видит и планы), а каскад отсюда тихо унёс бы строки
        // плана вместе с типом — план перестал бы сходиться, и никто бы не понял почему.
        b.Property(e => e.DocumentTypeId).IsRequired();
        b.HasIndex(e => e.DocumentTypeId);
    }
}
