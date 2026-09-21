using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Email;
using BHS.CRG.Infrastructure.Email;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Users;

public static class UserEndpoints
{
    /// <summary>
    /// Роли, которые можно назначить. Берутся из каталога системных ролей (issue #945), а не
    /// перечисляются здесь: список из двух имён означал, что заведённые роли назначить нечем, и
    /// про это узнали бы не сразу — назначение просто отказывало бы «недопустимой ролью».
    /// </summary>
    private static string[] ValidRoles => [.. BHS.CRG.Api.Auth.SystemRoles.All.Select(r => r.Name)];

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        // Первая дверь на праве, а не на роли (ТЗ AUTH-8). Управление пользователями выбрано
        // первым не случайно: это дверь, за которой выдаются все остальные.
        var g = app.MapGroup("/api/users")
            .RequireAuthorization(AppPolicies.Permission(BHS.CRG.Api.Auth.CorePermissions.UsersManage));

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
            UserManager<ApplicationUser> users, AccountEmailService emails, IActivityLog journal,
            CancellationToken ct) =>
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

            // Заведение пользователя — это и выдача прав (ТЗ CORE-28): роль названа прямо здесь, и
            // без этой записи в журнале было бы видно только последующие СМЕНЫ роли, а начальная
            // выдача — самая широкая из всех — оставалась бы неизвестно чьей.
            await journal.RecordAsync(ActivityActions.UserCreated,
                user.Id.ToString(), user.Email, after: RoleTitle(role), ct: ct);

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
            UserManager<ApplicationUser> users, ClaimsPrincipal principal, IActivityLog journal,
            CancellationToken ct) =>
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

            // Смена ролей действует НЕМЕДЛЕННО (ТЗ AUTH-7). Отметка безопасности обновляется —
            // выданные токены с прежними ролями перестают приниматься на следующем же запросе, и
            // вместе с ними теряет силу посчитанный по ним набор прав: ключ кэша содержит отметку.
            //
            // Перелогина это не стоит: refresh-сессии НЕ отзываются (в отличие от смены пароля
            // ниже), клиент молча меняет токен и продолжает работу — уже с новыми правами. Отзыв
            // одного права не должен выглядеть как «меня разлогинило».
            await users.UpdateSecurityStampAsync(user);

            // То самое «Готово» из issue #950: автор, время и ПРЕЖНЕЕ значение. Прежнее — потому
            // что по нынешнему составу ролей нельзя ответить на единственный вопрос, ради которого
            // в журнал заглядывают: что у человека было до того, как ему это выдали.
            await journal.RecordAsync(ActivityActions.UserRoleChanged,
                user.Id.ToString(), user.Email,
                before: RoleTitles(current), after: RoleTitle(role), ct: ct);

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
            UserManager<ApplicationUser> users, AppDbContext db, ClaimsPrincipal principal,
            IActivityLog journal, CancellationToken ct) =>
        {
            if (id == CurrentUserId(principal))
                return Results.BadRequest(new { error = "Нельзя удалить самого себя" });

            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();

            var roles = await users.GetRolesAsync(user);
            if (roles.Contains("Admin") && await IsLastAdmin(users))
                return Results.BadRequest(new { error = "Нельзя удалить последнего администратора" });

            var result = await users.DeleteAsync(user);
            if (!result.Succeeded) return Results.BadRequest(new { error = DescribeErrors(result) });

            // Личные уведомления удалённого — вслед за ним. Внешнего ключа у notifications."UserId"
            // нет, а подрезка теперь работает по корзине того, кому только что опубликовали: в
            // корзину удалённого больше никто не напишет никогда, и её три сотни строк остались бы
            // в базе навсегда, невидимые ниоткуда. Отметки прочтения уходят каскадом сами.
            await db.Notifications.Where(n => n.UserId == id).ExecuteDeleteAsync();

            // Удаление — снятие всех прав разом, и след от него остаётся только здесь: самой
            // учётной записи больше нет, а кто её убрал и с какой ролью — вопрос, который задают.
            await journal.RecordAsync(ActivityActions.UserDeleted,
                id.ToString(), user.Email, before: RoleTitles(roles), ct: ct);

            return Results.NoContent();
        });
    }

    /// <summary>
    /// Название роли для человека. В журнал уходит именно оно, а не техническое имя: запись читают
    /// глазами, и «Инженер ИД» отвечает на вопрос, а <c>User</c> — нет.
    ///
    /// ⚠️ Название записывается СНИМКОМ. Переименуют роль — прежние записи останутся со старым
    /// названием, и это верно: тогда выдали именно то, что так называлось.
    /// </summary>
    private static string RoleTitle(string name) =>
        BHS.CRG.Api.Auth.SystemRoles.All.FirstOrDefault(r => r.Name == name)?.Title ?? name;

    private static string? RoleTitles(IEnumerable<string> names)
    {
        var titles = names.Select(RoleTitle).ToList();
        return titles.Count == 0 ? null : string.Join(", ", titles);
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
