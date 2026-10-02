using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Правила, по которым прогон обращается со своей базой (issue #1142), — та их часть, что считается
/// без базы: имя, ключ, «один раз на ключ» и повтор. Живую половину — снос учётных таблиц и замок —
/// держит <see cref="RunResetTests" />.
/// </summary>
public class TestRunDatabaseTests
{
    private static string Named(string database) =>
        $"Host=localhost;Port=5433;Username=postgres;Password=x;Database={database}";

    // ── Имя базы ──────────────────────────────────────────────────────────────

    private const string Prefix = IntegrationTestFixture.TestDatabasePrefix;

    // Имена — от префикса, а не строкой: имя базы строкой в тестах не пишется
    // (TestDatabaseNameGuardTests), и образцы здесь не исключение.
    [Theory]
    [InlineData("test")]
    [InlineData("ci")]
    [InlineData("test_966_lines")]
    public void Тестовое_имя_проходит(string rest) => TestRunDatabase.EnsureTestName(Named(Prefix + rest));

    /// <summary>
    /// Имя приходит из переменной окружения целиком, а прогон сносит в базе всё, включая учётные
    /// записи. База стенда стоит на том же порту и отличается от тестовых одним суффиксом.
    /// </summary>
    [Theory]
    [InlineData("bhs_crg")]     // база дев-стенда
    [InlineData(Prefix)]        // префикс без имени — опечатка в переменной
    [InlineData("postgres")]
    [InlineData("crg_customer")]
    public void Нетестовое_имя_останавливает_прогон(string database)
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => TestRunDatabase.EnsureTestName(Named(database)));

        Assert.Contains(database, refusal.Message);
        Assert.Contains("BHS_TEST_DB", refusal.Message);
    }

    /// <summary>Отказ по имени приходит ДО подключения: сервер, которого нет, до него не доходит.</summary>
    [Fact]
    public async Task Нетестовую_базу_не_занимают_и_не_трогают()
    {
        var unreachable = "Host=192.0.2.1;Port=1;Timeout=1;Username=x;Password=x;Database=bhs_crg";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestRunDatabase.ClaimAsync(unreachable, TimeSpan.Zero));
    }

    // ── Ключ: база, а не строка ───────────────────────────────────────────────

    /// <summary>
    /// Одну базу строка подключения называет по-разному. Ключ по тексту строки дал бы ей две очистки
    /// за прогон — вторая снесла бы статический посев классов, отработавших на первой.
    /// </summary>
    [Fact]
    public void Одна_база_один_ключ_как_бы_ни_была_записана_строка()
    {
        var plain = "Host=localhost;Port=5433;Username=postgres;Password=x;Database=bhs_crg_test_lines";
        var reordered = "Database=bhs_crg_test_lines;Password=x;Include Error Detail=true;Port=5433;Host=localhost;Username=postgres;Timeout=30";
        var rebuilt = new NpgsqlConnectionStringBuilder(plain) { Pooling = false }.ConnectionString;

        Assert.Equal(TestRunDatabase.KeyOf(plain), TestRunDatabase.KeyOf(reordered));
        Assert.Equal(TestRunDatabase.KeyOf(plain), TestRunDatabase.KeyOf(rebuilt));
    }

    [Fact]
    public void Разные_базы_и_разные_серверы_ключ_не_делят()
    {
        var key = TestRunDatabase.KeyOf(Named("проба"));

        Assert.NotEqual(key, TestRunDatabase.KeyOf(Named("проба_lines")));
        Assert.NotEqual(key, TestRunDatabase.KeyOf(Named("проба").Replace("5433", "5434")));
    }

    // ── Один раз на ключ ──────────────────────────────────────────────────────

    [Fact]
    public async Task Работа_по_ключу_делается_один_раз()
    {
        var once = new OncePerKey();
        var runs = 0;
        Task Work() { runs++; return Task.CompletedTask; }

        Assert.False(once.Done("база"));
        await once.RunAsync("база", Work);
        await once.RunAsync("база", Work);
        await once.RunAsync("другая", Work);

        Assert.Equal(2, runs);
        Assert.True(once.Done("база"));
    }

    /// <summary>
    /// Неудача не запоминается. Запомненный отказ ронял бы весь прогон из-за одного сбоя на старте:
    /// фикстура коллекции спрашивает первой, и её отказ достался бы каждому тесту коллекции.
    /// </summary>
    [Fact]
    public async Task Неудавшуюся_работу_следующий_пробует_заново()
    {
        var once = new OncePerKey();
        var runs = 0;
        Task Work() => ++runs == 1 ? Task.FromException(new InvalidOperationException("сбой")) : Task.CompletedTask;

        // Тот, у кого не вышло, получает свой отказ — на непочищенной базе он работать не должен.
        await Assert.ThrowsAsync<InvalidOperationException>(() => once.RunAsync("база", Work));
        Assert.False(once.Done("база"));

        await once.RunAsync("база", Work);

        Assert.Equal(2, runs);
        Assert.True(once.Done("база"));
    }

    // ── Повтор ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Отказ_базы_повторяется_один_раз()
    {
        var runs = 0;
        Task Work() => ++runs == 1 ? Task.FromException(new NpgsqlException("взаимная блокировка")) : Task.CompletedTask;

        await OncePerKey.RetryOnceAsync(Work, TimeSpan.Zero);

        Assert.Equal(2, runs);
    }

    /// <summary>EF заворачивает отказ базы в своё исключение — повтор обязан узнать его и там.</summary>
    [Fact]
    public async Task Отказ_базы_под_обёрткой_тоже_повторяется()
    {
        var runs = 0;
        Task Work() => ++runs == 1
            ? Task.FromException(new InvalidOperationException("transient", new NpgsqlException("оборвано")))
            : Task.CompletedTask;

        await OncePerKey.RetryOnceAsync(Work, TimeSpan.Zero);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Второй_отказ_базы_уже_отказ()
    {
        var runs = 0;
        Task Work() { runs++; return Task.FromException(new NpgsqlException("база недоступна")); }

        await Assert.ThrowsAsync<NpgsqlException>(() => OncePerKey.RetryOnceAsync(Work, TimeSpan.Zero));
        Assert.Equal(2, runs);
    }

    /// <summary>
    /// Наш собственный отказ («фоновые задачи не доработали») не случаен, и повторять его — только
    /// ждать тот же отказ вдвое дольше.
    /// </summary>
    [Fact]
    public async Task Наш_отказ_не_повторяется()
    {
        var runs = 0;
        Task Work() { runs++; return Task.FromException(new InvalidOperationException("задачи не доработали")); }

        await Assert.ThrowsAsync<InvalidOperationException>(() => OncePerKey.RetryOnceAsync(Work, TimeSpan.Zero));
        Assert.Equal(1, runs);
    }
}
