using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Endpoints.Users;

/// <summary>
/// Редактор матрицы ролей (ТЗ AUTH-5, AUTH-5.1, issue #951).
///
/// Ворота — то же право, что и на пользователях: <c>core.users.manage</c> объявлено как «заводить
/// пользователей, назначать им роли, менять состав прав роли». Отдельное право на правку ролей
/// означало бы, что доступ можно раздавать, не умея его настроить, — разделение без смысла.
/// </summary>
public static class RoleEndpoints
{
    public static void MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/roles")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.UsersManage));

        g.MapGet("/", async (RoleEditor editor) => Results.Ok(await editor.ListAsync()));

        // Справочник прав с объяснениями — отдельным адресом, а не внутри списка ролей: он один на
        // все роли и не меняется, пока не сменится состав модулей.
        g.MapGet("/permissions", (RoleEditor editor, ModuleRegistry modules) =>
            Results.Ok(editor.PermissionGroups(modules)));

        g.MapPost("/", async (RoleRequest req, RoleEditor editor, CancellationToken ct) =>
            Answer(await editor.CreateAsync(req.Title, req.Summary, req.Permissions, ct)));

        g.MapPut("/{name}", async (string name, RoleRequest req, RoleEditor editor, CancellationToken ct) =>
            Answer(await editor.RenameAsync(name, req.Title, req.Summary, ct)));

        g.MapPut("/{name}/permissions", async (
                string name, PermissionsRequest req, RoleEditor editor, CancellationToken ct) =>
            Answer(await editor.SetPermissionsAsync(name, req.Permissions, ct)));

        g.MapDelete("/{name}", async (string name, RoleEditor editor, CancellationToken ct) =>
            Answer(await editor.DeleteAsync(name, ct)));
    }

    /// <summary>
    /// Отказ уходит кодом и текстом, а не исключением: причина отказа здесь всегда адресована
    /// человеку («нельзя снять право…»), и показать её обязан тот же экран, где он нажал кнопку.
    /// </summary>
    private static IResult Answer(RoleResult result) => result switch
    {
        { Error: not null } => Results.Json(new { error = result.Error }, statusCode: result.Status),
        { Status: StatusCodes.Status204NoContent } => Results.NoContent(),
        _ => Results.Ok(result.Role),
    };

    private record RoleRequest(string? Title, string? Summary, string[]? Permissions);
    private record PermissionsRequest(string[]? Permissions);
}
