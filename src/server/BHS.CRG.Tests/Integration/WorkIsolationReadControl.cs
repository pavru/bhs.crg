using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Integration;

/// <summary>Контроль чтения в отдельных тестовых экземплярах. Профили назначаются после подготовки данных.</summary>
internal sealed class WorkIsolationReadControl
{
    private sealed record Readers(Guid Installer, Guid Unassigned, ModuleReach Sites);

    private Readers? readers;

    internal void Start(Guid installer, Guid unassigned, Guid siteA, Guid siteB) =>
        readers = new(installer, unassigned, ModuleReach.OfSites([siteA, siteB]));

    internal void Stop() => readers = null;

    internal void Register(IServiceCollection services)
    {
        services.RemoveAll<IOwnReportReader>();
        services.AddScoped<IOwnReportReader>(sp => new Own(this,
            ActivatorUtilities.CreateInstance<OwnReportReader>(sp), sp.GetRequiredService<IModuleUser>()));
        services.RemoveAll<IModuleReaders>();
        services.AddScoped<IModuleReaders>(sp => new Work(this,
            ActivatorUtilities.CreateInstance<ModuleReadersPort>(sp), sp.GetRequiredService<IHttpContextAccessor>()));
        services.RemoveAll<IRequestDataAccessResolver>();
        services.AddSingleton<IRequestDataAccessResolver>(sp => new Snapshots(this,
            sp.GetRequiredService<DataAccessResolver>(), sp.GetRequiredService<IHttpContextAccessor>()));
    }

    private sealed class Own(WorkIsolationReadControl control, OwnReportReader original, IModuleUser user) : IOwnReportReader
    {
        public async Task<ReportViewer> ViewerAsync(CancellationToken ct)
        {
            var viewer = await original.ViewerAsync(ct);
            return control.readers is { } selected && user.Id == selected.Installer
                ? ReportViewer.Of(selected.Sites, viewer.EmployeeId)
                : viewer;
        }
    }

    private sealed class Work(WorkIsolationReadControl control, ModuleReadersPort original, IHttpContextAccessor http) : IModuleReaders
    {
        public async Task<ModuleReader> ForRequestAsync(CancellationToken ct = default)
        {
            var originalReader = await original.ForRequestAsync(ct);
            var request = http.HttpContext?.Request;
            if (control.readers is not { } selected || originalReader.UserId is not { } id || request is null
                || !HttpMethods.IsGet(request.Method)) return originalReader;

            var path = request.Path;
            if (id == selected.Installer && (path.StartsWithSegments("/api/work/photos")
                    || path.StartsWithSegments("/api/work/reports") && path.Value!.EndsWith("/journal", StringComparison.Ordinal)))
                return ModuleReader.Of(id, originalReader.Who, [.. originalReader.Keys, WorkModule.ReportRead]);

            if (id == selected.Unassigned && (path.StartsWithSegments("/api/work/entry")
                    || path.StartsWithSegments("/api/work/review") || path.StartsWithSegments("/api/work/summary")
                    || path.StartsWithSegments("/api/work/crew-groups") || path.StartsWithSegments("/api/work/labor")))
                return ModuleReader.Of(id, originalReader.Who, [.. originalReader.Keys, WorkModule.SitesAll]);

            return originalReader;
        }

        public Task<ModuleReader> ForJobAsync(ModuleJobRun run, CancellationToken ct = default) => original.ForJobAsync(run, ct);

        public ModuleReader ForSystem(string reason) => original.ForSystem(reason);
    }

    private sealed class Snapshots(WorkIsolationReadControl control, DataAccessResolver original, IHttpContextAccessor http)
        : IRequestDataAccessResolver
    {
        public async Task<DataAccess> ForAsync(ClaimsPrincipal principal, CancellationToken ct)
        {
            var access = await original.ForAsync(principal, ct);
            var request = http.HttpContext?.Request;
            if (control.readers is not { } selected || access.UserId is not { } id || request is null
                || id != selected.Installer && id != selected.Unassigned) return access;

            var path = request.Path;
            var table = path.StartsWithSegments("/api/tables/work.reports", out var suffix)
                        || path.StartsWithSegments("/api/tables/work.lines", out suffix);
            var tableRead = table && (HttpMethods.IsGet(request.Method) || HttpMethods.IsPost(request.Method)
                && suffix.Value is "/query" or "/export");
            var datasetRead = HttpMethods.IsGet(request.Method) && path.StartsWithSegments("/api/datasets");
            return tableRead || datasetRead || path == "/mcp"
                ? DataAccess.Of(id, access.Who, [.. access.Keys, WorkModule.ReportRead], access.EnabledModules)
                : access;
        }
    }
}
