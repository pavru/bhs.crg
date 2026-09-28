using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Контекст модуля «Счета и накладные»: схема <c>costs</c>, свой набор миграций, своя история
/// (задача A2a этапа 2, issue #1072, ТЗ CORE-4).
///
/// <para>⚠️ Таблиц здесь пока НЕТ, и это решение, а не заготовка. A2a кладёт границу — схему, набор
/// миграций, место в старте и правило «нет общей транзакции ядро↔модуль», — а первая таблица (счёт)
/// приезжает своей задачей, C1 (issue #1076), вместе с типом, формой ввода и адресами, которые её
/// читают. Таблица, заведённая раньше потребителя, проверяется только тем, что она создалась:
/// колонки у неё угаданы, а угадать их за C1 значит переписать её следующим PR.</para>
///
/// <para>Начальная миграция при этом не пустая по смыслу: она создаёт саму схему и историю миграций
/// модуля в ней. Ровно это и включается на чистой и на рабочей базе, и ровно это переживает
/// выключение модуля.</para>
/// </summary>
public sealed class CostsDbContext(DbContextOptions<CostsDbContext> options) : ModuleDbContext(options)
{
    /// <summary>Имя схемы. То же, что объявлено модулем (<c>CostsModule.Schema</c>) — ядро сверяет.</summary>
    public const string SchemaName = "costs";

    protected override string Schema => SchemaName;

    /// <summary>
    /// Настройка контекста — одна на всех, кто его создаёт: и приложение
    /// (<c>CostsModule.RegisterServices</c>), и <c>dotnet ef</c> через
    /// <see cref="CostsDbContextFactory" />.
    ///
    /// <para>⚠️ История миграций названа ЯВНО, вместе со схемой. Без этого EF кладёт её в схему по
    /// умолчанию СВОЕЙ служебной модели, а не нашей, — то есть в <c>public</c>, в одну таблицу с
    /// историей ядра. Выглядело бы это исправной работой: миграции применяются, приложение
    /// поднимается. А затем обновление ядра встретило бы в своей истории строки, которых нет в его
    /// сборке, — и остановилось бы у заказчика, назвав чужую миграцию. Сторож на это есть: история
    /// модуля обязана лежать в схеме модуля, а в истории ядра его миграций быть не должно.</para>
    ///
    /// <para>Одна на всех потому, что разойтись эти две настройки могут только молча: миграции
    /// генерировались бы в одну схему, а применялись в другую.</para>
    /// </summary>
    public static void Configure(DbContextOptionsBuilder options, string? connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsHistoryTable(HistoryTableName, SchemaName));

    /// <summary>Имя таблицы истории миграций — то же, что у EF по умолчанию; своё только место.</summary>
    public const string HistoryTableName = "__EFMigrationsHistory";
}
