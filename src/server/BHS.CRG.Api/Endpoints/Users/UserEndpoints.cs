using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BHS.CRG.Application.Email;
using BHS.CRG.Infrastructure.Email;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Users;

public static class UserEndpoints
{
    private static readonly string[] ValidRoles = ["Admin", "User"];

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/users").RequireAuthorization("Admin");

        g.MapGet("/", async (UserManager<ApplicationUser> users) =>
        {
            var list = await users.Users.OrderBy(u => u.Email).ToListAsync();
            var result = new List<UserDto>(list.Count);
            foreach (var u in list)
            {
                var roles = await users.GetRolesAsync(u);
                result.Add(new UserDto(u.Id, u.Email ?? "", u.DisplayName, roles.FirstOrDefault() ?? "User"));
            }
            return Results.Ok(result);
        });

        g.MapPost("/", async (CreateUserRequest req,
            UserManager<ApplicationUser> users, AccountEmailService emails, CancellationToken ct) =>
        {
            var role = NormalizeRole(req.Role);
            if (role is null) return Results.BadRequest(new { error = "Недопустимая роль" });
            if (string.IsNullOrWhiteSpace(req.Email)) return Results.BadRequest(new { error = "Email обязателен" });

            var user = new ApplicationUser
            {
                UserName = req.Email.Trim(),
                Email = req.Email.Trim(),
                DisplayName = (req.DisplayName ?? "").Trim(),
            };
            var created = await users.CreateAsync(user, req.Password);
            if (!created.Succeeded) return Results.BadRequest(new { error = DescribeErrors(created) });
            await users.AddToRoleAsync(user, role);

            // По желанию админа — сразу отправить письмо для подтверждения адреса (issue #148).
            // Ошибку отправки не роняем в ответ: пользователь уже создан, письмо можно переслать позже.
            if (req.SendConfirmation == true)
            {
                try
                {
                    var token = await users.GenerateEmailConfirmationTokenAsync(user);
                    await emails.SendEmailConfirmationAsync(user.Email!, token, ct);
                }
                // SMTP/App:PublicUrl не настроены — пользователь создан, письмо отправят позже.
                catch (Exception ex) when (ex is EmailNotConfiguredException or AppUrlNotConfiguredException) { }
            }
            return Results.Ok(new UserDto(user.Id, user.Email!, user.DisplayName, role));
        });

        g.MapPut("/{id:guid}/role", async (Guid id, ChangeRoleRequest req,
            UserManager<ApplicationUser> users, ClaimsPrincipal principal) =>
        {
            var role = NormalizeRole(req.Role);
            if (role is null) return Results.BadRequest(new { error = "Недопустимая роль" });

            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();

            var current = await users.GetRolesAsync(user);
            if (current.Contains("Admin") && role != "Admin" && await IsLastAdmin(users))
                return Results.BadRequest(new { error = "Нельзя понизить последнего администратора" });
            if (id == CurrentUserId(principal) && role != "Admin")
                return Results.BadRequest(new { error = "Нельзя снять роль администратора с самого себя" });

            if (current.Count > 0) await users.RemoveFromRolesAsync(user, current);
            await users.AddToRoleAsync(user, role);
            return Results.Ok(new UserDto(user.Id, user.Email ?? "", user.DisplayName, role));
        });

        g.MapPost("/{id:guid}/reset-password", async (Guid id, ResetPasswordRequest req,
            UserManager<ApplicationUser> users, RefreshTokenService refreshTokens, CancellationToken ct) =>
        {
            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var result = await users.ResetPasswordAsync(user, token, req.NewPassword);
            if (!result.Succeeded) return Results.BadRequest(new { error = DescribeErrors(result) });

            // Сброс пароля админом снимает блокировку и отзывает refresh-сессии (issue #148 follow-up).
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            await refreshTokens.RevokeAllForUserAsync(user.Id, ct);
            return Results.Ok();
        });

        g.MapDelete("/{id:guid}", async (Guid id,
            UserManager<ApplicationUser> users, ClaimsPrincipal principal) =>
        {
            if (id == CurrentUserId(principal))
                return Results.BadRequest(new { error = "Нельзя удалить самого себя" });

            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();

            var roles = await users.GetRolesAsync(user);
            if (roles.Contains("Admin") && await IsLastAdmin(users))
                return Results.BadRequest(new { error = "Нельзя удалить последнего администратора" });

            var result = await users.DeleteAsync(user);
            return result.Succeeded ? Results.NoContent() : Results.BadRequest(new { error = DescribeErrors(result) });
        });
    }

    private static string? NormalizeRole(string? role) =>
        ValidRoles.FirstOrDefault(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));

    private static async Task<bool> IsLastAdmin(UserManager<ApplicationUser> users) =>
        (await users.GetUsersInRoleAsync("Admin")).Count <= 1;

    private static Guid CurrentUserId(ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(JwtRegisteredClaimNames.Sub)
                      ?? p.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : Guid.Empty;

    private static string DescribeErrors(IdentityResult r) =>
        string.Join("; ", r.Errors.Select(e => e.Description));

    record UserDto(Guid Id, string Email, string DisplayName, string Role);
    record CreateUserRequest(string Email, string? DisplayName, string Password, string Role, bool? SendConfirmation);
    record ChangeRoleRequest(string Role);
    record ResetPasswordRequest(string NewPassword);
}
