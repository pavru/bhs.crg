using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Generation;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Printing;
using MediatR;
using Microsoft.AspNetCore.Authorization;

namespace BHS.CRG.Api.Endpoints.Generation;

public static class RecordPrintEndpoints
{
    public static void MapRecordPrintEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/print/{typeCode}/{recordId:guid}").RequireAuthorization();
        // WritesNothing: оба POST строят PDF в памяти, без сохранения записи или GeneratedFile.
        group.MapPost("", (string typeCode, Guid recordId, Guid? templateId, HttpContext http,
            RecordPrintEndpoint print, CancellationToken ct) => print.RunAsync(http, typeCode, recordId, templateId, download: true, debug: false, ct));
        group.MapPost("/preview", (string typeCode, Guid recordId, Guid? templateId, HttpContext http,
            RecordPrintEndpoint print, CancellationToken ct) => print.RunAsync(http, typeCode, recordId, templateId, download: false, debug: false, ct));
        group.MapGet("/debug-bundle", (string typeCode, Guid recordId, Guid? templateId, HttpContext http,
            RecordPrintEndpoint print, CancellationToken ct) => print.RunAsync(http, typeCode, recordId, templateId, download: true, debug: true, ct))
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.PrintEdit));
    }
}

public sealed class RecordPrintEndpoint(
    ModulePrintFormCatalog forms,
    ModuleRegistry modules,
    IAuthorizationService authorization,
    IServiceProvider services,
    IModuleUser user,
    IMediator mediator)
{
    public async Task<IResult> RunAsync(HttpContext http, string typeCode, Guid recordId, Guid? templateId,
        bool download, bool debug, CancellationToken ct)
    {
        var entry = forms.Find(typeCode);
        if (entry is null) return Results.NotFound();
        if (!modules.IsEnabled(entry.Module.Code))
            return Results.Json(new { error = $"Модуль «{entry.Module.Title}» не подключён на этом экземпляре.", module = entry.Module.Code },
                statusCode: StatusCodes.Status501NotImplemented);

        if (!(await authorization.AuthorizeAsync(http.User, AppPolicies.Module(entry.Module.Code))).Succeeded
            || !(await authorization.AuthorizeAsync(http.User, AppPolicies.Permission(entry.Form.ReadPermission))).Succeeded)
            return Results.Forbid();

        var provider = services.GetRequiredKeyedService<IModulePrintContext>(entry.Form.TypeCode);
        var context = await provider.BuildAsync(recordId, user, ct);
        if (context is ModulePrintResult.Missing) return Results.NotFound();
        if (context is ModulePrintResult.Forbidden) return Results.Forbid();
        var data = ((ModulePrintResult.Found)context).Data;
        var bytes = await mediator.Send(new PrintRecordCommand(entry.Form.TypeCode, entry.Module.Code, templateId, data, debug), ct);

        http.Response.Headers.CacheControl = "no-store";
        if (debug) return Results.File(bytes, "application/zip", $"typst-debug-{recordId}.zip");
        return Results.File(bytes, "application/pdf", download ? $"{entry.Form.TypeCode}-{recordId}.pdf" : null);
    }
}
