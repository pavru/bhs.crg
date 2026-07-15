using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Email;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

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
            UserManager<ApplicationUser> users, RefreshTokenService refreshTokens,
            IConfiguration cfg, CancellationToken ct) =>
        {
            var user = await users.FindByEmailAsync(req.Email);
            if (user is null)
                return Results.Unauthorized();

            // Защита от перебора пароля: временная блокировка после N неудач (issue #148 follow-up).
            if (await users.IsLockedOutAsync(user))
                return Results.Json(new { error = "Слишком много попыток. Аккаунт временно заблокирован, попробуйте позже." },
                    statusCode: StatusCodes.Status423Locked);

            if (!await users.CheckPasswordAsync(user, req.Password))
            {
                await users.AccessFailedAsync(user);
                if (await users.IsLockedOutAsync(user))
                    return Results.Json(new { error = "Слишком много попыток. Аккаунт временно заблокирован, попробуйте позже." },
                        statusCode: StatusCodes.Status423Locked);
                return Results.Unauthorized();
            }

            await users.ResetAccessFailedCountAsync(user);
            var roles = await users.GetRolesAsync(user);
            var stamp = await users.GetSecurityStampAsync(user);
            var access = JwtTokens.Create(user, roles, stamp, cfg);
            var refresh = await refreshTokens.IssueAsync(user.Id, ct);
            return Results.Ok(new { accessToken = access, refreshToken = refresh });
        });
        // Смена пароля переехала в /api/account/change-password (issue #148).

        // Обмен refresh-токена на новую пару (issue #148 follow-up). Ротация: старый ревокается.
        g.MapPost("/refresh", async (RefreshRequest req,
            UserManager<ApplicationUser> users, RefreshTokenService refreshTokens,
            IConfiguration cfg, CancellationToken ct) =>
        {
            var rotated = await refreshTokens.RotateAsync(req.RefreshToken, ct);
            if (rotated is null) return Results.Unauthorized();

            var user = await users.FindByIdAsync(rotated.Value.UserId.ToString());
            if (user is null) return Results.Unauthorized();

            var roles = await users.GetRolesAsync(user);
            var stamp = await users.GetSecurityStampAsync(user);
            var access = JwtTokens.Create(user, roles, stamp, cfg);
            return Results.Ok(new { accessToken = access, refreshToken = rotated.Value.NewToken });
        }).RequireRateLimiting("auth");

        // Логаут: отзыв refresh-токена текущей сессии.
        g.MapPost("/logout", async (RefreshRequest req, RefreshTokenService refreshTokens, CancellationToken ct) =>
        {
            await refreshTokens.RevokeAsync(req.RefreshToken, ct);
            return Results.Ok();
        });

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
            UserManager<ApplicationUser> users, RefreshTokenService refreshTokens, CancellationToken ct) =>
        {
            var user = await users.FindByEmailAsync(req.Email);
            if (user is null)
                return Results.BadRequest(new { error = "Ссылка недействительна или устарела." });

            var result = await users.ResetPasswordAsync(user, req.Token, req.NewPassword);
            if (!result.Succeeded)
                return Results.BadRequest(new { error = DescribeErrors(result) });

            // Успешный сброс снимает блокировку и отзывает все refresh-сессии (issue #148 follow-up).
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            await refreshTokens.RevokeAllForUserAsync(user.Id, ct);
            return Results.Ok();
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

    record RegisterRequest(string Email, string Password, string DisplayName);
    record LoginRequest(string Email, string Password);
    record ForgotPasswordRequest(string Email);
    record ResetPasswordRequest(string Email, string Token, string NewPassword);
    record ConfirmEmailRequest(string Email, string Token);
    record ConfirmEmailChangeRequest(Guid UserId, string NewEmail, string Token);
    record RefreshRequest(string RefreshToken);
}
