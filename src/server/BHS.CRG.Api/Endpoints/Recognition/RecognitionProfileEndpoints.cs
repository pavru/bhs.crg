using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Infrastructure.DataSets;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Recognition;

public static class RecognitionProfileEndpoints
{
    public static void MapRecognitionProfileEndpoints(this IEndpointRouteBuilder app)
    {
        // Чтение доступно всем аутентифицированным (профиль выбирается при распознавании),
        // запись — только Admin, как и прочая конфигурация системы.
        var g = app.MapGroup("/api/recognition-profiles").RequireAuthorization(AppPolicies.Permission(CorePermissions.DataSetsRead));
        var admin = app.MapGroup("/api/recognition-profiles").RequireAuthorization(AppPolicies.Permission(CorePermissions.RecognitionSettings));

        g.MapGet("/", async (IMediator m) =>
            Results.Ok(await m.Send(new ListRecognitionProfilesQuery())));

        g.MapGet("/kinds", async (IMediator m) =>
            Results.Ok(await m.Send(new ListRecognitionKindsQuery())));

        // Профили выключенных модулей: только имя и владелец (issue #1075). Нужны экрану, чтобы
        // назвать причину — «скрыто столько-то» и «привязан профиль выключенного модуля».
        g.MapGet("/hidden", async (IMediator m) =>
            Results.Ok(await m.Send(new ListHiddenRecognitionProfilesQuery())));

        // Что предложить в диалоге «Распознать PDF» (issue #1075). Перечень раньше был зашит в
        // клиенте и расходился с воротами записи: выбор, который сервер отвергнет, предлагать нельзя.
        g.MapGet("/pdf", (RecognitionProfileCatalog catalog) =>
            Results.Ok(PdfProfileRegistry.Offered(catalog.IsAvailable)));

        admin.MapPost("/", async (ProfileRequest req, IMediator m) =>
        {
            try
            {
                return Results.Ok(await m.Send(new CreateRecognitionProfileCommand(
                    req.Name, req.Kind ?? "", req.Fields ?? [], req.RowColumns ?? [], req.Shape)));
            }
            catch (InvalidRequestException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        admin.MapPut("/{id:guid}", async (Guid id, ProfileRequest req, IMediator m) =>
        {
            try
            {
                return Results.Ok(await m.Send(new UpdateRecognitionProfileCommand(
                    id, req.Name, req.Fields ?? [], req.RowColumns ?? [], req.Shape)));
            }
            catch (InvalidRequestException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        admin.MapPost("/{id:guid}/reset", async (Guid id, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new ResetRecognitionProfileCommand(id))); }
            catch (InvalidRequestException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        admin.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            try { await m.Send(new DeleteRecognitionProfileCommand(id)); return Results.NoContent(); }
            catch (InvalidRequestException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
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
