using System.Globalization;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Work.Crew;
using BHS.CRG.Modules.Work.Devices;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Контекст модуля «Учёт работ»: схема <c>work</c>, свой набор миграций, своя история (change
/// <c>work-10-skeleton</c>, ТЗ CORE-4, D1 зонтика <c>work-00-epic</c>).
///
/// <para>Таблицы приезжают со своими адресами, а не раньше потребителя (таблица, заведённая раньше,
/// проверяется только тем, что она создалась): назначения — <c>work-11</c>, отчёт и его строки —
/// <c>work-13</c> (его первый PR приносит таблицы вместе с путём записи <c>ReportWriter</c> и тестами на нём, адреса
/// — второй).</para>
///
/// <para>Начальная миграция при этом не пустая по смыслу: она создаёт саму схему и историю миграций
/// модуля в ней — то, что включается на чистой и на рабочей базе и переживает выключение модуля.</para>
///
/// <para>Запись, проигравшая одновременной, отвечает 409 без кода здесь: исключение EF переводит общий
/// обработчик API (<c>ApiErrorMapping</c>) для любого контекста. Таблица, которой нужны свои слова
/// («отчёт изменили…»), переведёт его в <c>ConflictException</c> сама — как счёт у <c>CostsDbContext</c>.
/// Версия строки — системная колонка <c>xmin</c> (<c>IsRowVersion()</c>), как у счёта и сметы.</para>
/// </summary>
public sealed class WorkDbContext(DbContextOptions<WorkDbContext> options) : ModuleDbContext(options)
{
    /// <summary>Имя схемы. То же, что объявлено модулем (<c>WorkModule.Schema</c>) — ядро сверяет.</summary>
    public const string SchemaName = "work";

    /// <summary>Имя таблицы истории миграций — то же, что у EF по умолчанию; своё только место.</summary>
    public const string HistoryTableName = "__EFMigrationsHistory";

    /// <summary>Имя теневого свойства с версией строки — системной колонки <c>xmin</c>, как у счёта.</summary>
    internal const string RowVersion = "RowVersion";

    protected override string Schema => SchemaName;

    /// <summary>Назначения сотрудников на стройки (<c>work-11</c>, ТЗ WORK-21, WORK-46).</summary>
    public DbSet<Assignment> Assignments => Set<Assignment>();

    internal DbSet<DeviceInvitation> DeviceInvitations => Set<DeviceInvitation>();
    internal DbSet<DeviceSession> DeviceSessions => Set<DeviceSession>();

    /// <summary>
    /// Серверная последовательность изменений отчётов (ТЗ MOB-16, change <c>work-13</c>): номер получает каждое
    /// изменение любого отчёта, и курсор <c>since</c> отдаёт их по нему. Время устройства порядок не задаёт.
    /// </summary>
    public const string ReportSequence = "report_seq";

    /// <summary>
    /// Следующий номер серверной последовательности изменений отчётов сотрудника (ТЗ MOB-16) — под замком его отчётов
    /// до конца транзакции. Часы устройства на порядок не влияют.
    ///
    /// <para><b>Зачем замок.</b> Номер выдаётся в начале транзакции, а курсор <c>since</c> видит только записанное. Две
    /// записи одного сотрудника разом (телефон и ввод менеджера) без замка могли бы записаться в обратном порядке:
    /// клиент получил бы номер 11, сдвинул курсор, и запись с номером 10, записанная позже, в курсор уже не попала бы.
    /// Под замком номера одного сотрудника выдаются и записываются по очереди, а курсор — по сотруднику.</para>
    /// </summary>
    public async Task<long> NextReportSeqAsync(Guid employeeId, CancellationToken ct)
    {
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Номер изменения отчёта выдаётся только внутри транзакции: вне её замок снимается тем же запросом.");
        await Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ReportLockKey(employeeId)}, 0))", ct);
        return await Database.SqlQueryRaw<long>("SELECT nextval('" + SchemaName + "." + ReportSequence + "') AS \"Value\"")
            .SingleAsync(ct);
    }

    /// <summary>
    /// Запереть отчёты сотрудника до конца транзакции, не беря номера (<c>work-17</c>): доставка файла объявленного фото
    /// отчёт не меняет, но обязана не разминуться с параллельной загрузкой того же фото и с правкой строки. Замок тот же,
    /// что у <see cref="NextReportSeqAsync" />, и повторный его захват в той же транзакции не ждёт.
    /// </summary>
    public async Task LockReportsAsync(Guid employeeId, CancellationToken ct)
    {
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Отчёты сотрудника запираются только внутри транзакции: вне её замок снимается тем же запросом.");
        await Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({ReportLockKey(employeeId)}, 0))", ct);
    }

    /// <summary>Ключ замка отчётов сотрудника — им же пользуется тест очерёдности.</summary>
    public static string ReportLockKey(Guid employeeId) => $"work.reports:{employeeId}";

    // Всегда перед замком сотрудника: утверждение записывает журнал сразу нескольких сотрудников.
    internal Task LockCrewShiftAsync(Guid site, DateOnly date, ReportShift shift, CancellationToken ct)
    {
        var key = FormattableString.Invariant($"work.crew:{site}:{date:yyyy-MM-dd}:{shift}");
        return Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }

    /// <summary>
    /// Версия строки назначения — поле <c>version</c> ответа и значение <c>If-Match</c>. ⚠️ Только у
    /// отслеживаемой строки: теневое свойство у прочитанной без отслеживания не заполняется — была бы «0».
    /// </summary>
    public string VersionOf(Assignment assignment) => TrackedVersion(assignment, "назначения");

    /// <summary>Версия отчёта — то же правило, что у назначения: только у отслеживаемой строки.</summary>
    public string VersionOf(Report report) => TrackedVersion(report, "отчёта");

    internal string VersionOf(DeviceSession session) => TrackedVersion(session, "устройства");

    private string TrackedVersion(object entity, string what)
    {
        var entry = Entry(entity);
        if (entry.State == EntityState.Detached)
            throw new InvalidOperationException(
                $"Версия {what} читается только у отслеживаемой строки: у прочитанной без отслеживания её нет.");
        return ((uint)entry.Property(RowVersion).CurrentValue!).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Взять замок шага сводки о поданных до конца транзакции (<c>work-16</c>), не дожидаясь: <c>false</c> — шаг уже идёт в
    /// другом прогоне, и второй уходит ни с чем, а не шлёт вторую сводку. Вне транзакции — отказ, как у прочих замков.
    /// </summary>
    public async Task<bool> TryLockReviewDigestAsync(CancellationToken ct)
    {
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Замок сводки о поданных берётся только внутри транзакции: вне её он снимается тем же запросом.");
        return await Database.SqlQueryRaw<bool>(
                "SELECT pg_try_advisory_xact_lock(hashtextextended('work.review-digest', 0)) AS \"Value\"")
            .SingleAsync(ct);
    }

    /// <summary>
    /// Запереть назначения стройки до конца транзакции (design <c>work-11</c>): проверка пересечений и
    /// «последнего менеджера» читает назначения стройки, а замок строк не удержал бы вставку, когда строк
    /// ещё нет. Вне транзакции — отказ: замок снялся бы тем же запросом и выглядел бы защитой.
    /// </summary>
    public async Task LockSiteAsync(Guid constructionId, CancellationToken ct)
    {
        if (Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "Назначения стройки запираются только внутри транзакции: вне её замок снимается тем же запросом.");
        var key = $"work.assignments:{constructionId}";
        await Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        var assignment = builder.Entity<Assignment>();
        assignment.ToTable("assignments");
        assignment.HasKey(a => a.Id);
        assignment.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        assignment.Property(a => a.EmployeeId).HasColumnName("employee_id");
        assignment.Property(a => a.ConstructionId).HasColumnName("construction_id");
        assignment.Property(a => a.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        assignment.Property(a => a.DateFrom).HasColumnName("date_from");
        assignment.Property(a => a.DateTo).HasColumnName("date_to");
        assignment.Property(a => a.CreatedAt).HasColumnName("created_at");
        assignment.Property(a => a.UpdatedAt).HasColumnName("updated_at");
        assignment.Property<uint>(RowVersion).IsRowVersion();

        // Бессрочный монтажник подавал бы отчёты на стройку, с которой ушёл; конец раньше начала — период,
        // в котором нет ни дня. Проверяет это сущность; ограничения — на запись мимо неё.
        assignment.ToTable(t =>
        {
            t.HasCheckConstraint("ck_assignments_installer_end", "kind = 'Manager' OR date_to IS NOT NULL");
            t.HasCheckConstraint("ck_assignments_period", "date_to IS NULL OR date_to >= date_from");
        });

        // Назначения стройки по виду и дате — экран стройки, «последний менеджер», охват менеджера.
        assignment.HasIndex(a => new { a.ConstructionId, a.Kind, a.DateFrom }).HasDatabaseName("ix_assignments_site");
        // Назначения сотрудника — «мои стройки», пересечения и вопрос ядра «кто держит сотрудника».
        assignment.HasIndex(a => new { a.EmployeeId, a.ConstructionId }).HasDatabaseName("ix_assignments_employee");

        WorkReportModel.Configure(builder, RowVersion);
        ReportVersion.Configure(builder);
        CrewGroupRecord.Configure(builder);
        DeviceModel.Configure(builder);
    }

    /// <summary>
    /// Настройка контекста — одна на всех, кто его создаёт: приложение (<c>WorkModule.RegisterServices</c>)
    /// и <c>dotnet ef</c> через <see cref="WorkDbContextFactory" />.
    ///
    /// <para>⚠️ История миграций названа ЯВНО, вместе со схемой. Без этого EF кладёт её в <c>public</c>,
    /// в одну таблицу с историей ядра, — и обновление ядра встретило бы там чужие миграции (подробно —
    /// у <c>CostsDbContext.Configure</c>).</para>
    /// </summary>
    public static void Configure(DbContextOptionsBuilder options, string? connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsHistoryTable(HistoryTableName, SchemaName));
}
