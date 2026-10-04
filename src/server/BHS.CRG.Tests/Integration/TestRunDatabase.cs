using System.Collections.Concurrent;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Что прогон делает с тестовой базой ДО старта хоста (issue #1142): убеждается, что база тестовая,
/// занимает её на время прогона и сносит учётные таблицы.
///
/// <para>Отдельно от фикстуры и на голом подключении, потому что всё это обязано случиться раньше
/// приложения. Старт сам пишет в базу (миграции, роли, права), и то, что он создаёт, очистке после
/// старта пришлось бы знать поимённо — список разошёлся бы с приложением на первой же роли,
/// заведённой мимо него. Очистка ДО старта списка не требует: что стартом создаётся, то он и
/// создаст заново.</para>
/// </summary>
internal static class TestRunDatabase
{
    /// <summary>
    /// Учётные таблицы, очищаемые раз за прогон, — в придачу к тому, что чистится между классами.
    /// Между классами их трогать нельзя: вошедший пользователь живёт дольше одного теста, а роли
    /// создаёт только старт. В начале прогона их ещё никто не завёл, и всё, что в них лежит, — от
    /// прошлых прогонов. Каскад уносит и то, что ссылается на учётную запись или роль (права роли,
    /// роли пользователя, его настройки); сессии названы отдельно — внешнего ключа у них нет.
    /// </summary>
    internal static readonly string[] IdentityTables = ["AspNetUsers", "AspNetRoles", "RefreshTokens"];

    /// <summary>Ключ замка прогона: число произвольное, важно лишь, что у всех прогонов оно одно.</summary>
    internal const long RunLock = 1142_2026;

    /// <summary>Подключения, которыми этот процесс держит свои базы: замок живёт, пока живо оно.</summary>
    private static readonly ConcurrentDictionary<string, NpgsqlConnection> Claims = new();

    /// <summary>
    /// База, а не строка подключения: «сервер:порт/имя». Одну и ту же базу строка называет по-разному
    /// (порядок ключей, лишний параметр), и ключ по тексту строки дал бы одной базе две очистки.
    /// </summary>
    internal static string KeyOf(string connectionString)
    {
        var parsed = new NpgsqlConnectionStringBuilder(connectionString);
        return $"{parsed.Host}:{parsed.Port}/{parsed.Database}";
    }

    /// <summary>
    /// Имя базы обязано быть тестовым. Прогон сносит в ней всё, включая учётные записи и роли, а имя
    /// целиком приходит из переменной окружения: <c>BHS_TEST_DB=bhs_crg</c> — это база стенда.
    /// </summary>
    internal static void EnsureTestName(string connectionString)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database ?? "";
        const string prefix = IntegrationTestFixture.TestDatabasePrefix;
        if (name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length) return;

        throw new InvalidOperationException(
            $"База «{name}» не похожа на тестовую: имя обязано начинаться с «{prefix}». " +
            "Прогон сносит в своей базе всё, включая учётные записи и роли, а «bhs_crg» — база стенда " +
            "на том же порту. Проверьте переменную BHS_TEST_DB.");
    }

    /// <summary>
    /// Занять базу на время прогона — или отказать, если её уже держит другой прогон.
    ///
    /// <para>Зачем. Очистка сносит строки, и второй прогон на той же базе снёс бы их из-под первого:
    /// падения при этом массовые, случайные и выглядят как дефект кода (так было у общей базы всегда,
    /// issue #618). Замок превращает это в отказ с причиной — у второго, до первой же правки базы.</para>
    ///
    /// <para>Замок совещательный и держится подключением: процесс умер — замок снят, убирать за ним
    /// нечего. Поэтому же висящий <c>testhost.exe</c> прошлого прогона базу держит, и отказ его
    /// называет — прежде он молча чистил базу под следующим прогоном.</para>
    /// </summary>
    internal static async Task ClaimAsync(string connectionString, TimeSpan patience)
    {
        EnsureTestName(connectionString);
        var key = KeyOf(connectionString);
        if (Claims.ContainsKey(key)) return;

        var connection = await LockAsync(connectionString, patience);
        if (!Claims.TryAdd(key, connection)) await connection.DisposeAsync();
    }

    /// <summary>
    /// Взять замок прогона новым подключением; оно и есть замок — закрыть его значит отпустить базу.
    /// Ждём недолго: тестовый процесс прошлого прогона доживает секунды после его конца.
    /// </summary>
    internal static async Task<NpgsqlConnection> LockAsync(string connectionString, TimeSpan patience)
    {
        var connection = await OpenAsync(connectionString);
        var deadline = DateTime.UtcNow + patience;
        while (true)
        {
            await using (var attempt = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection))
            {
                attempt.Parameters.AddWithValue("key", RunLock);
                if ((bool)(await attempt.ExecuteScalarAsync())!) return connection;
            }

            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(500);
        }

        await using (connection)
        {
            throw new InvalidOperationException(
                $"Базу «{connection.Database}» уже держит другой прогон тестов ({await HolderAsync(connection)}). " +
                "Очистка перед прогоном снесла бы его строки, поэтому этот прогон не начат. Дождитесь его " +
                "конца, снимите висящий testhost.exe или задайте своё имя базы в BHS_TEST_DB.");
        }
    }

    /// <summary>
    /// Снести учётные таблицы. Тех, которых в базе ещё нет (она пуста или отстала по миграциям),
    /// не касаемся: чистить в них нечего, а создаст их старт.
    /// </summary>
    internal static async Task ClearIdentityAsync(string connectionString)
    {
        await using var connection = await OpenAsync(connectionString);

        var present = new List<string>();
        foreach (var table in IdentityTables)
        {
            await using var exists = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", connection);
            exists.Parameters.AddWithValue("name", $"public.\"{table}\"");
            if ((bool)(await exists.ExecuteScalarAsync())!) present.Add($"\"{table}\"");
        }
        if (present.Count == 0) return;

        // Имена — из константы выше, не из данных: параметром имя таблицы не передать.
        await using var truncate = new NpgsqlCommand(
            $"TRUNCATE TABLE {string.Join(", ", present)} RESTART IDENTITY CASCADE", connection);
        await truncate.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Подключение мимо пула и с подписью: по ней отказ соседнего прогона называет, кто держит базу.
    /// Базы может не быть вовсе — первый прогон и каждый прогон в CI; тогда создаём её сами, а схему
    /// накатит приложение при старте, как на установке.
    /// </summary>
    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            // Только ASCII: всё прочее сервер в application_name заменяет знаками вопроса.
            ApplicationName = $"BHS.CRG tests, pid {Environment.ProcessId}",
        };

        try
        {
            return await ConnectAsync(target.ConnectionString);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            var name = target.Database!;
            var server = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = "postgres" };
            await using (var admin = await ConnectAsync(server.ConnectionString))
            {
                try
                {
                    // Имя складывается из константы и BHS_TEST_DB — из окружения прогона, а не из
                    // данных; кавычки в нём удваивает Quote.
                    await using var create = new NpgsqlCommand($"CREATE DATABASE {TestDatabases.Quote(name)}", admin);
                    await create.ExecuteNonQueryAsync();
                }
                catch (PostgresException raced) when (raced.SqlState == PostgresErrorCodes.DuplicateDatabase)
                {
                    // Соседний прогон создал её между нашей попыткой подключиться и этой строкой.
                }
            }
            return await ConnectAsync(target.ConnectionString);
        }
    }

    private static async Task<NpgsqlConnection> ConnectAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Кто держит замок этой базы — словами для отказа.</summary>
    private static async Task<string> HolderAsync(NpgsqlConnection connection)
    {
        await using var query = new NpgsqlCommand(
            """
            SELECT a.application_name, a.backend_start
            FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
            WHERE l.locktype = 'advisory' AND l.granted AND l.objid = CAST(@key AS oid)
              AND l.database = (SELECT oid FROM pg_database WHERE datname = current_database())
            LIMIT 1
            """, connection);
        query.Parameters.AddWithValue("key", RunLock);
        await using var reader = await query.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return "кто именно — узнать не удалось: замок уже отпущен";

        var name = reader.IsDBNull(0) || reader.GetString(0).Length == 0 ? "подключение без подписи" : reader.GetString(0);
        return $"{name}; подключился {reader.GetDateTime(1).ToLocalTime():HH:mm:ss}";
    }
}

