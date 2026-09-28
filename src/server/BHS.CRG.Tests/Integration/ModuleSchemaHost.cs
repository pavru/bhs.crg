using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Хост для проверки второго контекста базы (задача A2a этапа 2, issue #1072): оба модуля включены,
/// база своя — и схема модуля перед запуском СНОСИТСЯ.
///
/// <para>⚠️ Снос — главное в этом хосте, и без него проверка была бы нарисованной. Схема, созданная
/// прошлым прогоном, остаётся в базе навсегда: тест «схема появилась при старте» проходил бы и с
/// вырезанной миграцией модуля — проверено прямо, снятием вызова из <c>StartupTasks</c> (сторож
/// остался зелёным). Это тот самый случай из памяти проекта: накопленная база отличается ИСТОРИЕЙ, и
/// всё, что зависит от порядка создания, проверяется на ней вхолостую.</para>
///
/// <para>⚠️ Перед сносом в схеме заводится таблица с одной строкой — данные, которые у модуля уже
/// есть. Так выглядит ОБНОВЛЕНИЕ: база рабочая, схема модуля на месте, а миграция ещё не применена.
/// Проверять только чистую установку значило бы проверить одну из двух дорог — ту, на которой терять
/// нечего (урок #1046: сторож переписи ронял чистую установку, потому что локально база уже была).
/// </para>
///
/// <para>Наследуется от <see cref="IntegrationTestFixture" />, чтобы не разойтись с ним в том, чем
/// тестовый хост держится (подставное хранилище, снятые расписания, ослабленные пределы частоты);
/// переопределения добавляются ПОСЛЕ базовых — последний слой конфигурации выигрывает.</para>
///
/// <para>Своя база — по той же причине, что у <see cref="CostsOnlyHost" /> и
/// <see cref="ModulePortsHost" />: состав системных ролей приводится при старте к объявленному, и хост
/// с другим набором модулей менял бы права ролям у соседних классов.</para>
/// </summary>
public sealed class ModuleSchemaHost : IntegrationTestFixture
{
    /// <summary>Таблица «данные прошлой версии»: её строка обязана пережить миграцию модуля.</summary>
    public const string LeftoverTable = "costs.legacy_rows";

    internal static string ConnectionString { get; } = Dedicated();

    private static string Dedicated()
    {
        var builder = new NpgsqlConnectionStringBuilder(TestConnectionString);
        builder.Database += "_schema";
        return builder.ConnectionString;
    }

    /// <summary>
    /// Готовит базу ДО первого запуска хоста: сносит схему модуля и кладёт в неё строку прошлой версии.
    ///
    /// Конструктор фикстуры выполняется раньше сборки приложения (<c>WebApplicationFactory</c> строит
    /// хост при первом обращении), поэтому здесь ещё можно привести базу в то состояние, из которого
    /// проверка имеет смысл. Базы может не быть вовсе — первый прогон; её создаст миграция ядра.
    /// </summary>
    public ModuleSchemaHost()
    {
        try
        {
            Prepare();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            // Базы ещё нет — так выглядит КАЖДЫЙ прогон в CI и первый прогон на новой машине. Создаём
            // её сами и готовим заново: иначе состояние базы решало бы, что проверяет набор, — у себя
            // проверялось бы обновление, а в CI (где база пуста) молча только чистая установка, и
            // сторож данных прошлой версии был бы там красным без всякой поломки.
            CreateDatabase();
            Prepare();
        }
    }

    /// <summary>Снести схему модуля и положить в неё строку «прошлой версии».</summary>
    private static void Prepare()
    {
        using var conn = new NpgsqlConnection(ConnectionString);
        conn.Open();
        Execute(conn, "DROP SCHEMA IF EXISTS costs CASCADE");
        Execute(conn, "CREATE SCHEMA costs");
        Execute(conn, $"CREATE TABLE {LeftoverTable} (id int primary key)");
        Execute(conn, $"INSERT INTO {LeftoverTable} (id) VALUES (1)");
    }

    /// <summary>
    /// Создать пустую базу этого хоста. Дальше её мигрирует само приложение при старте — как на
    /// установке у заказчика.
    /// </summary>
    private static void CreateDatabase()
    {
        var target = new NpgsqlConnectionStringBuilder(ConnectionString);
        var name = target.Database!;
        target.Database = "postgres";

        using var conn = new NpgsqlConnection(target.ConnectionString);
        conn.Open();
        // Имя базы параметром не передать; складывается оно из константы фикстуры и переменной
        // BHS_TEST_DB, то есть из окружения прогона, а не из данных.
        Execute(conn, $"CREATE DATABASE \"{name}\"");
    }

    private static void Execute(NpgsqlConnection conn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var overrides = new Dictionary<string, string?>
        {
            // Оба модуля: схему объявляет costs, а id живёт на таблицах ядра — вместе они проверяют и
            // то, что модуль без своей схемы старту не мешает.
            ["Modules:Enabled"] = "id,costs",
            ["ConnectionStrings:Postgres"] = ConnectionString,
        };

        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(overrides));
    }
}
