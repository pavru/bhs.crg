using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Контекст модуля «Планирование»: схема <c>plan</c>, свой набор миграций, своя история (change
/// <c>plan-01-skeleton</c>, ТЗ CORE-4, D1 зонтика <c>plan-00-epic</c>).
///
/// <para>Первые таблицы — смета, её редакции, строки и ресурсы строк (change <c>plan-06-estimate-model</c>):
/// они приехали вместе с адресами, которые их читают, а не раньше них. Раскладка —
/// <see cref="EstimateModel" />.</para>
///
/// <para>Начальная миграция при этом не пустая по смыслу: она создаёт саму схему и историю миграций
/// модуля в ней — то, что включается на чистой и на рабочей базе и переживает выключение модуля.</para>
///
/// <para><b>Отказы базы — словами сметы, одним местом</b>: проигравшая одновременной запись, удаление
/// действующей редакции и сметы с редакциями, повтор номера редакции. Адресов, которые пишут смету,
/// несколько, и забыть перевод у одного из них значило бы ответить человеку 500 о ситуации, которая
/// ошибкой сервера не является.</para>
/// </summary>
public sealed class PlanDbContext(DbContextOptions<PlanDbContext> options) : ModuleDbContext(options)
{
    /// <summary>Имя схемы. То же, что объявлено модулем (<c>PlanModule.Schema</c>) — ядро сверяет.</summary>
    public const string SchemaName = "plan";

    /// <summary>Имя таблицы истории миграций — то же, что у EF по умолчанию; своё только место.</summary>
    public const string HistoryTableName = "__EFMigrationsHistory";

    /// <summary>Имя теневого свойства с версией строки — системной колонки <c>xmin</c>, как у счёта.</summary>
    internal const string RowVersion = "RowVersion";

    /// <summary>Внешний ключ «действующая редакция сметы» — по нему узнаётся отказ удалить действующую.</summary>
    internal const string CurrentRevisionKey = "fk_estimates_current_revision";

    /// <summary>Внешний ключ «редакция → смета» — по нему узнаётся отказ удалить смету с редакциями.</summary>
    internal const string RevisionEstimateKey = "fk_estimate_revisions_estimate";

    /// <summary>Единственность номера редакции в смете.</summary>
    internal const string RevisionNumberKey = "ix_estimate_revisions_estimate_number";

    protected override string Schema => SchemaName;

    public DbSet<Estimate> Estimates => Set<Estimate>();

    public DbSet<EstimateRevision> EstimateRevisions => Set<EstimateRevision>();

    /// <summary>
    /// Строки редакций. ⚠️ Строки для СУММ плана читаются только через <see cref="PlanRows.Current" />:
    /// план стройки — сумма действующих редакций, и прямое чтение отсюда удвоило бы объём дополнительной
    /// сметой или прежней редакцией. Стережёт <c>PlanRowsInventoryTests</c>.
    /// </summary>
    public DbSet<EstimateRow> EstimateRows => Set<EstimateRow>();

    public DbSet<EstimateRowMaterial> EstimateRowMaterials => Set<EstimateRowMaterial>();

    public DbSet<EstimateRowLabor> EstimateRowLabor => Set<EstimateRowLabor>();

    /// <summary>
    /// Настройка контекста — одна на всех, кто его создаёт: приложение (<c>PlanModule.RegisterServices</c>)
    /// и <c>dotnet ef</c> через <see cref="PlanDbContextFactory" />.
    ///
    /// <para>⚠️ История миграций названа ЯВНО, вместе со схемой. Без этого EF кладёт её в <c>public</c>,
    /// в одну таблицу с историей ядра, — и обновление ядра встретило бы там чужие миграции (подробно —
    /// у <c>CostsDbContext.Configure</c>).</para>
    /// </summary>
    public static void Configure(DbContextOptionsBuilder options, string? connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsHistoryTable(HistoryTableName, SchemaName));

    /// <summary>
    /// Версия сметы, как её знает контекст: после сохранения — новая. Отдаётся наружу полем
    /// <c>version</c> и возвращается правкой заголовком <c>If-Match</c>.
    /// </summary>
    public string VersionOf(Estimate estimate) => Version(estimate);

    /// <summary>Версия редакции — её называет удаление редакции.</summary>
    public string VersionOf(EstimateRevision revision) => Version(revision);

    private string Version(object entity) =>
        ((uint)Entry(entity).Property(RowVersion).CurrentValue!).ToString(CultureInfo.InvariantCulture);

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        }
        catch (DbUpdateException e) when (Refusal(e) is { } refusal)
        {
            throw refusal;
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateException e) when (Refusal(e) is { } refusal)
        {
            throw refusal;
        }
    }

    /// <summary>
    /// Отказ базы словами сметы; <c>null</c> — отказ не наш, пусть идёт как есть.
    ///
    /// <para>⚠️ Один внешний ключ отказывает с двух сторон: удаление того, на что ссылаются, и запись
    /// ссылки на то, чего уже нет. Слова выбираются по тому, ЧТО делала эта фиксация (удаляла ли она
    /// редакцию или смету), а не по одному имени ключа: иначе сделавший редакцию действующей прочитал бы
    /// «редакцию нельзя удалить», хотя ничего не удалял.</para>
    /// </summary>
    private ConflictException? Refusal(DbUpdateException e)
    {
        if (e is DbUpdateConcurrencyException)
            return new ConflictException(
                "Смету тем временем изменили, и эта правка не записана — целиком, ни одной частью. Перечитайте " +
                "смету и повторите: записанная поверх, правка затёрла бы чужую.", e);

        bool Deleting<T>() where T : class => ChangeTracker.Entries<T>().Any(x => x.State == EntityState.Deleted);

        return (e.InnerException as PostgresException)?.ConstraintName switch
        {
            CurrentRevisionKey when Deleting<EstimateRevision>() => new ConflictException(
                "Редакцию нельзя удалить: она действующая — по ней считается план стройки. Сначала сделайте " +
                "действующей другую редакцию.", e),
            CurrentRevisionKey => new ConflictException(
                "Редакцию тем временем удалили, и действующей она не стала. Перечитайте смету и выберите " +
                "действующую из оставшихся.", e),
            RevisionEstimateKey when Deleting<Estimate>() => new ConflictException(
                "Смету нельзя удалить: у неё есть редакции. Удаляется только смета без редакций.", e),
            RevisionEstimateKey => new ConflictException(
                "Смету тем временем удалили, и редакция в неё не записана. Заведите смету заново и повторите.", e),
            RevisionNumberKey => new ConflictException(
                "В смету одновременно записывалась другая редакция, и эта не записана. Повторите — она получит " +
                "следующий номер.", e),
            _ => null,
        };
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        EstimateModel.Configure(builder);
    }
}
