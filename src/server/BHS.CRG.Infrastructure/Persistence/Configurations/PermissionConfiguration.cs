using BHS.CRG.Domain.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> b)
    {
        b.ToTable("permissions");
        b.HasKey(e => e.Id);

        b.Property(e => e.Code).HasMaxLength(128).IsRequired();
        b.Property(e => e.Gives).HasMaxLength(512).IsRequired();
        b.Property(e => e.Opens).HasMaxLength(512).IsRequired();

        // Уникальность кода — не украшение: сверка с кодом идёт «найди по коду и обнови», и второй
        // строкой с тем же кодом одна из них навсегда осталась бы непрочитанной.
        b.HasIndex(e => e.Code).IsUnique();

        // Список подсказок хранится массивом: он короткий, ссылочной целостности не несёт и нужен
        // целиком. Отдельная таблица здесь дала бы соединение ради строки текста в редакторе ролей.
        b.PrimitiveCollection(e => e.UsuallyWith);
    }
}
