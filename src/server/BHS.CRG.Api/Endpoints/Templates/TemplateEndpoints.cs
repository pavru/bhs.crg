using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Templates;
using Mediator;

namespace BHS.CRG.Api.Endpoints.Templates;

public static class TemplateEndpoints
{
    public static void MapTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        // Общие ворота допускают редакторов печати. Для конкретного типа или шаблона нижний фильтр
        // сверяет владельца и соответствующее ему право.
        var g = app.MapGroup("/api/templates").RequireAuthorization(TemplateAccess.Policy(app, edit: false));
        var admin = app.MapGroup("/api/templates").RequireAuthorization(TemplateAccess.Policy(app, edit: true));

        admin.MapGet("/types", async (HttpContext http, TemplateAccess access, CancellationToken ct)
            => Results.Ok(await access.TypesAsync(http, ct)));

        // Список — версии без текстов (issue #1972): текст одной версии — адресом ниже.
        g.MapGet("/", async (Guid documentTypeId, IMediator m)
            => Results.Ok(await m.Send(new ListTemplatesQuery(documentTypeId)))).ForType();

        g.MapGet("/{id:guid}/content", async (Guid id, IMediator m)
            => await m.Send(new GetTemplateContentQuery(id)) is { } content
                ? Results.Ok(new { content })
                : Results.NotFound()).ForTemplate();

        // Системная Typst-библиотека (issue #344) — хардкод, только чтение (просмотр на странице шаблонов).
        g.MapGet("/systemlib", () => Results.Ok(new { content = SystemTypstLib.Content }));

        // Собранный typeblocks.typ (issue #770) — только чтение. Не Admin: шаблон пишут против этого
        // файла, а читать его вправе всякий, кто вправе смотреть шаблоны; правка блоков живёт у типа
        // и остаётся под Admin.
        g.MapGet("/typeblocks", async (IMediator m) => Results.Ok(await m.Send(new GetTypeBlocksQuery())));

        // Одноразовый переход на адресацию Код.Имя (issue #773). Только Admin и только явным
        // запуском: команда переписывает пользовательские тексты и заводит новые версии шаблонов.
        // dryRun=true (по умолчанию) считает и показывает план, ничего не записывая.
        if (app.ServiceProvider.GetRequiredService<ModuleRegistry>().IsEnabled("id"))
            admin.MapPost("/migrate-prefixed-addressing", async (bool? dryRun, HttpContext http,
                TemplateAccess access, IMediator m, CancellationToken ct) =>
            {
                var command = new MigrateToPrefixedAddressingCommand(dryRun ?? true, await access.CanEditModuleTemplatesAsync(http));
                try { return Results.Ok(await m.Send(command, ct)); }
                catch (ModuleTemplateMigrationForbidden) { return Results.Forbid(); }
            }).RequireAuthorization(AppPolicies.Permission(TemplateAccess.IdEdit));

        // Сколько активных шаблонов зовёт блоки этого типа по коду (issue #773) — спрашивается
        // перед переименованием кода: оно обрывает вызовы «Код.имя» в их текстах.
        g.MapGet("/code-usage", async (string code, IMediator m)
            => Results.Ok(new { count = await m.Send(new CountTemplatesUsingTypeCodeQuery(code)) }));

        g.MapGet("/active", async (Guid documentTypeId, IMediator m) =>
        {
            var t = await m.Send(new GetActiveTemplateQuery(documentTypeId));
            return t is null ? Results.NotFound() : Results.Ok(t);
        }).ForType();

        admin.MapPost("/", async (CreateTemplateRequest req, IMediator m)
            => Results.Ok(await m.Send(new CreateTemplateCommand(
                req.DocumentTypeId, req.Name, req.Content))))
            .ForType(edit: true, idOf: context => context.GetArgument<CreateTemplateRequest>(0).DocumentTypeId);

        // Простое сохранение (issue #360, Ctrl+S) — правит содержимое активной версии на месте.
        // Отказ (409), если версия историческая (не активна): её можно только форкнуть.
        admin.MapPut("/{id:guid}/content", async (Guid id, UpdateTemplateRequest req, IMediator m) =>
        {
            try { return Results.Ok(await m.Send(new SaveTemplateContentCommand(id, req.Content))); }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).ForTemplate(edit: true);

        // Явное «Сохранить как новую версию» (issue #360) — форк новой версии + опц. примечание.
        // Гонку двух новых версий (#1922) и двух созданий под одним названием (#1924) переводит в 409
        // запись версий (ITemplateVersionWriter, D5) — для всех путей сразу, а не на каждом адресе.
        admin.MapPost("/{id:guid}/versions", async (Guid id, NewVersionRequest req, IMediator m)
            => Results.Ok(await m.Send(new UpdateTemplateCommand(id, req.Content, req.Comment)))).ForTemplate(edit: true);

        admin.MapPost("/{id:guid}/duplicate", async (Guid id, DuplicateTemplateRequest? req, IMediator m)
            => Results.Ok(await m.Send(new DuplicateTemplateCommand(id, req?.Name)))).ForTemplate(edit: true);

        // Использование версий шаблона (issue #364) — сколько документов запиннуто на каждую версию.
        g.MapGet("/usage", async (Guid documentTypeId, IMediator m)
            => Results.Ok(await m.Send(new GetTemplatesUsageQuery(documentTypeId)))).ForType();

        // Удаление версии (issue #364). reassign=true → снять пин у документов (→ дефолт) + сброс в Draft;
        // reassign=false и версия используется → 409 (защита bulk/случайного удаления).
        admin.MapDelete("/{id:guid}", async (Guid id, bool? reassign, IMediator m) =>
        {
            try
            {
                await m.Send(new DeleteTemplateCommand(id, reassign ?? false));
                return Results.NoContent();
            }
            catch (ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).ForTemplate(edit: true);

        admin.MapPut("/{id:guid}/set-default", async (Guid id, IMediator m)
            => Results.Ok(await m.Send(new SetTemplateDefaultCommand(id)))).ForTemplate(edit: true);

        // Объявление параметров шаблона (JSON-массив [{name,label,type,default}] или null).
        admin.MapPut("/{id:guid}/parameters", async (Guid id, UpdateParametersRequest req, IMediator m)
            => Results.Ok(await m.Send(new UpdateTemplateParametersCommand(id, req.Parameters)))).ForTemplate(edit: true);
    }

    record CreateTemplateRequest(Guid DocumentTypeId, string Name, string Content);
    record UpdateTemplateRequest(string Content);
    record NewVersionRequest(string Content, string? Comment);
    record DuplicateTemplateRequest(string? Name);
    record UpdateParametersRequest(string? Parameters);
}
