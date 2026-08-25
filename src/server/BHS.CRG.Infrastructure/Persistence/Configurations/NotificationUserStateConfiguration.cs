using BHS.CRG.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class NotificationUserStateConfiguration : IEntityTypeConfiguration<NotificationUserState>
{
    public void Configure(EntityTypeBuilder<NotificationUserState> b)
    {
        b.ToTable("notification_user_states");
        b.HasKey(e => e.Id);

        // Пара уникальна: upsert в сервисе опирается на ON CONFLICT по этому индексу.
        b.HasIndex(e => new { e.NotificationId, e.UserId }).IsUnique();

        // Каскады с обеих сторон: удалили уведомление (prune, «очистить» личных) или пользователя —
        // строки состояния уходят вместе с ним, иначе таблица копит мусор, невидимый ниоткуда.
        b.HasOne<Notification>()
            .WithMany()
            .HasForeignKey(e => e.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
