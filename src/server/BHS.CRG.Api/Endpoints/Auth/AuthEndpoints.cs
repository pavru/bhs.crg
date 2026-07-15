using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BHS.CRG.Infrastructure.Email;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

namespace BHS.CRG.Api.Endpoints.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");

        // Bootstrap: регистрация открыта ТОЛЬКО когда в системе ещё нет пользователей.
        // Первый зарегистрированный становится администратором. Дальше пользователей
        // заводит администратор через /api/users.
        g.MapPost("/register", async (RegisterRequest req,
            UserManager<ApplicationUser> users) =>
        {
            if (users.Users.Any())
                return Results.Problem("Регистрация закрыта. Обратитесь к администратору.", statusCode: 403);

            // Первому админу подтверждать адрес некому — считаем подтверждённым (issue #148).
            var user = new ApplicationUser { UserName = req.Email, Email = req.Email, DisplayName = req.DisplayName, EmailConfirmed = true };
            var result = await users.CreateAsync(user, req.Password);
            if (!result.Succeeded) return Results.BadRequest(result.Errors);
            await users.AddToRoleAsync(user, "Admin");
            return Results.Ok();
        });

        g.MapGet("/registration-open", (UserManager<ApplicationUser> users) =>
            Results.Ok(new { open = !users.Users.Any() }));

        g.MapPost("/login", async (LoginRequest req,
            UserManager<ApplicationUser> users,
            IConfiguration cfg) =>
        {
            var user = await users.FindByEmailAsync(req.Email);
            if (user is null || !await users.CheckPasswordAsync(user, req.Password))
                return Results.Unauthorized();

            var roles = await users.GetRolesAsync(user);
            var token = CreateToken(user, roles, cfg);
            return Results.Ok(new { accessToken = token });
        });
        // Смена пароля переехала в /api/account/change-password (issue #148).

        // Сброс пароля (issue #148). Анонимные, под rate-limit «auth».
        // Enumeration-safe: forgot всегда 200, существование адреса не раскрываем.
        g.MapPost("/forgot-password", async (ForgotPasswordRequest req,
            UserManager<ApplicationUser> users, AccountEmailService emails,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            var user = await users.FindByEmailAsync(req.Email);
            if (user is not null)
            {
                try
                {
                    var token = await users.GeneratePasswordResetTokenAsync(user);
                    await emails.SendPasswordResetAsync(user.Email!, token, ct);
                }
                catch (Exception ex)
                {
                    // Не роняем ответ (и не раскрываем существование адреса) — только лог без секретов.
                    loggers.CreateLogger("Auth").LogWarning(ex, "Не удалось отправить письмо сброса пароля");
                }
            }
            return Results.Ok();
        }).RequireRateLimiting("auth");

        g.MapPost("/reset-password", async (ResetPasswordRequest req,
            UserManager<ApplicationUser> users) =>
        {
            var user = await users.FindByEmailAsync(req.Email);
            if (user is null)
                return Results.BadRequest(new { error = "Ссылка недействительна или устарела." });

            var result = await users.ResetPasswordAsync(user, req.Token, req.NewPassword);
            return result.Succeeded
                ? Results.Ok()
                : Results.BadRequest(new { error = DescribeErrors(result) });
        }).RequireRateLimiting("auth");

        // Подтверждение адреса по ссылке из письма (issue #148). Анонимные.
        g.MapPost("/confirm-email", async (ConfirmEmailRequest req, UserManager<ApplicationUser> users) =>
        {
            var user = await users.FindByEmailAsync(req.Email);
            if (user is null)
                return Results.BadRequest(new { error = "Ссылка недействительна или устарела." });

            var result = await users.ConfirmEmailAsync(user, req.Token);
            return result.Succeeded
                ? Results.Ok()
                : Results.BadRequest(new { error = DescribeErrors(result) });
        }).RequireRateLimiting("auth");

        // Подтверждение смены адреса (переход по ссылке из письма на НОВЫЙ адрес).
        g.MapPost("/confirm-email-change", async (ConfirmEmailChangeRequest req, UserManager<ApplicationUser> users) =>
        {
            var user = await users.FindByIdAsync(req.UserId.ToString());
            if (user is null)
                return Results.BadRequest(new { error = "Ссылка недействительна или устарела." });

            var result = await users.ChangeEmailAsync(user, req.NewEmail, req.Token);
            if (!result.Succeeded)
                return Results.BadRequest(new { error = DescribeErrors(result) });

            // UserName == Email (вход по email) — синхронизируем, иначе логин отвалится.
            await users.SetUserNameAsync(user, req.NewEmail);
            return Results.Ok();
        }).RequireRateLimiting("auth");
    }

    private static string DescribeErrors(IdentityResult r) =>
        string.Join("; ", r.Errors.Select(e => e.Description));

    private static string CreateToken(ApplicationUser user, IList<string> roles, IConfiguration cfg)
    {
        var jwtCfg = cfg.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtCfg["Key"]!));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email!),
            new("displayName", user.DisplayName),
        };
        foreach (var role in roles)
            claims.Add(new Claim("role", role));

        var token = new JwtSecurityToken(
            issuer: jwtCfg["Issuer"],
            audience: jwtCfg["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    record RegisterRequest(string Email, string Password, string DisplayName);
    record LoginRequest(string Email, string Password);
    record ForgotPasswordRequest(string Email);
    record ResetPasswordRequest(string Email, string Token, string NewPassword);
    record ConfirmEmailRequest(string Email, string Token);
    record ConfirmEmailChangeRequest(Guid UserId, string NewEmail, string Token);
}
