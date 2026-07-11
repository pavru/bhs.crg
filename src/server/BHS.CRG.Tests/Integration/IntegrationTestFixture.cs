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
    internal const string TestConnectionString =
        "Host=localhost;Port=5432;Database=bhs_crg_test;Username=postgres;Password=xxsystem";

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
                subscriptions,
                document_set_outputs,
                generated_files,
                document_instances,
                document_sets,
                sections,
                constructions,
                templates,
                document_types,
                catalog_entities,
                common_data_entries,
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
