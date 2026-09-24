using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Jobs;
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
public class IntegrationTestFixture : WebApplicationFactory<Program>
{
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
    /// </summary>
    internal static readonly string TestConnectionString =
        // Include Error Detail — чтобы отказ базы называл, что именно сцепилось: без него взаимная
        // блокировка из #928 пришла с «Detail redacted», и обе стороны пришлось вычислять по журналу.
        "Host=localhost;Port=5433;Username=postgres;Password=xxsystem;Include Error Detail=true;Database="
        + (Environment.GetEnvironmentVariable("BHS_TEST_DB") is { Length: > 0 } db ? db : "bhs_crg_test");

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
            services.RemoveAll<IConfigureOptions<RateLimiterOptions>>();
            services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                foreach (var policy in (string[])["login", "auth", "refresh", "bug-report"])
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
#pragma warning disable EF1003
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE " + string.Join(", ", TruncatedTables.Select(t => $"\"{t}\""))
            + " RESTART IDENTITY CASCADE");
#pragma warning restore EF1003

        // Настройки интеграций живут ещё и в памяти. Без сброса кеша очистка таблицы даёт ложное
        // чувство изоляции: строки нет, а следующий класс продолжает видеть чужую почту и ключи.
        scope.ServiceProvider.GetRequiredService<IIntegrationSettings>().Invalidate();
    }
}

[CollectionDefinition("Integration", DisableParallelization = true)]
public class IntegrationCollection : ICollectionFixture<IntegrationTestFixture> { }
