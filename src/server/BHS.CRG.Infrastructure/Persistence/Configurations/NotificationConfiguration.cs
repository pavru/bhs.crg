using BHS.CRG.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("notifications");
        b.HasKey(e => e.Id);
        b.Property(e => e.Severity).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Title).IsRequired();
        b.Property(e => e.Message).IsRequired();
        b.Property(e => e.Source);
        b.Property(e => e.LinkUrl);
        b.Property(e => e.LinkLabel);
        // Код права или модуля — короткая строка; длина ограничена, чтобы в поле адресата не
        // оказался текст.
        b.Property(e => e.Audience).HasMaxLength(128);
        b.HasIndex(e => e.CreatedAt);
        b.HasIndex(e => e.UserId);
    }
}
