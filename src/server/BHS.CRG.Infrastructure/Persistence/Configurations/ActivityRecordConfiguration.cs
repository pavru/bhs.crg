using BHS.CRG.Domain.Activity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class ActivityRecordConfiguration : IEntityTypeConfiguration<ActivityRecord>
{
    public void Configure(EntityTypeBuilder<ActivityRecord> b)
    {
        b.ToTable("activity_log");
        b.HasKey(e => e.Id);

        b.Property(e => e.Action).IsRequired().HasMaxLength(128);
        // Имя автора — снимок, а не ссылка: внешнего ключа на учётную запись здесь нет нарочно,
        // иначе удаление пользователя либо унесло бы его след из журнала, либо запретило бы
        // удаление вовсе (см. ActivityRecord).
        //
        // Ширины взяты из констант сущности, а не набраны числом: обрезка перед вставкой живёт там
        // же (ActivityRecord.Create), и разъехавшись с колонкой она дала бы отказ 22001 уже после
        // совершённого действия — issue #980.
        b.Property(e => e.ActorName).IsRequired().HasMaxLength(ActivityRecord.ActorNameMax);
        b.Property(e => e.TargetId).HasMaxLength(128);
        b.Property(e => e.TargetLabel).HasMaxLength(ActivityRecord.TargetLabelMax);

        // Прежнее и новое значение длины не ограничены: у смены роли это два слова, у правки схемы —
        // перечень полей. Обрезать перечень значило бы записать «изменено 7 полей: …» и потерять
        // ровно тот хвост, ради которого в журнал и заглядывают.
        b.Property(e => e.Before);
        b.Property(e => e.After);

        // Читают журнал всегда одинаково: свежие сверху, иногда с фильтром по действию.
        b.HasIndex(e => e.OccurredAt);
        b.HasIndex(e => e.Action);
    }
}
