using System.Text;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Имена баз, которые тесты заводят РЯДОМ с базой прогона: <c>&lt;база прогона&gt;_&lt;суффикс&gt;</c>
/// (issue #1145).
///
/// <para>Зачем одно место. База прогона задаётся переменной <c>BHS_TEST_DB</c> — ею развязаны
/// одновременные прогоны в разных worktree (issue #618). Развязка держится, только пока ВСЕ базы
/// прогона названы от неё, а имя выводилось в шести местах врозь: пять хостов модулей дописывали
/// суффикс сами, шестой — <see cref="MigrationCensusTests" /> — суффикса не дописывал вовсе и
/// заводил базы с прибитыми именами. Два прогона с РАЗНЫМИ базами сносили и создавали друг у друга
/// одну и ту же, и на тесноту отказ не был похож ничем: «57P01: terminating connection due to
/// administrator command» посреди миграции или «23505 … pg_database_datname_index» на создании
/// читаются как поломка самой миграции.</para>
///
/// <para>Чтобы седьмое место не появилось, за исходниками тестов смотрит сторож
/// <c>TestDatabaseNameGuardTests</c>: имя базы строкой в тесте — отказ сборки набора.</para>
///
/// <para>⚠️ Базы прежних, прибитых имён (<c>bhs_crg_census_*</c>, восемь штук) этот код не создаёт и
/// не сносит. На стенде, где гоняли прежние версии, они остались лежать, и убирать их нужно руками —
/// но не раньше, чем прежнего кода не останется ни в одной ветке: прогон на нём заведёт их снова, а
/// снос под идущим прогоном оборвёт его тем же 57P01.</para>
/// </summary>
internal static class TestDatabases
{
    /// <summary>Предел длины идентификатора PostgreSQL (NAMEDATALEN − 1).</summary>
    private const int MaxIdentifierBytes = 63;

    /// <summary>
    /// Сколько байт имени отведено под «_суффикс». Равно самому длинному из нынешних
    /// (<c>_census_lift_down_filled</c>); базе прогона остаётся <see cref="MaxBaseBytes" />.
    ///
    /// Суффиксу длиннее здесь откажут — тогда число поднимают, сознательно ужимая предел для
    /// <c>BHS_TEST_DB</c>.
    /// </summary>
    private const int SuffixBudgetBytes = 24;

    /// <summary>Сколько байт остаётся имени базы прогона.</summary>
    internal const int MaxBaseBytes = MaxIdentifierBytes - SuffixBudgetBytes;

    /// <summary>Имя базы с этим суффиксом — рядом с базой прогона.</summary>
    public static string Name(string suffix) => Compose(BaseName, suffix);

    /// <summary>Строка подключения к базе с этим суффиксом — та же, что у прогона, с другим именем.</summary>
    public static string ConnectionString(string suffix) =>
        new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString)
        {
            Database = Name(suffix),
        }.ConnectionString;

    /// <summary>
    /// Имя как идентификатор SQL: в кавычках, кавычки внутри удвоены.
    ///
    /// Параметром имя базы не передать, а складывается оно из <c>BHS_TEST_DB</c> — то есть в
    /// <c>DROP DATABASE … WITH (FORCE)</c> попадает значение из окружения. Без удвоения кавычка в нём
    /// закрыла бы идентификатор раньше времени, и команда со сносом ушла бы на другое имя.
    /// </summary>
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string BaseName =>
        new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString).Database!;

    /// <summary>
    /// Сложить имя и отказать, если оно не уместится в идентификатор.
    ///
    /// <para>Сама обрезка ничего не ломает, и это проверено на PostgreSQL 18.6: сервер режет имя и в
    /// <c>CREATE DATABASE</c>, и в запросе на подключение, так что база с обрезанным именем находится
    /// и по полному. Ломает обрезка РАЗВЯЗКУ: имена, совпавшие в первых 63 байтах, становятся одной
    /// базой. Два прогона с длинными <c>BHS_TEST_DB</c>, различающимися в хвосте, снова делят базы —
    /// молча (NOTICE никто не читает) и с теми же отказами, от которых это место избавляло.</para>
    ///
    /// <para>Мерится база ПРОГОНА против общего предела, а не готовое имя против 63: иначе при
    /// пограничной длине падали бы только классы с длинным суффиксом, и прогон выглядел бы как
    /// три упавших теста миграции, а не как неверная настройка окружения.</para>
    /// </summary>
    internal static string Compose(string baseName, string suffix)
    {
        if (Encoding.UTF8.GetByteCount(suffix) + 1 > SuffixBudgetBytes)
            throw new InvalidOperationException(
                $"Суффикс базы «{suffix}» не умещается в отведённые {SuffixBudgetBytes} байт (с подчёркиванием). "
                + $"Укоротите его или поднимите {nameof(SuffixBudgetBytes)} — предел для BHS_TEST_DB сократится на столько же.");

        if (Encoding.UTF8.GetByteCount(baseName) > MaxBaseBytes)
            throw new InvalidOperationException(
                $"Имя тестовой базы «{baseName}» длиннее {MaxBaseBytes} байт: вместе с суффиксом оно не уместится "
                + $"в {MaxIdentifierBytes} байта идентификатора PostgreSQL, сервер обрежет его молча, и базы разных "
                + "прогонов могут совпасть. Укоротите BHS_TEST_DB.");

        return baseName + "_" + suffix;
    }
}
