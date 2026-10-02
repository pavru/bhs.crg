using System.Text.RegularExpressions;
using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Базы, которые тесты заводят сами, названы ОТ базы прогона — и никак иначе (issue #1145).
///
/// <para>Зачем сторож, а не договорённость. База с прибитым именем ломается только при соседе: один
/// прогон зелёный и у разработчика, и в CI, а два одновременных с разными <c>BHS_TEST_DB</c> сносят
/// её друг у друга. То есть дефект не виден ни одной проверкой, пока не случится, — а случившись,
/// выглядит поломкой миграции («57P01», «23505 … pg_database_datname_index»). Так прожил
/// <c>MigrationCensusTests</c> с девятью именами; следующий тест со своей базой повторил бы это
/// первой же строкой.</para>
///
/// <para>Смотрит сторож в ИСХОДНИКИ тестов: в запущенном наборе прибитое имя ничем не отличается от
/// выведенного.</para>
/// </summary>
public class TestDatabaseNameGuardTests
{
    /// <summary>Единственное место, где имя базы вправе стоять строкой: умолчание для <c>BHS_TEST_DB</c>.</summary>
    private const string DefaultNameHome = "IntegrationTestFixture.cs";

    // ── Сам помощник ─────────────────────────────────────────────────────────

    [Fact]
    public void Имя_складывается_из_базы_прогона_и_суффикса() =>
        Assert.Equal("bhs_crg_wtv_census_lift", TestDatabases.Compose("bhs_crg_wtv", "census_lift"));

    [Fact]
    public void Самый_длинный_суффикс_и_база_на_пределе_умещаются_в_идентификатор()
    {
        var name = TestDatabases.Compose(new string('a', TestDatabases.MaxBaseBytes), "census_lift_down_filled");
        Assert.Equal(63, name.Length);
    }

    /// <summary>
    /// Отказ обязан зависеть от БАЗЫ, а не от суффикса: иначе при пограничной длине падали бы только
    /// классы с длинным суффиксом — три теста из десяти вместо одной понятной причины.
    /// </summary>
    [Theory]
    [InlineData("costs")]
    [InlineData("census_lift_down_filled")]
    public void Длинная_база_прогона_отвергается_при_любом_суффиксе(string suffix)
    {
        var tooLong = new string('a', TestDatabases.MaxBaseBytes + 1);
        var error = Assert.Throws<InvalidOperationException>(() => TestDatabases.Compose(tooLong, suffix));
        Assert.Contains("BHS_TEST_DB", error.Message);
    }

    /// <summary>Предел сервера — в байтах: кириллическое имя вдвое короче латинского.</summary>
    [Fact]
    public void Длина_считается_в_байтах_а_не_в_знаках()
    {
        var cyrillic = new string('я', TestDatabases.MaxBaseBytes / 2 + 1);
        Assert.Throws<InvalidOperationException>(() => TestDatabases.Compose(cyrillic, "costs"));
    }

    [Fact]
    public void Суффикс_длиннее_отведённого_отвергается_по_имени_причины()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => TestDatabases.Compose("bhs_crg_test", "census_lift_down_filled_x"));
        Assert.Contains("Суффикс", error.Message);
    }

    [Fact]
    public void Кавычка_в_имени_удваивается_а_не_закрывает_идентификатор() =>
        Assert.Equal("\"a\"\"; DROP\"", TestDatabases.Quote("a\"; DROP"));

    // ── Исходники тестов ─────────────────────────────────────────────────────

    [Fact]
    public void Имя_базы_строкой_в_тестах_не_пишется()
    {
        var found = Sources()
            .Where(file => Path.GetFileName(file) != DefaultNameHome)
            .SelectMany(file => Offences(file, HardcodedNames))
            .ToList();

        Assert.True(found.Count == 0,
            "Имя базы записано строкой — от BHS_TEST_DB оно не зависит, и одновременные прогоны будут "
            + "делить эту базу. Берите имя у TestDatabases.Name(\"суффикс\"):\n" + string.Join("\n", found));
    }

    [Fact]
    public void База_создаётся_и_сносится_только_по_имени_из_помощника()
    {
        var found = Sources().SelectMany(file => Offences(file, UnquotedDatabaseCommands)).ToList();

        Assert.True(found.Count == 0,
            "CREATE/DROP DATABASE с именем не из TestDatabases.Quote(…): имя либо прибито, либо попадает в "
            + "команду без удвоения кавычек.\n" + string.Join("\n", found));
    }

    /// <summary>
    /// Правила ОБЯЗАНЫ срабатывать: зелёный сторож, который ничего не ловит, хуже отсутствующего.
    /// Образцы — те самые строки, что стояли в коде до #1145.
    /// </summary>
    [Theory]
    [InlineData("await using var db = await CreateDatabaseAsync(\"bhs_crg_census_fresh\");")]
    [InlineData("var name = \"bhs_crg_census_absent\";")]
    public void Прибитое_имя_ловится(string line) => Assert.NotEmpty(HardcodedNames([line]));

    [Theory]
    [InlineData("new NpgsqlCommand($\"DROP DATABASE IF EXISTS \\\"{name}\\\" WITH (FORCE)\", conn);")]
    [InlineData("new NpgsqlCommand($\"CREATE DATABASE \\\"{name}\\\"\", conn);")]
    [InlineData("Execute(conn, \"create database scratch\");")]
    public void Команда_мимо_помощника_ловится(string line) => Assert.NotEmpty(UnquotedDatabaseCommands([line]));

    [Theory]
    [InlineData("new NpgsqlCommand($\"CREATE DATABASE {TestDatabases.Quote(name)}\", conn);")]
    [InlineData("$\"DROP DATABASE IF EXISTS {TestDatabases.Quote(name)} WITH (FORCE)\", conn);")]
    [InlineData("// соединение обрывает чужой DROP DATABASE bhs_crg_test WITH (FORCE)")]
    [InlineData("/// <c>\"bhs_crg_census_fresh\"</c> — так было до #1145")]
    public void Команда_через_помощник_и_комментарий_не_ловятся(string line)
    {
        Assert.Empty(UnquotedDatabaseCommands([line]));
        Assert.Empty(HardcodedNames([line]));
    }

    // ── Правила ──────────────────────────────────────────────────────────────

    /// <summary>Строковый литерал, начинающийся с имени базы проекта.</summary>
    private static readonly Regex HardcodedName = new("\"bhs_crg_", RegexOptions.Compiled);

    /// <summary>
    /// CREATE/DROP DATABASE, за которым стоит НЕ подстановка из помощника.
    ///
    /// Группы атомарные (<c>(?&gt;…)</c>) нарочно: с обычным «IF EXISTS» по желанию движок, не найдя
    /// совпадения, отступал — пропускал «IF EXISTS» и находил нарушение в правильной строке.
    /// </summary>
    private static readonly Regex UnquotedCommand = new(
        @"\b(CREATE|DROP)\s+DATABASE(?>\s+)(?>(IF\s+EXISTS\s+)?)(?!\{TestDatabases\.Quote\()",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static IEnumerable<int> HardcodedNames(IReadOnlyList<string> lines) => Matching(lines, HardcodedName);

    private static IEnumerable<int> UnquotedDatabaseCommands(IReadOnlyList<string> lines) =>
        Matching(lines, UnquotedCommand);

    /// <summary>Номера строк кода (с единицы), на которых сработало правило. Комментарии не в счёт.</summary>
    private static IEnumerable<int> Matching(IReadOnlyList<string> lines, Regex rule) =>
        Enumerable.Range(0, lines.Count)
            .Where(i => !lines[i].TrimStart().StartsWith("//") && rule.IsMatch(lines[i]))
            .Select(i => i + 1);

    private static IEnumerable<string> Offences(string file, Func<IReadOnlyList<string>, IEnumerable<int>> rule) =>
        rule(File.ReadAllLines(file)).Select(line => $"  {Path.GetRelativePath(TestsRoot, file)}:{line}");

    /// <summary>Исходники тестового проекта, кроме этого файла: образцы нарушений лежат здесь же.</summary>
    private static IEnumerable<string> Sources()
    {
        var files = Directory.EnumerateFiles(TestsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/"))
            .Where(f => Path.GetFileName(f) != nameof(TestDatabaseNameGuardTests) + ".cs")
            .ToList();

        // Пустой список означал бы сторожа, который зелёный, потому что смотреть не на что.
        Assert.True(files.Count > 100, $"Исходников тестов найдено {files.Count} — сторож смотрит не туда: {TestsRoot}");
        return files;
    }

    private static string TestsRoot { get; } = FindTestsRoot();

    private static string FindTestsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BHS.CRG.Tests.csproj")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Не найден каталог тестового проекта (BHS.CRG.Tests.csproj) выше " + AppContext.BaseDirectory +
                " — сторож читает исходники тестов, и без них проверять нечего.");
    }
}
