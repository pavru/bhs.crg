using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Email;
using BHS.CRG.Infrastructure.Email;
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
            UserManager<ApplicationUser> users, ClaimsPrincipal principal, IConfiguration cfg) =>
        {
            var user = await FindCurrent(users, principal);
            if (user is null) return Results.Unauthorized();

            var result = await users.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword);
            if (!result.Succeeded) return Results.BadRequest(new { error = DescribeErrors(result) });

            // Смена пароля обновляет SecurityStamp → текущий токен становится недействительным.
            // Выдаём свежий, чтобы не разлогинивать активную сессию (issue #148 follow-up).
            var roles = await users.GetRolesAsync(user);
            var stamp = await users.GetSecurityStampAsync(user);
            return Results.Ok(new { accessToken = JwtTokens.Create(user, roles, stamp, cfg) });
        });

        // Повторно отправить письмо подтверждения себе (issue #148).
        g.MapPost("/resend-confirmation", async (
            UserManager<ApplicationUser> users, AccountEmailService emails, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = await FindCurrent(users, principal);
            if (user is null) return Results.Unauthorized();
            if (user.EmailConfirmed) return Results.Ok();

            var token = await users.GenerateEmailConfirmationTokenAsync(user);
            try { await emails.SendEmailConfirmationAsync(user.Email!, token, ct); }
            catch (EmailNotConfiguredException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.Ok();
        });

        // Смена email: письмо-подтверждение уходит на НОВЫЙ адрес; сам email меняется
        // только после перехода по ссылке (/api/auth/confirm-email-change). Требует текущий пароль.
        g.MapPost("/change-email", async (ChangeEmailRequest req,
            UserManager<ApplicationUser> users, AccountEmailService emails, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = await FindCurrent(users, principal);
            if (user is null) return Results.Unauthorized();

            var newEmail = (req.NewEmail ?? "").Trim();
            if (string.IsNullOrWhiteSpace(newEmail))
                return Results.BadRequest(new { error = "Укажите новый email" });
            if (!await users.CheckPasswordAsync(user, req.CurrentPassword))
                return Results.BadRequest(new { error = "Неверный текущий пароль" });
            if (await users.FindByEmailAsync(newEmail) is not null)
                return Results.BadRequest(new { error = "Этот email уже используется" });

            var token = await users.GenerateChangeEmailTokenAsync(user, newEmail);
            try { await emails.SendEmailChangeAsync(user.Id, newEmail, token, ct); }
            catch (EmailNotConfiguredException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.Ok();
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
    record ChangeEmailRequest(string? NewEmail, string CurrentPassword);
}
