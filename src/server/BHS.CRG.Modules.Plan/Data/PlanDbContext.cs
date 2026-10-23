using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Контекст модуля «Планирование»: схема <c>plan</c>, свой набор миграций, своя история (change
/// <c>plan-01-skeleton</c>, ТЗ CORE-4, D1 зонтика <c>plan-00-epic</c>).
///
/// <para>⚠️ Таблиц здесь пока НЕТ — как и у <c>costs</c> в его каркасе, и по той же причине: таблица,
/// заведённая раньше потребителя, проверяется только тем, что она создалась. Первые таблицы приезжают
/// со своими адресами: смета и её редакции — <c>plan-04</c>, строки и деньги — <c>plan-06</c>.</para>
///
/// <para>Начальная миграция при этом не пустая по смыслу: она создаёт саму схему и историю миграций
/// модуля в ней — то, что включается на чистой и на рабочей базе и переживает выключение модуля.</para>
///
/// <para>Запись, проигравшая одновременной, отвечает 409 без кода здесь: исключение EF переводит общий
/// обработчик API (<c>ApiErrorMapping</c>) для любого контекста. Таблица, которой нужны свои слова
/// («смету изменили…»), переведёт его в <c>ConflictException</c> сама — как счёт у <c>CostsDbContext</c>.
/// Версия строки — системная колонка <c>xmin</c> (<c>IsRowVersion()</c>), как у счёта.</para>
/// </summary>
public sealed class PlanDbContext(DbContextOptions<PlanDbContext> options) : ModuleDbContext(options)
{
    /// <summary>Имя схемы. То же, что объявлено модулем (<c>PlanModule.Schema</c>) — ядро сверяет.</summary>
    public const string SchemaName = "plan";

    /// <summary>Имя таблицы истории миграций — то же, что у EF по умолчанию; своё только место.</summary>
    public const string HistoryTableName = "__EFMigrationsHistory";

    protected override string Schema => SchemaName;

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
}
