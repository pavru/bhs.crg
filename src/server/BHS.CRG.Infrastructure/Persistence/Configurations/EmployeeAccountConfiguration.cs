using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class EmployeeAccountConfiguration : IEntityTypeConfiguration<EmployeeAccount>
{
    public const int AccountNameMax = 256;

    public void Configure(EntityTypeBuilder<EmployeeAccount> b)
    {
        b.ToTable("employee_accounts");
        b.HasKey(e => e.EmployeeId);
        b.Property(e => e.EmployeeId).ValueGeneratedNever();
        b.Property(e => e.AccountName).IsRequired().HasMaxLength(AccountNameMax);
        b.Ignore(e => e.IsLive);

        // ⚠️ Уникальность учётной записи держит БАЗА, а не проверка в службе (ТЗ CORE-15): два
        // связывания одной записи, пришедшие почти разом, оба прошли бы проверку «свободна ли». Индекс
        // частичный — строк «учётная запись удалена» (UserId = null) может быть сколько угодно.
        // Сторож — EmployeeAccountLinkTests: снимите индекс, и «вторая связь» краснеет.
        b.HasIndex(e => e.UserId).IsUnique().HasFilter("\"UserId\" IS NOT NULL");

        // SetNull, а не каскад: связь переживает удаление учётной записи и становится «удалена» —
        // сотрудник и его отчёты остаются, а экран называет, что именно пропало.
        b.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Каскад по сотруднику: связь — свойство карточки и без неё не живёт. Удаление карточки идёт
        // своими проверками (кто её держит); запрет здесь сделал бы из удаления связанного сотрудника
        // отказ базы — 500 на любом из путей удаления записи вместо слов.
        b.HasOne<DomainObject>()
            .WithMany()
            .HasForeignKey(e => e.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
