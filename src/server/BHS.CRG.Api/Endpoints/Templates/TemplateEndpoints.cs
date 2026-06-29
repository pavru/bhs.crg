using BHS.CRG.Application.Templates;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Templates;

public static class TemplateEndpoints
{
    public static void MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/templates").RequireAuthorization();
        var admin = app.MapGroup("/api/templates").RequireAuthorization("Admin");

        g.MapGet("/", async (Guid documentTypeId, IMediator m)
            => Results.Ok(await m.Send(new ListTemplatesQuery(documentTypeId))));

        g.MapGet("/active", async (Guid documentTypeId, IMediator m) =>
        {
            var t = await m.Send(new GetActiveTemplateQuery(documentTypeId));
            return t is null ? Results.NotFound() : Results.Ok(t);
        });

        admin.MapPost("/", async (CreateTemplateRequest req, IMediator m)
            => Results.Ok(await m.Send(new CreateTemplateCommand(
                req.DocumentTypeId, req.Name, req.Content))));

        admin.MapPut("/{id:guid}", async (Guid id, UpdateTemplateRequest req, IMediator m)
            => Results.Ok(await m.Send(new UpdateTemplateCommand(id, req.Content))));

        admin.MapPost("/{id:guid}/duplicate", async (Guid id, DuplicateTemplateRequest? req, IMediator m)
            => Results.Ok(await m.Send(new DuplicateTemplateCommand(id, req?.Name))));

        admin.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeleteTemplateCommand(id));
            return Results.NoContent();
        });

        admin.MapPut("/{id:guid}/settings", async (Guid id, UpdateSettingsRequest req, IMediator m)
            => Results.Ok(await m.Send(new UpdateTemplateSettingsCommand(
                id, req.PageSize, req.PageOrientation,
                req.MarginTop, req.MarginRight, req.MarginBottom, req.MarginLeft))));

        admin.MapPut("/{id:guid}/set-default", async (Guid id, IMediator m)
            => Results.Ok(await m.Send(new SetTemplateDefaultCommand(id))));
    }

    record CreateTemplateRequest(Guid DocumentTypeId, string Name, string Content);
    record UpdateTemplateRequest(string Content);
    record DuplicateTemplateRequest(string? Name);
    record UpdateSettingsRequest(string PageSize, string PageOrientation, int MarginTop, int MarginRight, int MarginBottom, int MarginLeft);
}
