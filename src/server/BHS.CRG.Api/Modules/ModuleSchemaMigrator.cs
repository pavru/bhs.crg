using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Миграция схем ВКЛЮЧЁННЫХ модулей при старте (ТЗ CORE-4, CORE-29, CORE-31; задача A2a этапа 2,
/// issue #1072).
///
/// <para>Живёт здесь, а не в ядре и не в модуле, по тому же направлению ссылок, что и проекция типов
/// модулей: это единственный слой, знающий и контракты модулей, и инфраструктуру. Модуль свой
/// контекст РЕГИСТРИРУЕТ, а мигрирует его ядро — иначе обязанность досталась бы тем модулям, которые
/// о ней вспомнили, и модуль без миграции выглядел бы обычным модулем, а не поломкой.</para>
///
/// <para>⚠️ Схема выключенного модуля не мигрируется — и не удаляется. Его данные переживают
/// выключение нетронутыми: включить модуль обратно можно, ничего не восстанавливая.</para>
///
/// <para>⚠️ Общей транзакции с миграцией ядра тут нет и быть не может: два контекста — два соединения
/// (см. <see cref="ModuleDbContext" />). Поэтому порядок односторонний: сначала схема ядра, потом
/// схемы модулей. Модуль вправе рассчитывать, что ядро на месте; обратной зависимости не бывает
/// (CORE-2).</para>
/// </summary>
public static class ModuleSchemaMigrator
{
    /// <summary>
    /// Применить миграции схем включённых модулей.
    /// </summary>
    /// <param name="scoped">Провайдер ОБЛАСТИ, а не корневой: контекст базы живёт областью.</param>
    public static async Task MigrateModuleSchemasAsync(
        this IServiceProvider scoped, ILogger logger, CancellationToken ct = default)
    {
        var registry = scoped.GetRequiredService<ModuleRegistry>();

        foreach (var module in registry.Enabled)
        {
            if (module.Schema is not { } schema) continue;

            // Объявление сверено ещё на сборке (ModuleDataDeclaration), но проверяется и здесь:
            // мигрировать по негодному объявлению значит создать схему с именем, которого потом никто
            // не найдёт. Проверка дешёвая, а порядок вызовов со временем меняется.
            if (schema.Validate(module.Code) is { } problem)
                throw new InvalidOperationException(
                    $"Схема модуля «{module.Code}» негодна, миграция не начата: {problem}");

            var (db, ours) = Resolve(scoped, module.Code, schema);
            try
            {
                EnsureModelStaysInSchema(module.Code, schema, db);
                EnsureHistoryStaysInSchema(module.Code, schema, db);

                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                if (pending.Count == 0)
                {
                    logger.LogDebug("Схема модуля {Module} в порядке: применять нечего", module.Code);
                    continue;
                }

                // Перепись до и после — как у ядра (CORE-29), и по той же причине: миграция,
                // потерявшая строки, не сообщит об этом ничем. Считается только когда есть что
                // применять: без ожидающих миграций терять данные нечему, а сумма по схеме — это
                // запрос по каждой её таблице.
                var before = await MigrationCensus.ReadSchemaAsync(db, schema.Name, ct);

                await db.Database.MigrateAsync(ct);

                var after = await MigrationCensus.ReadSchemaAsync(db, schema.Name, ct);
                MigrationCensus.EnsureNothingLost(before, after, schema.Name);

                logger.LogInformation(
                    "Схема модуля {Module} ({Schema}): применено миграций {Count}; {Census}",
                    module.Code, schema.Name, pending.Count,
                    before is null
                        ? "схемы до этого не было — сверять было нечего"
                        : "данные на месте: " + (after?.Describe() ?? ""));
            }
            finally
            {
                // Созданный фабрикой контекст закрываем мы — его жизнью не управляет контейнер.
                // Полученный из области не трогаем: его закроет область запроса, а закрытый раньше
                // времени контекст уронил бы всё, что в этой области идёт после.
                if (ours) await db.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Взять контекст модуля из контейнера: сам контекст либо его фабрику
    /// (<see cref="IDbContextFactory{TContext}" />).
    ///
    /// <para>Фабрика поддержана наравне с контекстом нарочно: модулю она нужна там, где области
    /// запроса нет вовсе — в фоновой работе, — и это обычный приём EF, а не обход правил (найдено
    /// ревью PR #1107). Оговорка та же, что в <c>ModuleDataDeclaration</c>: помощники EF регистрируют
    /// рядом и сам контекст, так что без этой ветки отказ доставался бы только модулю, который завёл
    /// фабрику своими руками.</para>
    /// </summary>
    /// <returns>Контекст и признак «создали мы» — такой контекст нам же и закрывать.</returns>
    /// <remarks>Открыт для прогона: ветку с фабрикой иначе нечем было бы сломать — у настоящих модулей
    /// сегодня зарегистрирован сам контекст, — а непроверяемая ветка ничего не утверждает.</remarks>
    public static (ModuleDbContext Context, bool Ours) Resolve(
        IServiceProvider scoped, string code, ModuleSchema schema)
    {
        if (scoped.GetService(schema.ContextType) is ModuleDbContext registered) return (registered, false);

        var factoryType = typeof(IDbContextFactory<>).MakeGenericType(schema.ContextType);
        if (scoped.GetService(factoryType) is { } factory
            && factoryType.GetMethod(nameof(IDbContextFactory<DbContext>.CreateDbContext))!
                .Invoke(factory, null) is ModuleDbContext created)
            return (created, true);

        throw new InvalidOperationException(
            $"Модуль «{code}» объявил схему «{schema.Name}» с контекстом " +
            $"«{schema.ContextType.Name}», но в контейнере нет ни его, ни его фабрики " +
            $"(IDbContextFactory<{schema.ContextType.Name}>). Мигрировать нечем.");
    }

    /// <summary>
    /// Все таблицы контекста — в своей схеме, и ни одного внешнего ключа за её пределы.
    ///
    /// <para>Зачем проверять то, что базовый контекст и так задаёт схемой по умолчанию. Умолчание
    /// обходится одной строкой — указанием схемы у таблицы, — и обойти его проще всего случайно:
    /// копированием настройки из ядра. Таблица модуля в схеме ядра стояла бы в чужой истории
    /// миграций и не попадала бы в свою: обновление ядра встретило бы таблицу, которую не создавало,
    /// а восстановление по частям оставило бы её либо дважды, либо нигде.</para>
    ///
    /// <para>Внешний ключ сквозь границу схем запрещён отдельно: он связал бы два набора миграций и
    /// две копии в одно целое — а общей транзакции у контекстов нет, то есть порядок восстановления
    /// перестал бы существовать. Ссылка на объект ядра хранится идентификатором, целость проверяет
    /// код (см. <see cref="ModuleDbContext" />).</para>
    /// </summary>
    /// <remarks>Открыт для прогона нарочно: на настоящем контексте модуля сегодня нечего нарушать —
    /// таблиц в нём ещё нет, — поэтому сторож ломается на поддельном контексте в тестах. Сторож,
    /// который нельзя сломать, ничего не доказывает.</remarks>
    public static void EnsureModelStaysInSchema(string code, ModuleSchema schema, ModuleDbContext db)
    {
        var strangers = new List<string>();

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null) continue;

            var actual = entity.GetSchema() ?? db.Model.GetDefaultSchema();
            if (!string.Equals(actual, schema.Name, StringComparison.Ordinal))
                strangers.Add($"таблица «{actual ?? "public"}.{table}» ({entity.DisplayName()})");

            foreach (var fk in entity.GetForeignKeys())
            {
                var principal = fk.PrincipalEntityType;
                var principalSchema = principal.GetSchema() ?? db.Model.GetDefaultSchema();
                if (principal.GetTableName() is not null
                    && !string.Equals(principalSchema, schema.Name, StringComparison.Ordinal))
                    strangers.Add(
                        $"внешний ключ {entity.DisplayName()} → «{principalSchema ?? "public"}." +
                        $"{principal.GetTableName()}»");
            }
        }

        if (strangers.Count == 0) return;

        throw new InvalidOperationException(
            $"Контекст модуля «{code}» вышел из своей схемы «{schema.Name}»: " +
            string.Join("; ", strangers) + ".\n" +
            "Таблицы модуля обязаны лежать в его схеме: по ней видно в базе, чьи это данные, и по ней " +
            "будет сниматься его часть резервной копии (задача A2b, issue #1073 — сегодня таблиц " +
            "модуля в копии нет вовсе). Внешних ключей сквозь " +
            "схемы не бывает — общей транзакции у контекстов нет, и связанные ключом схемы нельзя " +
            "восстановить по отдельности. На объект ядра ссылаются идентификатором, целость " +
            "проверяет код.");
    }

    /// <summary>
    /// История миграций модуля — в схеме модуля.
    ///
    /// <para>Это тот отказ, который иначе приходит позже всех и не там. EF, не получив схему явно,
    /// кладёт историю в схему по умолчанию СВОЕЙ служебной модели, то есть в <c>public</c> — в одну
    /// таблицу с историей ядра. Выглядит это исправной работой: миграции применяются, приложение
    /// поднимается, тесты зелёные. А потом обновление ЯДРА встречает в своей истории строки, которых
    /// нет в его сборке, и останавливается у заказчика, называя чужую миграцию.</para>
    /// </summary>
    /// <remarks>Открыт для прогона по той же причине, что и <see cref="EnsureModelStaysInSchema" />:
    /// настоящий контекст схему истории называет, и сломать его можно только правкой кода — а сторож
    /// обязан падать в прогоне.</remarks>
    public static void EnsureHistoryStaysInSchema(string code, ModuleSchema schema, ModuleDbContext db)
    {
        var relational = db.GetService<IDbContextOptions>().Extensions
            .OfType<RelationalOptionsExtension>().FirstOrDefault();

        if (string.Equals(relational?.MigrationsHistoryTableSchema, schema.Name, StringComparison.Ordinal))
            return;

        throw new InvalidOperationException(
            $"Контекст модуля «{code}» не назвал схему для истории миграций " +
            $"(сейчас «{relational?.MigrationsHistoryTableSchema ?? "не задана"}», ожидается " +
            $"«{schema.Name}»). Без явного имени EF кладёт историю рядом с историей ядра, в одну " +
            "таблицу: поднимется всё, а следующее обновление ядра остановится на миграции модуля, " +
            "которой в его сборке нет. Задайте MigrationsHistoryTable(имя, схема) в настройке " +
            "контекста.");
    }
}