/// <summary>
/// Работа, которая делается один раз на ключ за процесс: первый спросивший выполняет, остальные
/// получают ту же задачу.
///
/// <para>⚠️ Неудача НЕ запоминается. Запомненный отказ ронял бы весь прогон из-за одного сбоя на
/// старте: фикстура коллекции спрашивает первой и единственный раз. Следующий спросивший пробует
/// заново — а тот, у кого не вышло, получает свой отказ и на непочищенной базе не работает.</para>
/// </summary>
internal sealed class OncePerKey
{
    private readonly ConcurrentDictionary<string, Lazy<Task>> _runs = new();

    public async Task RunAsync(string key, Func<Task> work)
    {
        var run = _runs.GetOrAdd(key, _ => new Lazy<Task>(work));
        try
        {
            await run.Value;
        }
        catch
        {
            // Снимаем ровно свою запись: соседний поток мог уже завести новую попытку.
            _runs.TryRemove(KeyValuePair.Create(key, run));
            throw;
        }
    }

    /// <summary>Работа по ключу выполнена и удалась.</summary>
    public bool Done(string key) =>
        _runs.TryGetValue(key, out var run) && run is { IsValueCreated: true, Value.IsCompletedSuccessfully: true };

    /// <summary>
    /// Выполнить и, если отказала база, один раз повторить. Взаимная блокировка на TRUNCATE и
    /// оборванное подключение проходят со второй попытки; отказ НАШЕЙ проверки (фоновые задачи не
    /// доработали, имя базы не тестовое) повторять незачем — он не случаен.
    /// </summary>
    public static async Task RetryOnceAsync(Func<Task> work, TimeSpan pause)
    {
        try
        {
            await work();
        }
        catch (Exception e) when (e is NpgsqlException || e.InnerException is NpgsqlException)
        {
            await Task.Delay(pause);
            await work();
        }
    }
}
