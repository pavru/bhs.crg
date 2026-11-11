namespace BHS.CRG.Domain.Catalog;

/// <summary>
/// Связь «учётная запись → сотрудник» (ТЗ WORK-47, CORE-7, CORE-15; change <c>work-01-employee-account</c>):
/// строго один к одному — ключ по сотруднику, уникальность по учётной записи.
///
/// <para><b>Таблица, а не поле карточки.</b> В JSON карточки второй сотрудник с той же учётной записью
/// прошёл бы молча, а на этой связи стоят «свои» отчёты монтажника. Поле <c>УчётнаяЗапись</c> схемы
/// «Сотрудника» остаётся запертым и связью не заполняется: зеркало дало бы два источника, которые
/// разойдутся при первом же восстановлении копии.</para>
///
/// <para><b>Строка переживает удаление учётной записи.</b> <see cref="UserId" /> тогда становится
/// <c>null</c> (внешний ключ <c>SetNull</c>), а <see cref="AccountName" /> остаётся: так экран говорит
/// «учётная запись удалена: имя», а не делает вид, что связи не было. Правило держит внешний ключ, и
/// забыть его в коде удаления пользователя негде.</para>
/// </summary>
public class EmployeeAccount
{
    // ReSharper disable once UnusedMember.Local — конструктор для EF.
    private EmployeeAccount() { }

    /// <summary>Сотрудник — объект типа «Сотрудник». Ключ: у сотрудника не больше одной связи.</summary>
    public Guid EmployeeId { get; private set; }

    /// <summary>Учётная запись. <c>null</c> — её удалили, связь в состоянии «удалена».</summary>
    public Guid? UserId { get; private set; }

    /// <summary>
    /// Имя учётной записи снимком на момент связывания. Живое имя действующей связи берётся из самой
    /// учётной записи; снимок нужен состоянию «удалена», когда спросить больше не у кого.
    /// </summary>
    public string AccountName { get; private set; } = "";

    /// <summary>Когда связали.</summary>
    public DateTimeOffset LinkedAt { get; private set; }

    /// <summary>Связь действует: учётная запись на месте.</summary>
    public bool IsLive => UserId is not null;

    public static EmployeeAccount Link(Guid employeeId, Guid userId, string accountName) => new()
    {
        EmployeeId = employeeId, UserId = userId, AccountName = accountName, LinkedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Связать заново поверх связи с УДАЛЁННОЙ учётной записью. Действующую связь так не заменить:
    /// сначала «отвязать» — это и есть «строго один к одному».
    /// </summary>
    public void Relink(Guid userId, string accountName)
    {
        if (IsLive)
            throw new Common.ConflictException(
                "Сотрудник уже связан с действующей учётной записью. Сначала отвяжите её.");
        UserId = userId;
        AccountName = accountName;
        LinkedAt = DateTimeOffset.UtcNow;
    }
}
