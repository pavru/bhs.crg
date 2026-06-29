using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Templates;

public static class TypstUserLibEndpoints
{
    public static void MapTypstUserLibEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/typst-userlib").RequireAuthorization("Admin");

        g.MapGet("/", async (IRepository<TypstUserLib> repo, CancellationToken ct) =>
        {
            var all = await repo.GetAllAsync(ct);
            var lib = all.FirstOrDefault();
            return Results.Ok(new { content = lib?.Content ?? string.Empty });
        });

        g.MapPut("/", async (SaveTypstUserLibRequest req, IRepository<TypstUserLib> repo, CancellationToken ct) =>
        {
            var all = await repo.GetAllAsync(ct);
            var lib = all.FirstOrDefault();
            if (lib is null)
            {
                lib = TypstUserLib.Create(req.Content);
                await repo.AddAsync(lib, ct);
            }
            else
            {
                lib.UpdateContent(req.Content);
                repo.Update(lib);
            }
            await repo.SaveChangesAsync(ct);
            return Results.Ok(new { content = lib.Content });
        });
    }
}

record SaveTypstUserLibRequest(string Content);
