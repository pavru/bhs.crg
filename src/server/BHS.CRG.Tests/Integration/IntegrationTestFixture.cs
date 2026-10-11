using BHS.CRG.Application.Common;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Jobs;
using BHS.CRG.Modules;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Shared factory for all integration tests.
/// Starts the ASP.NET Core host once, pointing at the bhs_crg_test database.
/// MinIO is replaced with FakeBlobStorage so tests don't need Docker.
/// </summary>
public class IntegrationTestFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>
    /// Клиент теста. Тот же, что даёт фабрика (переходы и куки), и ещё одно: правка счёта называет его
    /// версию сама — см. <see cref="SeenInvoiceVersion" /> (issue #1176).
    ///
    /// <para>Здесь, а не в базовом классе тестов счёта: счёт правят проверки десятка классов (реестр,
    /// таблицы модуля, занятые записи, журнал), и клиент каждый из них создаёт по-своему. Подставлять
    /// версию в каждом значило бы однажды её забыть — и получить отказ, который читается как дефект.</para>
    ///
    /// <para>⚠️ Метод базового класса не виртуален, и это СОКРЫТИЕ: клиент, взятый у производной фабрики
    /// (<c>WithWebHostBuilder(...)</c>) или через базовый тип, обработчика не получит. Такому клиенту,
    /// если он правит счёт, обработчик дают явно — <see cref="SeenInvoiceVersion.ClientOf" />.</para>
    /// </summary>
    public new HttpClient CreateClient() => SeenInvoiceVersion.ClientOf(this);

    /// <summary>
    /// Имя тестовой БД — из переменной окружения <c>BHS_TEST_DB</c>, по умолчанию прежнее (issue #618).
    ///
    /// Разработка идёт в нескольких worktree одновременно, и прогоны в них пересекаются. База была
    /// одна на всех, а <see cref="ResetDatabaseAsync" /> делает TRUNCATE всех таблиц перед каждым
    /// классом — то есть чужой прогон вычищает данные у идущего. Падения при этом выглядят как
    /// настоящие дефекты («Construction not found», «тип с кодом AOSR уже существует», нарушения
    /// внешнего ключа), и каждый раз приходится доказывать, что упало не от твоей правки.
    ///
    /// Создавать базу вручную не нужно: приложение мигрирует при старте, а миграция создаёт БД.
    ///
    /// Порт 5433, а не 5432 (issue #894): база стенда работает в контейнере, а 5432 может быть
    /// занят нативной службой PostgreSQL на машине разработчика. Строка подключения у них
    /// одинаковая, поэтому совпадение портов означало бы тесты, молча ушедшие в чужую базу.
    /// В ci.yml сервисный контейнер публикует тот же 5433 — значение одно на все окружения.
    ///
    /// ⚠️ Адрес — 127.0.0.1, а НЕ localhost (issue #1142). Имя разрешается сначала в <c>::1</c>, а
    /// порт контейнера по IPv6 на машине разработчика может не отвечать вовсе: не отказом, а
    /// молчанием. Npgsql делит таймаут между адресами и идёт по ним по очереди, поэтому КАЖДОЕ новое
    /// физическое подключение ждало по семь секунд — а новое оно у каждого поднятого хоста. Замер на
    /// одном и том же наборе из трёх классов: 107 с с именем против 16 с с адресом. Снаружи это не
    /// видно никак: тесты зелёные, просто медленные.
    /// </summary>
    internal static readonly string TestConnectionString =
        // Include Error Detail — чтобы отказ базы называл, что именно сцепилось: без него взаимная
        // блокировка из #928 пришла с «Detail redacted», и обе стороны пришлось вычислять по журналу.
        "Host=127.0.0.1;Port=5433;Username=postgres;Password=xxsystem;Include Error Detail=true;Database="
        + (Environment.GetEnvironmentVariable("BHS_TEST_DB") is { Length: > 0 } db ? db : "bhs_crg_test");

    /// <summary>
    /// С чего обязано начинаться имя тестовой базы — и умолчание выше, и всё, что приходит из
    /// <c>BHS_TEST_DB</c> (issue #1142). База дев-стенда называется <c>bhs_crg</c> и стоит на том же
    /// порту, что и тестовые: имя отличает их одним суффиксом, а прогон сносит в своей базе всё.
    /// Проверяет <see cref="TestRunDatabase.EnsureTestName" />.
    ///
    /// Записано рядом с умолчанием нарочно: это единственный файл тестов, где имя базы вправе стоять
    /// строкой (<c>TestDatabaseNameGuardTests</c>), а правило и умолчание обязаны сходиться.
    /// </summary>
    internal const string TestDatabasePrefix = "bhs_crg_";

    /// <summary>
    /// Значения, которые тестовый хост обязан назвать сам: без них приложение отказывается
    /// стартовать (<c>JwtKeyGuard</c>, <c>StorageConfigGuard</c>).
    /// </summary>
    private static readonly Dictionary<string, string?> TestSettings = new()
    {
        ["ConnectionStrings:Postgres"] = TestConnectionString,
        ["Jwt:Key"] = "integration-tests-only-signing-key-8b31d0c47f2a",
        // Хранилище всё равно подменено (см. ниже) — эти значения только проходят проверку старта.
        ["BlobStorage:AccessKey"] = "integration-tests",
        ["BlobStorage:SecretKey"] = "integration-tests",
        ["BlobStorage:Bucket"] = "integration-tests",
        // Плановое копирование (issue #832) под тестовым хостом НЕ поднимаем. Расписание включено
        // по умолчанию, и через две минуты прогона служба сняла бы копию тестовой базы в каталог
        // рядом с исходниками — а её экспорт читал бы базу ровно тогда, когда соседний класс делает
        // TRUNCATE. Прогон целиком укладывается примерно в те же две минуты, то есть встретились бы
        // мы с этим не сразу и не там.
        ["Backup:SchedulerEnabled"] = "false",
        // Проверка обновлений и мониторинг — то же самое (issue #928): обе службы пишут в базу по
        // своему расписанию. Ни один тест на них не опирается; сами службы в контейнере остаются.
        ["Updates:CheckerEnabled"] = "false",
        ["Health:MonitorEnabled"] = "false",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting, а НЕ только ConfigureAppConfiguration. Разница не стилистическая: проверки
        // конфигурации стоят в Program.cs верхнеуровневыми операторами, то есть до builder.Build(),
        // а слой ConfigureAppConfiguration подмешивается на Build — и до проверок не доходит вовсе.
        // Пока этого не заметили, набор держался на appsettings.Development.json: среду
        // WebApplicationFactory берёт по умолчанию, и значения приезжали оттуда. Проверено прямо —
        // с обнулённой секцией BlobStorage в dev-файле хост переставал стартовать, хотя фикстура
        // «задавала» ключи. То есть страховка была нарисованной.
        foreach (var (key, value) in TestSettings) builder.UseSetting(key, value);

        // Слой на Build оставляем тоже: до него доходит всё остальное приложение (строку подключения
        // читает AddDbContext, ключ подписи — выдача токенов), и порядок слоёв тут значения не имеет.
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(TestSettings));

        builder.ConfigureServices(services =>
        {
            // Подменяем только САМО хранилище, не обёртку над ним (issue #672). Если зарегистрировать
            // подделку как IBlobStorage напрямую, тесты пойдут мимо реестра — то есть мимо проверки
            // выдачи, ради которой он заведён, — и весь набор будет зелёным на конвейере, которого в
            // бою нет. Тем, кому нужна сама подделка (проверить, что блоб удалён), она доступна по
            // своему типу.
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<FakeBlobStorage>();
            services.AddSingleton<IBlobStorage>(sp => new RegisteredBlobStorage(
                sp.GetRequiredService<FakeBlobStorage>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<RegisteredBlobStorage>>()));

            // Пределы частоты входа — только в тестовом хосте (issue #947). Боевая настройка НЕ
            // трогается, и ручки для её ослабления в приложении не заводится.
            //
            // Зачем. Предел «30 входов за 5 минут» считается по адресу клиента, а под тестовым
            // хостом адрес один на весь прогон: тридцать входов делятся между ВСЕМИ тестами набора.
            // Прогон подошёл к потолку вплотную, и тест, добавивший вход, ронял не себя, а соседей —
            // они получали 429 просто потому, что шли следом. Ищут причину при этом в соседях, а
            // записывают её в «набор нестабильный».
            //
            // ⚠️ Именно RemoveAll, а не повторный AddPolicy с тем же именем: AddPolicy на занятое
            // имя БРОСАЕТ ArgumentException, а не заменяет политику. Ошибка при этом вылезает не
            // там, где сделана: конвейер приложения перестаёт собираться целиком, и падает каждый
            // запрос каждого теста (проверено — набор шёл 16 минут вместо трёх).
            //
            // ⚠️ Сам ограничитель после этого в тестах не проверяется — и не проверялся раньше:
            // теста на него нет ни одного. Появится — ему нужен свой хост с боевыми пределами,
            // иначе он будет зелёным, ничего не проверяя.
            //
            // ⚠️ Список имён ДУБЛИРУЕТ приложение и расходится с ним молча: политика, о которой
            // здесь не знают, даёт 500 «no such policy exists» в каждом тесте, который трогает её
            // адрес. Наступили на это с политикой branding (ревью PR #1061), поэтому расхождение
            // теперь ловит RateLimitPolicyCoverageTests — в обе стороны.
            services.RemoveAll<IConfigureOptions<RateLimiterOptions>>();
            services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                foreach (var policy in (string[])["login", "auth", "refresh", "bug-report", "branding"])
                    o.AddPolicy(policy, _ => RateLimitPartition.GetNoLimiter("tests"));
            });
        });
    }

    /// <summary>
    /// Таблицы, очищаемые перед каждым классом тестов. Список — не украшение, а решение: всё, чего
    /// в нём нет, переживает прогон и достаётся следующему классу. Против расхождения списка с
    /// моделью стоит <see cref="FixtureResetCoverageTests" /> — он требует, чтобы каждая таблица
    /// была либо здесь, либо в его списке исключений с причиной.
    /// </summary>
    internal static readonly string[] TruncatedTables =
    [
        "blob_registry",
        "renditions",
        "agent_observations",
        "reconciliation_aliases",
        "reconciliation_findings",
        "reconciliation_decisions",
        "reconciliation_runs",
        "reconciliations",
        "material_quality_links",
        "quality_audit_runs",
        "quality_documents",
        "bug_reports",
        "notification_user_states",
        "notifications",
        "jobs",
        "subscriptions",
        "document_set_outputs",
        "generated_files",
        "document_facets",
        "domain_objects",
        "document_set_plans",
        "work_plan_items",
        "document_sets",
        "sections",
        "constructions",
        "templates",
        "template_assets",
        "typst_user_lib",
        "typst_user_lib_files",
        "document_types",
        "catalog_entities",
        "primitive_types",
        "enum_types",
        "dataset_bindings",
        "dataset_binding_templates",
        "dataset_processing_templates",
        "dataset_sources",
        "dataset_files",
        "integration_settings",
        "service_state",
        // Настройки экземпляра (issue #960): часовой пояс компании. Очищаем — тест, поменявший
        // пояс, иначе оставил бы его следующему, и сутки у соседа считались бы по чужому поясу.
        "app_settings",
        "activity_log",
        // Закрытия периода (issue #1081) — состояние ВСЕГО экземпляра: оставленное соседу, оно
        // закрыло бы ему месяц, в который тот пишет.
        "period_closures",
        // Предпочтения пользователей (issue #953). Учётные записи переживают класс тестов нарочно,
        // а их настройки — нет: тест, записавший тему, иначе достался бы следующему, и «настройки
        // пусты у нового пользователя» перестало бы проверяться.
        "user_settings",
    ];

    /// <summary>Сколько ждать, пока доработают фоновые задачи прошлого теста.</summary>
    private static readonly TimeSpan BackgroundJobsDeadline = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Ждёт, пока доработают фоновые задачи, поставленные прошлым тестом (issue #928).
    ///
    /// Тесты, запускающие сборку и распознавание по HTTP, ставят настоящие задачи, и те дорабатывают
    /// в фоне сами по себе — уже после конца теста. Очистка базы следующим тестом встречалась с ними:
    /// в CI это дало взаимную блокировку TRUNCATE, а там, где блокировки не случалось, задача
    /// дописывала строки прошлого теста в чистую базу следующего.
    ///
    /// Не дождались — падаем, а не чистим поверх: зелёный прогон на базе с чужими строками хуже
    /// честного отказа. Предел с запасом: задачи в тестах идут секунды.
    /// </summary>
    private async Task WaitForBackgroundJobsAsync()
    {
        var queue = Services.GetRequiredService<JobQueue>();
        var deadline = DateTime.UtcNow + BackgroundJobsDeadline;
        while (!queue.IsIdle)
        {
            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException(
                    $"Фоновые задачи прошлого теста не доработали за {BackgroundJobsDeadline.TotalSeconds:0} с — " +
                    "очистка базы поверх них перемешала бы данные тестов. Проверьте, не зависла ли задача.");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// Таблицы из <see cref="TruncatedTables" />, в которые с прошлой очистки что-то писали
    /// (issue #1164).
    ///
    /// <para>Зачем выбирать. Очистка идёт перед КАЖДЫМ тестом — экземпляр класса xUnit создаёт на
    /// тест, — а <c>TRUNCATE</c> платит не за строки, а за отношения: каждой таблице, её индексам и
    /// TOAST заводится новый файл. У сорока двух таблиц отношений 175, и это 60 мс на очистку —
    /// 57 секунд на прогон, хотя тест обычно трогает три-четыре таблицы.</para>
    ///
    /// <para>Признак — РАЗМЕР, а не «есть ли строки». У усечённой таблицы файл пуст, и первая же
    /// вставка выделяет страницу; обратно страница не отдаётся ни удалением строк, ни откатом
    /// (отдать её может только VACUUM, а после него мёртвых версий в таблице уже нет). То есть
    /// нулевой размер означает «усекать нечего», и повторное усечение такой таблицы не меняет
    /// ничего. Спроси мы вместо этого про строки, таблица с удалёнными
    /// строками осталась бы неусечённой — с мёртвыми версиями в куче, от которых зависит порядок
    /// выдачи без сортировки, а на нём тесты уже ловили настоящие дефекты (issue #1149).</para>
    ///
    /// <para>Вторая половина условия — про секционированные таблицы: у родителя своего файла нет,
    /// данные лежат в секциях, и размер самого родителя всегда нулевой. Сегодня таких в списке нет,
    /// но появись одна — она молча перестала бы очищаться, а сторож списка остался бы зелёным:
    /// имя-то в списке есть. У обычной таблицы дерево секций пусто, и вторая половина ничего не
    /// добавляет.</para>
    /// </summary>
    /// <param name="tables">Что проверять; по умолчанию — <see cref="TruncatedTables" />.</param>
    internal static async Task<List<string>> TouchedTablesAsync(AppDbContext db, string[]? tables = null) =>
        await db.Database
            .SqlQueryRaw<string>(
                """
                SELECT t AS "Value" FROM unnest({0}) AS t
                WHERE pg_relation_size(format('%I', t)::regclass) > 0
                   OR (SELECT coalesce(sum(pg_relation_size(part.relid)), 0)
                       FROM pg_partition_tree(format('%I', t)::regclass) AS part) > 0
                """,
                (object)(tables ?? TruncatedTables))
            .ToListAsync();

    /// <summary>Truncates all domain tables so each test class starts clean.</summary>
    public async Task ResetDatabaseAsync()
    {
        await WaitForBackgroundJobsAsync();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Имена берём в кавычки: сейчас список весь в нижнем регистре, но часть таблиц модели
        // названа как RefreshTokens, и первое же такое имя, добавленное сюда как есть, Postgres
        // свернёт в refreshtokens — фикстура упадёт до первого теста с «relation does not exist».
        //
        // EF1003 — про склейку значений в SQL. Здесь склеиваются имена таблиц, а имя таблицы
        // параметром не передашь; список выше — константа в коде тестов, снаружи в него не попасть.
        static string Quoted(IEnumerable<string> names) => string.Join(", ", names.Select(t => $"\"{t}\""));

        // Замок на ВСЕ таблицы списка, и только под ним — вопрос «какие тронуты». Раньше барьером
        // был сам TRUNCATE всех таблиц: он дожидался транзакции, которую не закрыл прошлый тест, и
        // сносил её строки. Усекая только тронутые без замка, мы бы этот барьер потеряли: запись,
        // пришедшая между вопросом и усечением — или в прогон, где усекать оказалось нечего, —
        // досталась бы следующему тесту. Замок с вопросом стоят 2,6 мс против 60 у усечения всех
        // таблиц: файлов замок не заводит. Сторож — FixtureResetTouchedTests.
        await using var tx = await db.Database.BeginTransactionAsync();
#pragma warning disable EF1003
        await db.Database.ExecuteSqlRawAsync(
            "LOCK TABLE " + Quoted(TruncatedTables) + " IN ACCESS EXCLUSIVE MODE");

        // Усекаем только тронутые — см. TouchedTablesAsync. CASCADE при этом дотягивается до тех же
        // таблиц, что и раньше: вне списка на таблицы списка не ссылается никто (иначе нынешняя
        // очистка сносила бы «оставленное нарочно»), а внутри списка нетронутая таблица пуста и так.
        var touched = await TouchedTablesAsync(db);
        if (touched.Count > 0)
            await db.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE " + Quoted(touched) + " RESTART IDENTITY CASCADE");
#pragma warning restore EF1003
        await tx.CommitAsync();

        await ResetModuleSchemasAsync(scope);

        // Настройки интеграций живут ещё и в памяти. Без сброса кеша очистка таблицы даёт ложное
        // чувство изоляции: строки нет, а следующий класс продолжает видеть чужую почту и ключи.
        scope.ServiceProvider.GetRequiredService<IIntegrationSettings>().Invalidate();
    }

    /// <summary>
    /// Схемы включённых модулей — по модели их контекстов (задача A2b этапа 2, issue #1073).
    ///
    /// <para>Список таблиц здесь НЕ ведётся, в отличие от <see cref="TruncatedTables" />, и это не
    /// поблажка: ядро не знает состава схемы модуля и узнать его может только у контекста. Список
    /// пришлось бы править каждой миграцией модуля, а забытая строка выглядела бы как плавающий тест
    /// — тот самый случай, от которого <see cref="FixtureResetCoverageTests" /> и защищает. Решение
    /// «этот контекст чистится целиком» записано там же, где решения по таблицам ядра: в карте
    /// <c>FixtureResetCoverageTests.ContextCoverage</c>, и мета-сторож
    /// (<c>ModuleDbContextInventoryTests</c>) требует записи на КАЖДЫЙ контекст.</para>
    ///
    /// <para>Сегодня у модуля <c>costs</c> таблиц нет, и вызов ничего не делает. Это и есть причина
    /// завести его сейчас: с первой таблицей счетов (C1, #1076) забытая очистка проявилась бы
    /// падением ЧУЖОГО теста со второго прогона — способом, при котором ищут не там.</para>
    /// </summary>
    internal static async Task ResetModuleSchemasAsync(IServiceScope scope)
    {
        var registry = scope.ServiceProvider.GetRequiredService<ModuleRegistry>();

        foreach (var module in registry.Enabled)
        {
            if (module.Schema is not { } schema) continue;

            var (db, ours) = ModuleSchemaMigrator.Resolve(scope.ServiceProvider, module.Code, schema);
            try
            {
                var tables = db.Model.GetRelationalModel().Tables
                    .Select(t => $"\"{t.Schema}\".\"{t.Name}\"")
                    .ToList();
                if (tables.Count == 0) continue;

#pragma warning disable EF1003 // склеиваются имена таблиц из модели, а не значения: см. выше
                await db.Database.ExecuteSqlRawAsync(
                    "TRUNCATE TABLE " + string.Join(", ", tables) + " RESTART IDENTITY CASCADE");
#pragma warning restore EF1003
            }
            finally
            {
                if (ours) await db.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// База этого хоста. Наследник со своей базой называет её здесь — и той же строкой переопределяет
    /// подключение в <c>ConfigureWebHost</c>.
    ///
    /// <para>Отдельным свойством, потому что знать базу надо ДО старта хоста: её занимают и чистят
    /// раньше, чем приложение начнёт в неё писать. Спросить у конфигурации значило бы поднять хост.
    /// Расхождение объявленного с настоящим ловит <see cref="EnsureRunsOnDeclaredDatabase" /> — иначе
    /// наследник, забывший это свойство, чистил бы общую базу, а свою копил бы молча.</para>
    /// </summary>
    protected virtual string HostConnectionString => TestConnectionString;

    /// <summary>По одной очистке на БАЗУ за процесс.</summary>
    private static readonly OncePerKey RunResets = new();

    /// <summary>Сколько ждать, пока базу отпустит доживающий процесс прошлого прогона.</summary>
    protected static readonly TimeSpan ClaimPatience = TimeSpan.FromSeconds(15);

    /// <summary>
    /// База чистится один раз, перед первым тестом прогона (issue #1142). xUnit зовёт это у фикстуры
    /// сам — до первого теста и до посева, который идёт в <c>InitializeAsync</c> самих классов.
    ///
    /// <para>Зачем. Между классами чистится не всё: учётные записи, роли и сессии живут дольше класса
    /// (<c>FixtureResetCoverageTests.DeliberatelyKept</c>), а классы хостов счетов базу не сбрасывают
    /// вовсе — отделяют свои строки меткой и за собой не убирают. Без очистки раз за прогон всё это
    /// растёт от прогона к прогону, и только на машине разработчика: в CI база каждый раз свежая. За
    /// несколько десятков прогонов у хоста строк счёта набралось 4262 счёта, 4081 учётная запись и 603
    /// источника на одной таблице, а в общей базе лежало 1634 роли и 7239 сессий. Запрос списка
    /// наборов перестал укладываться в сто секунд, и падал тест, который ни в чём не виноват.</para>
    ///
    /// <para>⚠️ Стоит это в самом хосте и у ВСЕХ хостов, а не у двух, на которых нашлось: новый хост
    /// со своей базой иначе снова копил бы молча, а вопрос «кто убирает» не возникает, пока база
    /// маленькая.</para>
    ///
    /// <para>⚠️ Второй прогон на той же базе не начнётся: базу держит первый
    /// (<see cref="TestRunDatabase.ClaimAsync" />). Развязка — своё имя базы в <c>BHS_TEST_DB</c>
    /// (issue #618).</para>
    /// </summary>
    public async Task InitializeAsync()
    {
        await ResetOncePerRunAsync();
        EnsureRunsOnDeclaredDatabase();
    }

    // Явно: у WebApplicationFactory уже есть DisposeAsync с другим возвращаемым типом. Гасит хост
    // по-прежнему Dispose — xUnit зовёт его следом.
    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Очистка базы ОДИН РАЗ ЗА ПРОГОН: первый спросивший чистит, остальные получают ту же задачу.
    ///
    /// <para>⚠️ Именно раз за процесс, а не на экземпляр хоста. Экземпляр xUnit создаёт на каждый
    /// класс, а посев у классов хостов счетов статический и случается однажды: очистка перед вторым
    /// классом снесла бы организации и номенклатуру, на которые уже указывают статические поля, — и
    /// падали бы не те тесты, что чистили. Сторож — <c>RunResetTests</c>.</para>
    ///
    /// <para>Хост при этом не поднимается, пока очистка не дошла до своей второй половины: ключ —
    /// объявленная база, а не то, что ответит конфигурация.</para>
    /// </summary>
    internal Task ResetOncePerRunAsync() =>
        RunResets.RunAsync(TestRunDatabase.KeyOf(HostConnectionString), ResetForRunAsync);

    /// <summary>Очистка этого прогона у базы хоста уже прошла — для сторожей, не для тестов.</summary>
    internal bool CleanedThisRun => RunResets.Done(TestRunDatabase.KeyOf(HostConnectionString));

    /// <summary>
    /// Привести базу к виду «приложение только что поднялось на пустой» — в два приёма, и порядок
    /// здесь главное.
    ///
    /// <para>ДО старта хоста: занять базу и снести учётные таблицы — всё, включая роли. Системные
    /// роли с составом прав вернёт сам старт, и перечислять их здесь не надо: список «что создаёт
    /// старт» разошёлся бы с приложением на первой роли, заведённой мимо него.</para>
    ///
    /// <para>ПОСЛЕ старта: то же, что между классами (<see cref="ResetDatabaseAsync" />), — список
    /// таблиц и схемы модулей берутся у поднятого приложения. Справочник прав и встроенные профили
    /// распознавания при этом остаются (<c>FixtureResetCoverageTests.DeliberatelyKept</c>). Типы
    /// модулей сносятся вместе с остальными типами, и возвращать их не нужно: посев классов заводит
    /// «Организацию» и повторяет проекцию сам — на свежей базе в CI при старте их тоже нет.</para>
    /// </summary>
    private async Task ResetForRunAsync()
    {
        await TestRunDatabase.ClaimAsync(HostConnectionString, ClaimPatience);
        await TestRunDatabase.ClearIdentityAsync(HostConnectionString);

        // Первое обращение к службам поднимает хост — уже на вычищенных учётных таблицах.
        EnsureRunsOnDeclaredDatabase();
        await OncePerKey.RetryOnceAsync(ResetDatabaseAsync, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Хост работает на той базе, которую объявил. Иначе занята и вычищена была бы одна база, а
    /// тесты шли бы на другой — никем не занятой и никогда не чищенной.
    /// </summary>
    private void EnsureRunsOnDeclaredDatabase()
    {
        var declared = TestRunDatabase.KeyOf(HostConnectionString);
        var actual = TestRunDatabase.KeyOf(
            Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")!);
        if (declared == actual) return;

        throw new InvalidOperationException(
            $"{GetType().Name} объявил базу «{declared}», а работает на «{actual}». Переопределите " +
            $"{nameof(HostConnectionString)} той же строкой, что подставлена в ConfigureWebHost: по ней " +
            "базу занимают и чистят перед прогоном.");
    }
}

[CollectionDefinition("Integration", DisableParallelization = true)]
public class IntegrationCollection : ICollectionFixture<IntegrationTestFixture> { }
