using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Core;

internal static class ConstructionProgressEndpoints
{
    internal static void MapConstructionProgressEndpoints(this IEndpointRouteBuilder app)
    {
        using var scope = app.ServiceProvider.CreateScope();
        var contracts = scope.ServiceProvider.GetRequiredService<IModuleContracts>();
        var permissions = contracts.GetAll<IProgressContribution>()
            .SelectMany(p => p.Contract.AnswerPermissions)
            .Concat(AnswerPermissions(contracts.Get<IActualWorks>(), provider => provider.AnswerPermissions))
            .Concat(AnswerPermissions(contracts.Get<IActualCosts>(), provider => provider.AnswerPermissions))
            .ToArray();

        var route = app.MapGet("/api/constructions/{id:guid}/progress", ReadAsync)
            .Produces<ConstructionProgressResponse>().Produces(StatusCodes.Status404NotFound);
        if (permissions.Length > 0)
            route.RequireAuthorization(AppPolicies.AnyPermission(permissions));
        else
            route.RequireAuthorization(policy => policy.RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement([])));
    }

    private static IReadOnlyList<string> AnswerPermissions<T>(
        ModuleContractAnswer<T> answer, Func<T, IReadOnlyList<string>> permissionOf) where T : class =>
        answer is ModuleContractAnswer<T>.Connected connected ? permissionOf(connected.Contract) : [];

    private static async Task<IResult> ReadAsync(
        Guid id, [FromQuery(Name = "section")] Guid? sectionId, DateOnly? laborMonth, AppDbContext db, IModuleReaders readers,
        IModuleContracts contracts, IModulePlanItems items, IModuleWorkTypes workTypes, IModuleUnits units,
        CancellationToken ct)
    {
        if (laborMonth is { Day: not 1 })
            return Results.BadRequest(new { error = "Назовите первое число месяца трудозатрат (laborMonth)." });

        if (!await db.Constructions.AnyAsync(
                site => site.Id == id && (sectionId == null || site.Sections.Any(section => section.Id == sectionId)), ct))
            return Results.NotFound();

        var reader = await readers.ForRequestAsync(ct);
        var progress = new ConstructionProgress(contracts, items, workTypes, units);
        return Results.Ok(await progress.ReadAsync(reader, id, sectionId, laborMonth, ct));
    }
}
