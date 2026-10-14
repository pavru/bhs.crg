using BHS.CRG.Domain.Tables;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class SavedTableViewConfiguration : IEntityTypeConfiguration<SavedTableView>
{
    public void Configure(EntityTypeBuilder<SavedTableView> b)
    {
        b.ToTable("table_views");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();

        b.Property(e => e.TableAddress).IsRequired().HasMaxLength(SavedTableView.AddressMax);
        b.Property(e => e.BasedOn).HasMaxLength(SavedTableView.AddressMax);
        b.Property(e => e.Title).IsRequired().HasMaxLength(SavedTableView.TitleMax);
        b.Property(e => e.State).IsRequired().HasColumnType("jsonb");

        // Версия строки — системная колонка xmin: колонки в таблице не прибавляется. Здесь она —
        // токен конкурентности: каждая запись этой строки — правка человека, и гонку двух правок
        // обязана ловить сама база, а не только сверка «прочёл — сравнил» в службе.
        b.Property(e => e.RowVersion).IsRowVersion();

        // Два «Мои счета» в одном списке неразличимы — имя уникально в своей группе: у общих — на
        // таблицу, у личных — на таблицу и автора. Индексы частичные: личное и общее с одним именем
        // живут рядом, это разные списки на экране.
        b.HasIndex(e => new { e.TableAddress, e.Title })
            .IsUnique().HasFilter("\"Shared\"").HasDatabaseName("IX_table_views_shared_title");
        b.HasIndex(e => new { e.TableAddress, e.OwnerId, e.Title })
            .IsUnique().HasFilter("NOT \"Shared\"").HasDatabaseName("IX_table_views_own_title");

        // SetNull, а не каскад: общее представление — настройка компании и автора переживает.
        // Личные каскадом не убрать (ключ один на оба вида) — их удаляет адрес удаления пользователя,
        // и забытое там оставило бы строку без автора, которую не видит никто; каскад же при той же
        // забывчивости унёс бы общие представления у всех.
        b.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(e => e.OwnerId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
