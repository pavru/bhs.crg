using BHS.CRG.Application.Recognition;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Recognition;

public static class RecognitionProfileEndpoints
{
    public static void MapRecognitionProfileEndpoints(this IEndpointRouteBuilder app)
    {
        // Чтение доступно всем аутентифицированным (профиль выбирается при распознавании),
        // запись — только Admin, как и прочая конфигурация системы.
        var g = app.MapGroup("/api/recognition-profiles").RequireAuthorization();
        var admin = app.MapGroup("/api/recognition-profiles").RequireAuthorization("Admin");

        g.MapGet("/", async (IMediator m) =>
            Results.Ok(await m.Send(new ListRecognitionProfilesQuery())));

        g.MapGet("/kinds", async (IMediator m) =>
            Results.Ok(await m.Send(new ListRecognitionKindsQuery())));

        admin.MapPost("/", async (ProfileRequest req, IMediator m) =>
        {
            try
            {
                return Results.Ok(await m.Send(new CreateRecognitionProfileCommand(
                    req.Name, req.Kind ?? "", req.Fields ?? [], req.RowColumns ?? [], req.Shape)));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        admin.MapPut("/{id:guid}", async (Guid id, ProfileRequest req, IMediator m) =>
        {
            try
            {
                return Results.Ok(await m.Send(new UpdateRecognitionProfileCommand(
                    id, req.Name, req.Fields ?? [], req.RowColumns ?? [], req.Shape)));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        admin.MapPost("/{id:guid}/reset", async (Guid id, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new ResetRecognitionProfileCommand(id))); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        admin.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            try { await m.Send(new DeleteRecognitionProfileCommand(id)); return Results.NoContent(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

    /// <summary>Вид (Kind) читается только при создании — при правке он игнорируется намеренно:
    /// вид выбирает применяемый промпт, его смена сделала бы профиль другой сущностью.</summary>
    record ProfileRequest(
        string Name,
        string? Kind,
        IReadOnlyList<RecognitionProfileField>? Fields,
        IReadOnlyList<RecognitionProfileField>? RowColumns,
        RecognitionTableShape? Shape);
}
