using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    internal static readonly string TestConnectionString =
        "Host=localhost;Port=5432;Username=postgres;Password=xxsystem;Database="
        + (Environment.GetEnvironmentVariable("BHS_TEST_DB") is { Length: > 0 } db ? db : "bhs_crg_test");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = TestConnectionString,
                // Свой ключ подписи: приложение отказывается стартовать без заданного (JwtKeyGuard),
                // и это правильно — значит и тестовый хост обязан назвать свой, а не молча
                // пользоваться значением из репозитория.
                ["Jwt:Key"] = "integration-tests-only-signing-key-8b31d0c47f2a",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<IBlobStorage, FakeBlobStorage>();
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
        "agent_observations",
        "reconciliation_aliases",
        "reconciliation_findings",
        "reconciliation_decisions",
        "reconciliation_runs",
        "reconciliations",
        "material_quality_links",
        "quality_audit_runs",
        "quality_documents",
        "notifications",
        "jobs",
        "subscriptions",
        "document_set_outputs",
        "generated_files",
        "document_facets",
        "domain_objects",
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
    ];

    /// <summary>Truncates all domain tables so each test class starts clean.</summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // EF1003 — про склейку значений в SQL. Здесь склеиваются имена таблиц, а имя таблицы
        // параметром не передашь; список выше — константа в коде тестов, снаружи в него не попасть.
#pragma warning disable EF1003
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE " + string.Join(", ", TruncatedTables) + " RESTART IDENTITY CASCADE");
#pragma warning restore EF1003

        // Настройки интеграций живут ещё и в памяти. Без сброса кеша очистка таблицы даёт ложное
        // чувство изоляции: строки нет, а следующий класс продолжает видеть чужую почту и ключи.
        scope.ServiceProvider.GetRequiredService<IIntegrationSettings>().Invalidate();
    }
}

[CollectionDefinition("Integration", DisableParallelization = true)]
public class IntegrationCollection : ICollectionFixture<IntegrationTestFixture> { }
