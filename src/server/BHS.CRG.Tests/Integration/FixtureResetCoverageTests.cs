using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Каждая таблица модели должна быть ЛИБО в списке очистки фикстуры, ЛИБО названа здесь с причиной.
///
/// Тест написан против дрейфа, а не ради нынешнего состава. Пропуск в списке не виден никак: он
/// проявляется падением ЧУЖОГО теста со второго прогона — данные предыдущего класса достаются
/// следующему. Выглядит это как дефект в упавшем тесте, его правят на месте, а корень остаётся.
/// Так было трижды: <c>integration_settings</c>, <c>typst_user_lib_files</c>,
/// <c>recognition_profiles</c>.
///
/// Приём тот же, что у <see cref="BackupManifestCoverageTests" />: связать «добавили таблицу» с
/// «примите решение», пока решение ещё дёшево.
/// </summary>
[Collection("Integration")]
public class FixtureResetCoverageTests(IntegrationTestFixture fixture)
{
    /// <summary>
    /// Таблицы, которые между классами тестов сознательно НЕ чистятся, и почему. Добавляя строку,
    /// вы принимаете решение — именно этого тест и добивается.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyKept = new()
    {
        // Встроенные профили создаёт сидер, и только при старте хоста. TRUNCATE снёс бы их на весь
        // оставшийся прогон, а на них рассчитывают тесты распознавания. Тесты, которым мешают
        // ПОЛЬЗОВАТЕЛЬСКИЕ профили, убирают их у себя точечно.
        ["recognition_profiles"] = "конфигурация: встроенные профили сидер создаёт один раз при старте",

        // Учётные записи заводятся тестами по мере надобности и живут дольше одного класса;
        // очистка выбила бы и пользователей, зарегистрированных для проверок авторизации.
        ["AspNetUsers"] = "Identity: учётные записи, чистятся точечно теми, кто их заводит",
        ["AspNetRoles"] = "Identity: роли создаёт приложение при старте, TRUNCATE их не вернёт",
        ["AspNetUserRoles"] = "Identity: связь пользователь-роль",
        ["AspNetUserClaims"] = "Identity",
        ["AspNetUserLogins"] = "Identity",
        ["AspNetUserTokens"] = "Identity",
        ["AspNetRoleClaims"] = "Identity",
        ["RefreshTokens"] = "Identity: сессии, уходят вместе с пользователями",
    };

    [Fact]
    public void EveryTable_IsEitherTruncated_OrExplicitlyKept()
    {
        var undecided = TableNames()
            .Where(t => !IntegrationTestFixture.TruncatedTables.Contains(t) && !DeliberatelyKept.ContainsKey(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(undecided.Count == 0,
            "В модели появились таблицы, про которые не принято решение об очистке между тестами: " +
            string.Join(", ", undecided) + ".\n" +
            "Добавьте каждую ЛИБО в IntegrationTestFixture.TruncatedTables, ЛИБО в DeliberatelyKept " +
            "с причиной. Молча оставлять нельзя: пропуск проявится падением чужого теста со второго " +
            "прогона, и искать будут не там.");
    }

    /// <summary>
    /// Таблица не может быть и очищаемой, и сознательно оставленной: проверка «либо там, либо там»
    /// пропускает такую пару молча. А случается она буднично — кто-то гонится за плавающим тестом,
    /// добавляет таблицу в очистку и не убирает прежнюю строку с причиной. Причина остаётся в файле
    /// как записанное решение, хотя действует уже обратное.
    /// </summary>
    [Fact]
    public void NoTable_IsBothTruncatedAndKept()
    {
        var both = IntegrationTestFixture.TruncatedTables
            .Where(DeliberatelyKept.ContainsKey)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(both.Count == 0,
            "Таблицы названы сразу в обоих списках: " + string.Join(", ", both) + ".\n" +
            "Очистка сильнее записанной причины — уберите строку из DeliberatelyKept, если решение " +
            "изменилось, или из TruncatedTables, если нет.");
    }

    /// <summary>
    /// Обратная сторона: имя в списке, за которым нет таблицы, — след переименования. TRUNCATE такой
    /// список не переживёт, но упадёт он в фикстуре, до первого теста, и причина будет неочевидна.
    /// </summary>
    [Fact]
    public void ListedNames_StillExistInModel()
    {
        var tables = TableNames().ToHashSet(StringComparer.Ordinal);

        var vanished = IntegrationTestFixture.TruncatedTables.Concat(DeliberatelyKept.Keys)
            .Where(t => !tables.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.True(vanished.Count == 0,
            "В списках названы таблицы, которых в модели больше нет: " + string.Join(", ", vanished));
    }

    private IEnumerable<string> TableNames()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
