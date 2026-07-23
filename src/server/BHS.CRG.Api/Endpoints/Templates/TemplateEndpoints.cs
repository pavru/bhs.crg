using BHS.CRG.Application.Generation;
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

        // Системная Typst-библиотека (issue #344) — хардкод, только чтение (просмотр на странице шаблонов).
        g.MapGet("/systemlib", () => Results.Ok(new { content = SystemTypstLib.Content }));

        g.MapGet("/active", async (Guid documentTypeId, IMediator m) =>
        {
            var t = await m.Send(new GetActiveTemplateQuery(documentTypeId));
            return t is null ? Results.NotFound() : Results.Ok(t);
        });

        admin.MapPost("/", async (CreateTemplateRequest req, IMediator m)
            => Results.Ok(await m.Send(new CreateTemplateCommand(
                req.DocumentTypeId, req.Name, req.Content))));

        // Простое сохранение (issue #360, Ctrl+S) — правит содержимое активной версии на месте.
        // Отказ (409), если версия историческая (не активна): её можно только форкнуть.
        admin.MapPut("/{id:guid}/content", async (Guid id, UpdateTemplateRequest req, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new SaveTemplateContentCommand(id, req.Content))); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        // Явное «Сохранить как новую версию» (issue #360) — форк новой версии + опц. примечание.
        admin.MapPost("/{id:guid}/versions", async (Guid id, NewVersionRequest req, IMediator m)
            => Results.Ok(await m.Send(new UpdateTemplateCommand(id, req.Content, req.Comment))));

        admin.MapPost("/{id:guid}/duplicate", async (Guid id, DuplicateTemplateRequest? req, IMediator m)
            => Results.Ok(await m.Send(new DuplicateTemplateCommand(id, req?.Name))));

        admin.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeleteTemplateCommand(id));
            return Results.NoContent();
        });

        admin.MapPut("/{id:guid}/set-default", async (Guid id, IMediator m)
            => Results.Ok(await m.Send(new SetTemplateDefaultCommand(id))));

        // Объявление параметров шаблона (JSON-массив [{name,label,type,default}] или null).
        admin.MapPut("/{id:guid}/parameters", async (Guid id, UpdateParametersRequest req, IMediator m)
            => Results.Ok(await m.Send(new UpdateTemplateParametersCommand(id, req.Parameters))));
    }

    record CreateTemplateRequest(Guid DocumentTypeId, string Name, string Content);
    record UpdateTemplateRequest(string Content);
    record NewVersionRequest(string Content, string? Comment);
    record DuplicateTemplateRequest(string? Name);
    record UpdateParametersRequest(string? Parameters);
}
