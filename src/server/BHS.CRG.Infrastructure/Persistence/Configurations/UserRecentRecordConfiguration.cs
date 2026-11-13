using BHS.CRG.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class UserRecentRecordConfiguration : IEntityTypeConfiguration<UserRecentRecord>
{
    public void Configure(EntityTypeBuilder<UserRecentRecord> b)
    {
        b.ToTable("user_recent_records");

        // Ключ — сама запись у пользователя: повторное открытие поднимает строку, а не добавляет вторую.
        // Пишется только командами «вставить или поднять» (BHS.CRG.Api/Endpoints/Account/RecentRecords:
        // отметка открытия и уход версии шаблона) — ключ и есть то, обо что они упираются. Отслеживаемой
        // вставкой сюда не пишут: вставка, параллельная открытию, упала бы на этом ключе.
        b.HasKey(e => new { e.UserId, e.RecordType, e.RecordId });

        b.Property(e => e.RecordType).IsRequired().HasMaxLength(64);

        // Чтение и вытеснение идут по открытиям одного пользователя, новые первыми.
        b.HasIndex(e => new { e.UserId, e.OpenedAt });

        // Каскад, как у личных настроек: удалили учётную запись — её недавние уходят с ней.
        b.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
