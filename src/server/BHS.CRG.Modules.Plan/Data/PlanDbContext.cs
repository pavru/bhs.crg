using BHS.CRG.Domain.Common;
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
/// </summary>
public sealed class PlanDbContext(DbContextOptions<PlanDbContext> options) : ModuleDbContext(options)
{
    /// <summary>Имя схемы. То же, что объявлено модулем (<c>PlanModule.Schema</c>) — ядро сверяет.</summary>
    public const string SchemaName = "plan";

    /// <summary>Имя таблицы истории миграций — то же, что у EF по умолчанию; своё только место.</summary>
    public const string HistoryTableName = "__EFMigrationsHistory";

    /// <summary>
    /// Имя теневого свойства с версией строки. Версия — системная колонка PostgreSQL <c>xmin</c>, как у
    /// счёта и накладной: её меняет любая запись строки, и забыть её поднять нельзя. Таблица модуля,
    /// которую правят с экрана, объявляет у себя <c>Property&lt;uint&gt;(RowVersion).IsRowVersion()</c> —
    /// и её одновременная правка отвечает отказом 409 (см. <see cref="SaveChangesAsync" />).
    /// </summary>
    internal const string RowVersion = "RowVersion";

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

    /// <summary>
    /// Запись, проигравшая одновременной, отвечает отказом 409, а не 500 — одним местом на весь модуль,
    /// а не у каждого адреса: адрес, который забыл бы перевести исключение EF, отвечал бы «внутренней
    /// ошибкой сервера» о ситуации, где человеку надо перечитать и повторить.
    ///
    /// <para>Стоит с каркаса, хотя таблиц ещё нет: первая таблица модуля получает перевод, не вспоминая
    /// о нём. Слова отказа общие; таблица, которой нужны свои («смету изменили…»), уточнит их здесь же,
    /// как счёт и накладная у <c>CostsDbContext</c>.</para>
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        }
        catch (DbUpdateConcurrencyException e)
        {
            throw Concurrent(e);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateConcurrencyException e)
        {
            throw Concurrent(e);
        }
    }

    private static ConflictException Concurrent(DbUpdateConcurrencyException e) =>
        new("Запись изменили одновременно с этой правкой, и она не записана — целиком, ни одной частью. " +
            "Перечитайте её и повторите: записанная поверх, она затёрла бы чужую правку.",
            e);
}
