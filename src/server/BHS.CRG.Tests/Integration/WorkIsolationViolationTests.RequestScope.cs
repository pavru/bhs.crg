using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Modules.Work;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Integration;

internal sealed partial class WorkIsolationScenario
{
    internal async Task CheckRestrictedReaderAsync()
    {
        var (installer, owner, manager, unassigned, executive) = await SeedAsync();
        using (installer)
        using (owner)
        using (manager)
        using (unassigned)
        using (executive)
        {
            var (_, _, full) = await ReaderAsync("Руководитель с правом приёмки без назначений",
                WorkModule.ReportRead, WorkModule.ReportReview);
            using var fullReader = full;
            await using var restrictedHost = testHost.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRequestDataAccessResolver>();
                services.AddSingleton<IRequestDataAccessResolver>(sp =>
                    new WithoutExecutiveRead(sp.GetRequiredService<DataAccessResolver>()));
            }));
            using var restricted = restrictedHost.CreateClient();
            restricted.DefaultRequestHeaders.Authorization = fullReader.DefaultRequestHeaders.Authorization;

            // Один токен, одни исходные данные и те же проверки HTTP и MCP.
            await CheckTablesAsync(installer, restricted, fullReader);
            await CheckDataSetsAsync(installer, restricted, fullReader);
        }
    }

    private sealed class WithoutExecutiveRead(DataAccessResolver original) : IRequestDataAccessResolver
    {
        public async Task<DataAccess> ForAsync(ClaimsPrincipal principal, CancellationToken ct)
        {
            var access = await original.ForAsync(principal, ct);
            if (access.UserId is not { } userId || !access.Allows(WorkModule.ReportRead))
                return access;
            return DataAccess.Of(userId, access.Who,
                access.Keys.Where(key => key != WorkModule.ReportRead), access.EnabledModules);
        }
    }
}
