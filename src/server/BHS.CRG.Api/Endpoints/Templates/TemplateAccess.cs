using BHS.CRG.Api.Auth;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Printing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Templates;

internal sealed class TemplateAccess(
    AppDbContext db, ModuleRegistry modules, ModulePrintFormCatalog forms, IAuthorizationService auth)
{
    private const string IdRead = "id.document.read";
    internal const string IdEdit = "id.config.edit";

    public static string Policy(IEndpointRouteBuilder app, bool edit)
    {
        var modules = app.ServiceProvider.GetRequiredService<ModuleRegistry>();
        return modules.IsEnabled("id")
            ? AppPolicies.AnyPermission(CorePermissions.PrintEdit, edit ? IdEdit : IdRead, IdEdit)
            : AppPolicies.Permission(CorePermissions.PrintEdit);
    }

    public async Task<IReadOnlyList<DocumentType>> TypesAsync(HttpContext http, CancellationToken ct)
    {
        var idEditing = modules.IsEnabled("id") && await HasAsync(http, IdEdit);
        var printing = await HasAsync(http, CorePermissions.PrintEdit);
        var offered = printing ? forms.All.Where(f => modules.IsEnabled(f.Module.Code))
            .ToDictionary(f => f.Form.TypeCode, f => f.Module.Code) : [];
        var codes = offered.Keys.ToArray();
        var types = await db.DocumentTypes.AsNoTracking()
            .Where(t => !t.IsAbstract && ((idEditing && (t.Module == "id" || t.Module == TypeOwner.Core)
                && t.Kind == DocumentTypeKind.Document) || codes.Contains(t.Code)))
            .OrderBy(t => t.Name).ToListAsync(ct);
        return types.Where(t => IsDocumentOwner(t.Module) || offered.GetValueOrDefault(t.Code) == t.Module).ToList();
    }

    public async Task<IResult?> ForTypeAsync(HttpContext http, Guid id, bool edit, CancellationToken ct)
    {
        var owner = await db.DocumentTypes.Where(t => t.Id == id).Select(t => t.Module).SingleOrDefaultAsync(ct);
        return await ForOwnerAsync(http, owner, edit);
    }

    public async Task<IResult?> ForTemplateAsync(HttpContext http, Guid id, bool edit, CancellationToken ct)
    {
        var owner = await (from template in db.Templates
                           join type in db.DocumentTypes on template.DocumentTypeId equals type.Id
                           where template.Id == id
                           select type.Module).SingleOrDefaultAsync(ct);
        return await ForOwnerAsync(http, owner, edit);
    }

    public Task<IResult?> ForAssetScopeAsync(HttpContext http, TemplateAssetScope scope, Guid? id, bool edit,
        CancellationToken ct) => (scope, id) switch
    {
        (TemplateAssetScope.System, null) => Task.FromResult<IResult?>(null),
        (TemplateAssetScope.Template, { } value) => ForTemplateAsync(http, value, edit, ct),
        (TemplateAssetScope.DocumentType, { } value) => ForTypeAsync(http, value, edit, ct),
        _ => Task.FromResult<IResult?>(Results.BadRequest(new { error = "Некорректная область ассета." })),
    };

    public async Task<IResult?> ForAssetAsync(HttpContext http, Guid id, CancellationToken ct)
    {
        var asset = await db.TemplateAssets.Where(a => a.Id == id).Select(a => new { a.Scope, a.ScopeId })
            .SingleOrDefaultAsync(ct);
        return asset is null ? Results.NotFound() : await ForAssetScopeAsync(http, asset.Scope, asset.ScopeId, true, ct);
    }

    public Task<bool> CanEditModuleTemplatesAsync(HttpContext http) => HasAsync(http, CorePermissions.PrintEdit);

    private async Task<IResult?> ForOwnerAsync(HttpContext http, string? owner, bool edit)
    {
        if (owner is null) return Results.NotFound();
        if (IsDocumentOwner(owner))
        {
            if (!modules.IsEnabled("id")) return Results.Forbid();
            return await HasAsync(http, edit ? IdEdit : IdRead) || (!edit && await HasAsync(http, IdEdit))
                ? null : Results.Forbid();
        }

        return modules.IsEnabled(owner) && await HasAsync(http, CorePermissions.PrintEdit) ? null : Results.Forbid();
    }

    private async Task<bool> HasAsync(HttpContext http, string permission) =>
        (await auth.AuthorizeAsync(http.User, AppPolicies.Permission(permission))).Succeeded;

    private static bool IsDocumentOwner(string owner) => owner == "id" || TypeOwner.IsCore(owner);
}

internal static class TemplateAccessFilters
{
    public static RouteHandlerBuilder ForType(this RouteHandlerBuilder route, bool edit = false,
        Func<EndpointFilterInvocationContext, Guid>? idOf = null) => route.AddEndpointFilter(async (context, next) =>
    {
        var http = context.HttpContext;
        var access = http.RequestServices.GetRequiredService<TemplateAccess>();
        var result = await access.ForTypeAsync(http, idOf?.Invoke(context) ?? context.GetArgument<Guid>(0), edit, http.RequestAborted);
        return result ?? await next(context);
    });

    public static RouteHandlerBuilder ForTemplate(this RouteHandlerBuilder route, bool edit = false) =>
        route.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var access = http.RequestServices.GetRequiredService<TemplateAccess>();
            var result = await access.ForTemplateAsync(http, context.GetArgument<Guid>(0), edit, http.RequestAborted);
            return result ?? await next(context);
        });
}
