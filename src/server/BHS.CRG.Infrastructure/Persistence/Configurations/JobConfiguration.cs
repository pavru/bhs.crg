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

        // Индекс «одна активная задача на цель» (issue #900) объявлен НЕ здесь, а сырым SQL в
        // миграции SingleActiveJobPerTarget: его ключ — выражение (виды распознавания сведены в одно
        // семейство), а такие индексы конфигурация EF не описывает. Держать здесь урезанную версию
        // значило бы, что снапшот обещает одно, а в базе стоит другое.
    }
}
