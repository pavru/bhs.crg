using BHS.CRG.Domain.Periods;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class PeriodClosureConfiguration : IEntityTypeConfiguration<PeriodClosure>
{
    public void Configure(EntityTypeBuilder<PeriodClosure> b)
    {
        b.ToTable("period_closures");
        b.HasKey(e => e.Id);
        b.Ignore(e => e.ContourRef);

        // Вид записи и контур — словами: таблицу читают и глазами, а «1» в колонке через год
        // означает то, что помнит читающий.
        b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(e => e.Contour).HasConversion<string>().HasMaxLength(16).IsRequired();

        // Внешнего ключа на стройку НЕТ нарочно: строки неудаляемы, и ключ сделал бы неудаляемой
        // всякую стройку со своим закрытием (см. PeriodClosure). По той же причине нет ключа на
        // отменённую запись — она в той же таблице и исчезнуть не может.
        b.Property(e => e.ConstructionId);
        b.Property(e => e.CancelsId);

        b.Property(e => e.ByName).IsRequired().HasMaxLength(PeriodClosure.ByNameMax);
        b.Property(e => e.Reason).HasMaxLength(PeriodClosure.ReasonMax);

        // Уникального индекса «одно закрытие на границу» нет: гонку двух закрывающих решает замок
        // службы и сверка увиденной границы, а у контура «компания» индекс и не ловил бы ничего —
        // NULL в колонке стройки для него различимы.
        b.HasIndex(e => e.At);
    }
}
