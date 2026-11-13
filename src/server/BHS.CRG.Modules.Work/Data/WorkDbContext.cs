using System.Globalization;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Контекст модуля «Учёт работ»: схема <c>work</c>, свой набор миграций, своя история (change
/// <c>work-10-skeleton</c>, ТЗ CORE-4, D1 зонтика <c>work-00-epic</c>).
///
/// <para>Таблицы приезжают со своими адресами, а не раньше потребителя (таблица, заведённая раньше,
/// проверяется только тем, что она создалась): назначения — <c>work-11</c>, отчёт и его строки —
/// <c>work-13</c>.</para>
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

    /// <summary>
    /// Версия строки назначения — поле <c>version</c> ответа и значение <c>If-Match</c>. ⚠️ Только у
    /// отслеживаемой строки: теневое свойство у прочитанной без отслеживания не заполняется — была бы «0».
    /// </summary>
    public string VersionOf(Assignment assignment)
    {
        var entry = Entry(assignment);
        if (entry.State == EntityState.Detached)
            throw new InvalidOperationException(
                "Версия назначения читается только у отслеживаемой строки: у прочитанной без отслеживания её нет.");
        return ((uint)entry.Property(RowVersion).CurrentValue!).ToString(CultureInfo.InvariantCulture);
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
