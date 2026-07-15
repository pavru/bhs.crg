using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace BHS.CRG.Api.Endpoints.Account;

/// <summary>
/// Профиль текущего пользователя (issue #148): просмотр/редактирование собственных
/// данных и смена пароля. Работает для любой роли — только со своей учётной записью
/// (пользователь берётся из JWT, не из параметра).
/// </summary>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/account").RequireAuthorization();

        g.MapGet("/", async (UserManager<ApplicationUser> users, ClaimsPrincipal principal) =>
        {
            var user = await FindCurrent(users, principal);
            if (user is null) return Results.Unauthorized();
            var roles = await users.GetRolesAsync(user);
            return Results.Ok(ToDto(user, roles));
        });

        g.MapPut("/", async (UpdateAccountRequest req,
            UserManager<ApplicationUser> users, ClaimsPrincipal principal) =>
        {
            var user = await FindCurrent(users, principal);
            if (user is null) return Results.Unauthorized();

            user.DisplayName = (req.DisplayName ?? "").Trim();
            var result = await users.UpdateAsync(user);
            if (!result.Succeeded) return Results.BadRequest(new { error = DescribeErrors(result) });

            var roles = await users.GetRolesAsync(user);
            return Results.Ok(ToDto(user, roles));
        });

        // Смена пароля текущим пользователем (перенесено из /api/auth в #148).
        g.MapPost("/change-password", async (ChangePasswordRequest req,
            UserManager<ApplicationUser> users, ClaimsPrincipal principal) =>
        {
            var user = await FindCurrent(users, principal);
            if (user is null) return Results.Unauthorized();

            var result = await users.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword);
            return result.Succeeded ? Results.Ok() : Results.BadRequest(new { error = DescribeErrors(result) });
        });
    }

    private static async Task<ApplicationUser?> FindCurrent(UserManager<ApplicationUser> users, ClaimsPrincipal p)
    {
        var id = p.FindFirstValue(JwtRegisteredClaimNames.Sub)
              ?? p.FindFirstValue(ClaimTypes.NameIdentifier);
        return id is null ? null : await users.FindByIdAsync(id);
    }

    private static AccountDto ToDto(ApplicationUser u, IList<string> roles) =>
        new(u.Email ?? "", u.DisplayName, roles.FirstOrDefault() ?? "User", u.EmailConfirmed);

    private static string DescribeErrors(IdentityResult r) =>
        string.Join("; ", r.Errors.Select(e => e.Description));

    record AccountDto(string Email, string DisplayName, string Role, bool EmailConfirmed);
    record UpdateAccountRequest(string? DisplayName);
    record ChangePasswordRequest(string CurrentPassword, string NewPassword);
}
