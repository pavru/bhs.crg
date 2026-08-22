using BHS.CRG.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class ServiceStateConfiguration : IEntityTypeConfiguration<ServiceStateEntity>
{
    public void Configure(EntityTypeBuilder<ServiceStateEntity> b)
    {
        b.ToTable("service_state");
        b.HasKey(e => e.Id);
        // Ключ уникален: одна строка на службу. Без индекса две параллельные записи создали бы
        // вторую строку, и след службы раздвоился бы молча.
        b.HasIndex(e => e.Key).IsUnique();
        b.Property(e => e.Key).HasMaxLength(64).IsRequired();
        b.Property(e => e.Data).HasColumnType("jsonb").IsRequired();
    }
}
