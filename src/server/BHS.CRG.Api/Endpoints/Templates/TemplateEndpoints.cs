using BHS.CRG.Application.Templates;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Templates;

public static class TemplateEndpoints
{
    public static void MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/templates").RequireAuthorization();

        g.MapGet("/", async (Guid documentTypeId, IMediator m)
            => Results.Ok(await m.Send(new ListTemplatesQuery(documentTypeId))));

        g.MapGet("/active", async (Guid documentTypeId, IMediator m) =>
        {
            var t = await m.Send(new GetActiveTemplateQuery(documentTypeId));
            return t is null ? Results.NotFound() : Results.Ok(t);
        });

        g.MapPost("/", async (CreateTemplateRequest req, IMediator m)
            => Results.Ok(await m.Send(new CreateTemplateCommand(
                req.DocumentTypeId, req.Name, req.Content))));

        g.MapPut("/{id:guid}", async (Guid id, UpdateTemplateRequest req, IMediator m)
            => Results.Ok(await m.Send(new UpdateTemplateCommand(id, req.Content))));

        g.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeleteTemplateCommand(id));
            return Results.NoContent();
        });

        g.MapPut("/{id:guid}/settings", async (Guid id, UpdateSettingsRequest req, IMediator m)
            => Results.Ok(await m.Send(new UpdateTemplateSettingsCommand(
                id, req.PageSize, req.PageOrientation,
                req.MarginTop, req.MarginRight, req.MarginBottom, req.MarginLeft))));

        g.MapPut("/{id:guid}/set-default", async (Guid id, IMediator m)
            => Results.Ok(await m.Send(new SetTemplateDefaultCommand(id))));
    }

    record CreateTemplateRequest(Guid DocumentTypeId, string Name, string Content);
    record UpdateTemplateRequest(string Content);
    record UpdateSettingsRequest(string PageSize, string PageOrientation, int MarginTop, int MarginRight, int MarginBottom, int MarginLeft);
}
