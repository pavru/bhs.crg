using BHS.CRG.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class UserSettingConfiguration : IEntityTypeConfiguration<UserSetting>
{
    public void Configure(EntityTypeBuilder<UserSetting> b)
    {
        b.ToTable("user_settings");

        // Составной ключ, а не суррогатный Id: «одна настройка у одного пользователя» — это и есть
        // ограничение, ради которого таблица заведена. Суррогатный ключ пришлось бы страховать
        // уникальным индексом по той же паре, то есть записать то же правило дважды.
        b.HasKey(e => new { e.UserId, e.Key });

        b.Property(e => e.Key).IsRequired().HasMaxLength(64);

        // Длину значения ограничивает КАТАЛОГ (UserSettingKeys), у каждого ключа своя: тема — три
        // слова, пространства — JSON. Колонка общая, и предел у неё один на всех — он страхует от
        // гигантской строки, а не заменяет проверку ключа.
        b.Property(e => e.Value).IsRequired().HasMaxLength(16_384);

        // Каскад: удалили учётную запись — её предпочтения уходят вместе с ней. Иначе таблица
        // копит строки, на которые никто уже не может посмотреть (тот же довод, что у
        // notification_user_states).
        b.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
