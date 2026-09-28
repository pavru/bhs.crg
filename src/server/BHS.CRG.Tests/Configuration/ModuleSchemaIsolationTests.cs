using BHS.CRG.Api.Modules;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Модуль не выходит из своей схемы: ни таблицей, ни внешним ключом, ни историей миграций (задача
/// A2a этапа 2, issue #1072, ТЗ CORE-4).
///
/// <para>Сторожа ломаются здесь на ПОДДЕЛЬНЫХ контекстах, и это единственный способ их сломать:
/// настоящий контекст модуля сегодня нечем нарушить — таблиц в нём ещё нет, — а проверка, которая ни
/// на чём не падает, ничего не утверждает. Базы для этого не нужно: всё решается по модели, до первого
/// соединения.</para>
/// </summary>
public class ModuleSchemaIsolationTests
{
    private static readonly ModuleSchema Declared = new("probe", typeof(StrayTableContext));

    /// <summary>
    /// Таблица, прибитая к схеме ядра, — отказ, называющий таблицу.
    ///
    /// Самая вероятная правка, которая это делает, — копирование настройки из ядра вместе с указанием
    /// схемы. Тихо это значило бы таблицу модуля в резервной копии ядра и вне истории миграций модуля:
    /// при восстановлении по частям она оказалась бы либо дважды, либо нигде.
    /// </summary>
    [Fact]
    public void Table_pinned_to_the_core_schema_is_refused()
    {
        using var db = Build<StrayTableContext>(o => new StrayTableContext(o));

        var ex = Assert.Throws<InvalidOperationException>(
            () => ModuleSchemaMigrator.EnsureModelStaysInSchema("probe", Declared, db));

        Assert.Contains("public.strays", ex.Message);
        Assert.Contains("вышел из своей схемы", ex.Message);
    }

    /// <summary>
    /// Внешний ключ сквозь границу схем — отказ: он связал бы два набора миграций и две резервные
    /// копии в одно целое, а общей транзакции у контекстов нет. Ссылка на объект ядра хранится
    /// идентификатором, целость проверяет код.
    /// </summary>
    [Fact]
    public void Foreign_key_across_schemas_is_refused()
    {
        using var db = Build<CrossSchemaContext>(o => new CrossSchemaContext(o));

        var ex = Assert.Throws<InvalidOperationException>(
            () => ModuleSchemaMigrator.EnsureModelStaysInSchema(
                "probe", new ModuleSchema("probe", typeof(CrossSchemaContext)), db));

        Assert.Contains("внешний ключ", ex.Message);
    }

    /// <summary>Контекст, оставшийся в своей схеме, проходит — иначе отказ был бы просто всегда.</summary>
    [Fact]
    public void Model_inside_its_own_schema_passes()
    {
        using var db = Build<TidyContext>(o => new TidyContext(o));

        ModuleSchemaMigrator.EnsureModelStaysInSchema(
            "probe", new ModuleSchema("probe", typeof(TidyContext)), db);
    }

    /// <summary>
    /// История миграций без явной схемы — отказ при старте.
    ///
    /// Это тот отказ, который иначе приходит позже всех и не там: EF кладёт историю в схему по
    /// умолчанию своей служебной модели, то есть в одну таблицу с историей ядра, — и обновление ЯДРА
    /// однажды останавливается на миграции модуля, которой в его сборке нет.
    /// </summary>
    [Fact]
    public void History_table_without_its_own_schema_is_refused()
    {
        using var db = Build<TidyContext>(o => new TidyContext(o), history: false);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ModuleSchemaMigrator.EnsureHistoryStaysInSchema(
                "probe", new ModuleSchema("probe", typeof(TidyContext)), db));

        Assert.Contains("историю рядом с историей ядра", ex.Message);
    }

    /// <summary>Названная схема истории проходит.</summary>
    [Fact]
    public void History_table_in_the_module_schema_passes()
    {
        using var db = Build<TidyContext>(o => new TidyContext(o));

        ModuleSchemaMigrator.EnsureHistoryStaysInSchema(
            "probe", new ModuleSchema("probe", typeof(TidyContext)), db);
    }

    /// <summary>
    /// Контекст на строке подключения, которой не существует: к базе никто не идёт — проверяется
    /// модель и настройка, а не данные.
    /// </summary>
    private static TContext Build<TContext>(
        Func<DbContextOptions<TContext>, TContext> create, bool history = true)
        where TContext : ModuleDbContext
    {
        var options = new DbContextOptionsBuilder<TContext>();
        options.UseNpgsql("Host=нет;Database=нет", npgsql =>
        {
            if (history) npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "probe");
        });
        return create(options.Options);
    }

    private sealed class StrayTableContext(DbContextOptions<StrayTableContext> options)
        : ModuleDbContext(options)
    {
        protected override string Schema => "probe";

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Stray>().ToTable("strays", ModuleSchema.CoreSchema);
        }
    }

    private sealed class CrossSchemaContext(DbContextOptions<CrossSchemaContext> options)
        : ModuleDbContext(options)
    {
        protected override string Schema => "probe";

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Stray>().ToTable("strays", ModuleSchema.CoreSchema);
            builder.Entity<Tidy>().ToTable("tidies").HasOne<Stray>().WithMany().HasForeignKey(t => t.StrayId);
        }
    }

    private sealed class TidyContext(DbContextOptions<TidyContext> options) : ModuleDbContext(options)
    {
        protected override string Schema => "probe";

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            builder.Entity<Tidy>().ToTable("tidies");
        }
    }

    private sealed class Stray
    {
        public Guid Id { get; set; }
    }

    private sealed class Tidy
    {
        public Guid Id { get; set; }

        public Guid StrayId { get; set; }
    }
}
