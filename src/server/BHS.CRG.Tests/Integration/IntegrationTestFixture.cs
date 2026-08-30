using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
    /// </summary>
    /// Порт 5433, а не 5432 (issue #894): база стенда работает в контейнере, а 5432 может быть
    /// занят нативной службой PostgreSQL на машине разработчика. Строка подключения у них
    /// одинаковая, поэтому совпадение портов означало бы тесты, молча ушедшие в чужую базу.
    /// В ci.yml сервисный контейнер публикует тот же 5433 — значение одно на все окружения.
    internal static readonly string TestConnectionString =
        "Host=localhost;Port=5433;Username=postgres;Password=xxsystem;Database="
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
    ];

    /// <summary>Truncates all domain tables so each test class starts clean.</summary>
    public async Task ResetDatabaseAsync()
    {
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
