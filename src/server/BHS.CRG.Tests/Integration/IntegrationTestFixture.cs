using BHS.CRG.Application.Common;
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
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<IBlobStorage, FakeBlobStorage>();
        });
    }

    /// <summary>Truncates all domain tables so each test class starts clean.</summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(@"
            TRUNCATE TABLE
                agent_observations,
                reconciliation_aliases,
                reconciliation_findings,
                reconciliation_decisions,
                reconciliation_runs,
                reconciliations,
                material_quality_links,
                quality_audit_runs,
                quality_documents,
                notifications,
                jobs,
                subscriptions,
                document_set_outputs,
                generated_files,
                document_facets,
                domain_objects,
                document_sets,
                sections,
                constructions,
                templates,
                template_assets,
                typst_user_lib,
                document_types,
                catalog_entities,
                primitive_types,
                enum_types,
                dataset_bindings,
                dataset_binding_templates,
                dataset_processing_templates,
                dataset_sources,
                dataset_files
            RESTART IDENTITY CASCADE");
    }
}

[CollectionDefinition("Integration", DisableParallelization = true)]
public class IntegrationCollection : ICollectionFixture<IntegrationTestFixture> { }
