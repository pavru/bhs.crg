using BHS.CRG.Domain.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> b)
    {
        b.ToTable("jobs");
        b.HasKey(e => e.Id);
        b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(32);
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Title).IsRequired();
        b.Property(e => e.Payload);
        b.Property(e => e.Progress);
        b.Property(e => e.Error);
        // Индикатор запрашивает «мои активные» — индекс по владельцу и статусу.
        b.HasIndex(e => new { e.UserId, e.Status });

        // Одна активная задача на цель — правилом БАЗЫ, а не только проверкой перед постановкой
        // (issue #900). Проверка и вставка — два шага, и между ними есть окно: два запроса, пришедшие
        // одновременно, оба увидят «свободно» и поставят по задаче — ровно та двойная запись, ради
        // которой защита и заведена. У человека окно почти недостижимо (между нажатиями сотни
        // миллисекунд, кнопка заблокирована), у внешнего агента вызов в цикле — обычное дело.
        //
        // Deny-list, а не allow-list: умолчание — «одна задача на цель», и новый вид получает защиту
        // сам. Исключение одно и названо: отправка комплекта почтой. Её дубль безвреден и законен —
        // тот же комплект отправляют разным получателям двумя действиями подряд.
        //
        // Фильтр — сырой SQL по именам КОЛОНОК: Kind и Status хранятся строками (HasConversion выше),
        // поэтому и сравнение строковое.
        b.HasIndex(e => new { e.TargetId, e.Kind })
            .IsUnique()
            .HasFilter("\"Status\" IN ('Queued', 'Running') AND \"Kind\" <> 'SendEmail'")
            .HasDatabaseName("ix_jobs_single_active_per_target");
    }
}
