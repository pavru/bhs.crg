using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Контекст модуля «Учёт работ»: схема <c>work</c>, свой набор миграций, своя история (change
/// <c>work-10-skeleton</c>, ТЗ CORE-4, D1 зонтика <c>work-00-epic</c>).
///
/// <para>⚠️ Таблиц здесь пока НЕТ — как у <c>costs</c> и <c>plan</c> в их каркасах, и по той же причине:
/// таблица, заведённая раньше потребителя, проверяется только тем, что она создалась. Первые таблицы
/// приезжают со своими адресами: назначения — <c>work-11</c>, отчёт и его строки — <c>work-13</c>.</para>
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

    protected override string Schema => SchemaName;

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
