using BHS.CRG.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> b)
    {
        b.ToTable("app_settings");

        // Ключ и есть первичный ключ: «одна настройка экземпляра» — это и есть ограничение, ради
        // которого таблица заведена (тот же довод, что у user_settings).
        b.HasKey(e => e.Key);
        b.Property(e => e.Key).HasMaxLength(64);
        b.Property(e => e.Value).IsRequired().HasMaxLength(16_384);
    }
}
