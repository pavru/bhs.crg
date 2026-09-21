using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
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

        /// Что доступно ЭТОМУ пользователю (ТЗ AUTH-14). Единственный источник, по которому клиент
        /// строит навигацию: названия ролей он не читает вовсе.
        ///
        /// Почему один адрес, а не «список прав» отдельно и «модули» отдельно: навигация — это
        /// ответ на один вопрос, и собранный из двух ответов он умеет расходиться сам с собой.
        ///
        /// ⚠️ Ответ НЕ кэшируется на клиенте бессрочно: права меняются немедленно (AUTH-7), и
        /// устаревший ответ рисует меню, которого у пользователя больше нет, — то есть пункты,
        /// отвечающие отказом. Это тот же случай, что и роль в токене, только этажом выше.
        g.MapGet("/access", async (ClaimsPrincipal principal, IUserPermissions permissions,
            ModuleRegistry modules, CancellationToken ct) =>
        {
            var granted = await permissions.ForAsync(principal, ct);
            return Results.Ok(new
            {
                permissions = granted.Order(StringComparer.Ordinal).ToArray(),
                // Модули экземпляра целиком: и доступные, и нет. Клиенту нужны оба списка —
                // «нет такого модуля на экземпляре» и «модуль есть, но не для вас» это разные
                // отказы, и страница AUTH-15 обязана называть их по-разному.
                modules = modules.Enabled.Select(m => new
                {
                    code = m.Code,
                    title = m.Title,
                    available = ModuleAccess.IsOpen(m.Code, granted),
                }).ToArray(),
            });
        });

        g.MapGet("/", async (UserManager<ApplicationUser> users, RoleEditor editor, ClaimsPrincipal principal) =>
        {
            var user = await FindCurrent(users, principal);
            if (user is null) return Results.Unauthorized();
            return Results.Ok(await ToDtoAsync(user, users, editor));
        });

        g.MapPut("/", async (UpdateAccountRequest req,
            UserManager<ApplicationUser> users, RoleEditor editor, ClaimsPrincipal principal) =>
        {
            var user = await FindCurrent(users, principal);
            if (user is null) return Results.Unauthorized();

            user.DisplayName = (req.DisplayName ?? "").Trim();
            var result = await users.UpdateAsync(user);
            if (!result.Succeeded) return Results.BadRequest(new { error = DescribeErrors(result) });

            return Results.Ok(await ToDtoAsync(user, users, editor));
        });

        // Аватар профиля (issue #245): data-URI уменьшённой на клиенте картинки; null — удалить.
        g.MapPut("/avatar", async (UpdateAvatarRequest req,
            UserManager<ApplicationUser> users, RoleEditor editor, ClaimsPrincipal principal) =>
        {
            var user = await FindCurrent(users, principal);
            if (user is null) return Results.Unauthorized();

            var avatar = req.Avatar?.Trim();
            if (string.IsNullOrEmpty(avatar))
            {
                user.AvatarDataUri = null;
            }
            else
            {
                if (!avatar.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || !avatar.Contains(";base64,"))
                    return Results.BadRequest(new { error = "Ожидается изображение (data:image;base64)" });
                // Аватар уменьшается на клиенте (~256px). Верхняя граница — защита от гигантских data-URI.
                if (avatar.Length > MaxAvatarChars)
                    return Results.BadRequest(new { error = "Изображение слишком большое" });
                user.AvatarDataUri = avatar;
            }

            var result = await users.UpdateAsync(user);
            if (!result.Succeeded) return Results.BadRequest(new { error = DescribeErrors(result) });

            return Results.Ok(await ToDtoAsync(user, users, editor));
        });

        // Смена пароля текущим пользователем (перенесено из /api/auth в #148).
        g.MapPost("/change-password", async (ChangePasswordRequest req,
            UserManager<ApplicationUser> users, RefreshTokenService refreshTokens,
            ClaimsPrincipal principal, IConfiguration cfg, CancellationToken ct) =>
        {
            var user = await FindCurrent(users, principal);
            if (user is null) return Results.Unauthorized();

            var result = await users.ChangePasswordAsync(user, req.CurrentPassword, req.NewPassword);
            if (!result.Succeeded) return Results.BadRequest(new { error = DescribeErrors(result) });

            // Смена пароля обновляет SecurityStamp и отзывает все refresh-сессии. Чтобы не разлогинить
            // текущую — выдаём свежую пару access+refresh (issue #148 follow-up).
            await refreshTokens.RevokeAllForUserAsync(user.Id, ct);
            var roles = await users.GetRolesAsync(user);
            var stamp = await users.GetSecurityStampAsync(user);
            var access = JwtTokens.Create(user, roles, stamp, cfg);
            var refresh = await refreshTokens.IssueAsync(user.Id, ct);
            return Results.Ok(new { accessToken = access, refreshToken = refresh });
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
            catch (Exception ex) when (ex is EmailNotConfiguredException or AppUrlNotConfiguredException)
            { return Results.BadRequest(new { error = ex.Message }); }
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
            catch (Exception ex) when (ex is EmailNotConfiguredException or AppUrlNotConfiguredException)
            { return Results.BadRequest(new { error = ex.Message }); }
            return Results.Ok();
        });
    }

    // ~700 КБ строки data-URI (≈0.5 МБ бинарных) — с запасом для уменьшённого клиентом аватара.
    private const int MaxAvatarChars = 700_000;

    private static async Task<ApplicationUser?> FindCurrent(UserManager<ApplicationUser> users, ClaimsPrincipal p)
    {
        var id = p.FindFirstValue(JwtRegisteredClaimNames.Sub)
              ?? p.FindFirstValue(ClaimTypes.NameIdentifier);
        return id is null ? null : await users.FindByIdAsync(id);
    }

    /// <summary>
    /// Профиль вместе с НАЗВАНИЕМ роли (issue #951).
    ///
    /// Раньше подпись роли собирал клиент по техническому имени, и знал он ровно два: «Admin» и
    /// «User». Роль, заведённую администратором, он подписать не мог вовсе, а системные подписывал
    /// по-своему — «User» в трёх местах интерфейса звался и «Пользователь», и «Инженер ИД».
    /// </summary>
    private static async Task<AccountDto> ToDtoAsync(
        ApplicationUser u, UserManager<ApplicationUser> users, RoleEditor editor)
    {
        // ВСЕ роли, а не первая (issue #984). Здесь же был и второй обман: у человека без ролей
        // подставлялся «Инженер ИД» — профиль называл роль, которой нет, ровно тому, у кого нет
        // никакого доступа, и «мне ничего не открывается» переставало сходиться с «у меня роль».
        //
        // Именно TitleAsync, а не FindAsync: профиль спрашивают на каждой загрузке экрана, а полный
        // вид роли ради одной подписи поднимал всех её носителей (ревью #983).
        var roles = new List<RoleRef>();
        foreach (var name in await users.GetRolesAsync(u))
            roles.Add(new RoleRef(name, await editor.TitleAsync(name)));

        return new(u.Email ?? "", u.DisplayName,
            [.. roles.OrderBy(r => r.Title, StringComparer.CurrentCulture)],
            u.EmailConfirmed, u.AvatarDataUri);
    }

    private static string DescribeErrors(IdentityResult r) =>
        string.Join("; ", r.Errors.Select(e => e.Description));

    record AccountDto(
        string Email, string DisplayName, IReadOnlyList<RoleRef> Roles, bool EmailConfirmed, string? Avatar);
    record UpdateAccountRequest(string? DisplayName);
    record UpdateAvatarRequest(string? Avatar);
    record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    record ChangeEmailRequest(string? NewEmail, string CurrentPassword);
}
