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
    }
}
