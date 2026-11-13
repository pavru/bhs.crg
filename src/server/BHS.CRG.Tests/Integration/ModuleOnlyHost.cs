using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Хост «только этот модуль»: <c>Modules__Enabled</c> из одного кода и своя база рядом с общей
/// тестовой. Общая основа <see cref="CostsOnlyHost" />, <see cref="PlanOnlyHost" /> и
/// <see cref="WorkOnlyHost" /> (ревью PR #1580): они различались только кодом модуля, суффиксом базы и
/// тем, что сносят до старта, и правка того, чем держится такой хост, иначе шла бы в каждую копию.
///
/// <para>⚠️ Своя база — не украшение. Состав системной роли приводится при старте к объявленному, а
/// прав выключенных модулей в каталоге нет, — на общей тестовой базе такой хост снял бы у ролей права
/// остальных модулей, и падали бы соседние классы.</para>
///
/// <para>Наследуется от <see cref="IntegrationTestFixture" />, чтобы не разойтись с ним в том, чем
/// тестовый хост вообще держится. Переопределения добавляются ПОСЛЕ базовых — слой конфигурации,
/// добавленный последним, выигрывает, и одного <c>UseSetting</c> тут не хватило бы: базовый класс
/// кладёт те же ключи ещё и слоем на <c>Build</c>.</para>
///
/// <para>Код, суффикс и сносимые схемы — абстрактные и виртуальные свойства, а не параметры
/// конструктора: базовый класс читает <see cref="HostConnectionString" /> раньше, чем отработал бы
/// конструктор наследника.</para>
/// </summary>
public abstract class ModuleOnlyHost : IntegrationTestFixture
{
    /// <summary>Код единственного включённого модуля.</summary>
    protected abstract string ModuleCode { get; }

    /// <summary>Суффикс своей базы: «&lt;база прогона&gt;_&lt;суффикс&gt;» (<see cref="TestDatabases" />).</summary>
    protected abstract string DatabaseSuffix { get; }

    /// <summary>
    /// Схемы, которые хост СНОСИТ до старта (ревью PR #1580, #1932). Без сноса проверка «схема
    /// мигрирована при старте» проходила бы на схеме прошлого прогона — и с вырезанной миграцией тоже.
    /// Пусто — ничего не сносится. <c>public</c> в списке — схема ядра: она создаётся заново пустой, и
    /// хост стартует как чистая установка.
    /// </summary>
    protected virtual IReadOnlyList<string> SchemasDroppedBeforeStart => [];

    /// <summary>Снос до старта действительно был — тесты опираются на него и проверяют это.</summary>
    internal bool SchemaDroppedBeforeStart { get; }

    /// <summary>База хоста. Создаётся сама: приложение мигрирует схему при старте, а миграция создаёт базу.</summary>
    internal string ConnectionString => TestDatabases.ConnectionString(DatabaseSuffix);

    protected override string HostConnectionString => ConnectionString;

    /// <summary>
    /// Снос — после того, как база занята (<see cref="TestRunDatabase.ClaimAsync" />): чужой прогон на ней
    /// же обязан получить отказ раньше первой правки. Заодно база создаётся, если её нет. Занятость держит
    /// сессионная блокировка соединения, а не строка в базе, — снос схем её не снимает.
    /// </summary>
    protected ModuleOnlyHost()
    {
        var schemas = SchemasDroppedBeforeStart;
        if (schemas.Count == 0) return;

        // Task.Run — чтобы ожидание в конструкторе не зависело от контекста синхронизации xUnit.
        Task.Run(() => TestRunDatabase.ClaimAsync(ConnectionString, ClaimPatience)).GetAwaiter().GetResult();

        using var conn = new NpgsqlConnection(ConnectionString);
        conn.Open();
        foreach (var schema in schemas)
        {
            using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", conn);
            drop.ExecuteNonQuery();
        }

        // Схему ядра миграции не создают — её создаёт сама база; снесённую возвращаем пустой.
        using var restore = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS public", conn);
        restore.ExecuteNonQuery();
        SchemaDroppedBeforeStart = true;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var overrides = new Dictionary<string, string?>
        {
            ["Modules:Enabled"] = ModuleCode,
            ["ConnectionStrings:Postgres"] = ConnectionString,
        };

        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(overrides));
    }
}
