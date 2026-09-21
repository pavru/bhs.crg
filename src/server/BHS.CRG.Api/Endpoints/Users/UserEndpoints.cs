using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BHS.CRG.Api.Auth;
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
    // Роли, которые можно назначить, спрашиваются у РЕДАКТОРА — то есть у живого списка ролей
    // (issue #951). Перечень системных ролей здесь означал бы, что заведённую администратором роль
    // назначить нечем: она создаётся, права ей выдаются, а носить её некому — и отказ приходит
    // словами «недопустимая роль», по которым не догадаться, что дело в списке, а не в роли.

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        // Первая дверь на праве, а не на роли (ТЗ AUTH-8). Управление пользователями выбрано
        // первым не случайно: это дверь, за которой выдаются все остальные.
        var g = app.MapGroup("/api/users")
            .RequireAuthorization(AppPolicies.Permission(BHS.CRG.Api.Auth.CorePermissions.UsersManage));

        g.MapGet("/", async (UserManager<ApplicationUser> users, RoleEditor editor) =>
        {
            var list = await users.Users.OrderBy(u => u.Email).ToListAsync();
            // Подписи ролей — ОДНИМ словарём на весь список. Спрошенные по одной, они поднимали
            // полный вид роли вместе со всеми её носителями — на каждого пользователя в списке
            // (ревью #983). Ролей единицы, и словарь дешевле любого из тех запросов.
            var titles = await editor.TitlesAsync();
            var result = new List<UserDto>(list.Count);
            foreach (var u in list)
            {
                var roles = await users.GetRolesAsync(u);
                var role = roles.FirstOrDefault() ?? SystemRoles.IdEngineer;
                result.Add(new UserDto(u.Id, u.Email ?? "", u.DisplayName, role,
                    titles.GetValueOrDefault(role, role)));
            }
            return Results.Ok(result);
        });

        g.MapPost("/", async (CreateUserRequest req,
            UserManager<ApplicationUser> users, AccountEmailService emails, IActivityLog journal,
            RoleEditor editor, CancellationToken ct) =>
        {
            var role = await editor.FindAsync(req.Role);
            if (role is null) return Results.BadRequest(new { error = Unknown(req.Role) });
            if (string.IsNullOrWhiteSpace(req.Email)) return Results.BadRequest(new { error = "Email обязателен" });

            var user = new ApplicationUser
            {
                UserName = req.Email.Trim(),
                Email = req.Email.Trim(),
                DisplayName = (req.DisplayName ?? "").Trim(),
            };
            var created = await users.CreateAsync(user, req.Password);
            if (!created.Succeeded) return Results.BadRequest(new { error = DescribeErrors(created) });
            await users.AddToRoleAsync(user, role.Name);

            // Заведение пользователя — это и выдача прав (ТЗ CORE-28): роль названа прямо здесь, и
            // без этой записи в журнале было бы видно только последующие СМЕНЫ роли, а начальная
            // выдача — самая широкая из всех — оставалась бы неизвестно чьей.
            await journal.RecordAsync(ActivityActions.UserCreated,
                user.Id.ToString(), user.Email, after: role.Title, ct: ct);

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
            return Results.Ok(new UserDto(user.Id, user.Email!, user.DisplayName, role.Name, role.Title));
        });

        g.MapPut("/{id:guid}/role", async (Guid id, ChangeRoleRequest req,
            UserManager<ApplicationUser> users, ClaimsPrincipal principal, IActivityLog journal,
            RoleEditor editor, CancellationToken ct) =>
        {
            var role = await editor.FindAsync(req.Role);
            if (role is null) return Results.BadRequest(new { error = Unknown(req.Role) });

            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();

            var current = await users.GetRolesAsync(user);
            var staysAdmin = string.Equals(role.Name, SystemRoles.Admin, StringComparison.OrdinalIgnoreCase);
            if (current.Contains(SystemRoles.Admin) && !staysAdmin && await IsLastAdmin(users))
                return Results.BadRequest(new { error = "Нельзя понизить последнего администратора" });
            if (id == CurrentUserId(principal) && !staysAdmin)
                return Results.BadRequest(new { error = "Нельзя снять роль администратора с самого себя" });

            if (current.Count > 0) await users.RemoveFromRolesAsync(user, current);
            await users.AddToRoleAsync(user, role.Name);

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
                before: await TitlesAsync(editor, current), after: role.Title, ct: ct);

            return Results.Ok(new UserDto(user.Id, user.Email ?? "", user.DisplayName, role.Name, role.Title));
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
            IActivityLog journal, RoleEditor editor, CancellationToken ct) =>
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
                id.ToString(), user.Email, before: await TitlesAsync(editor, roles), ct: ct);

            return Results.NoContent();
        });
    }

    /// <summary>
    /// Названия ролей для человека. В журнал уходят именно они, а не технические имена: запись
    /// читают глазами, и «Инженер ИД» отвечает на вопрос, а <c>User</c> или <c>role-1a2b3c4d</c> —
    /// нет.
    ///
    /// ⚠️ Название записывается СНИМКОМ. Переименуют роль — прежние записи останутся со старым
    /// названием, и это верно: тогда выдали именно то, что так называлось.
    /// </summary>
    private static async Task<string?> TitlesAsync(RoleEditor editor, IEnumerable<string> names)
    {
        var titles = new List<string>();
        foreach (var name in names) titles.Add(await editor.TitleAsync(name));
        return titles.Count == 0 ? null : string.Join(", ", titles);
    }

    private static string Unknown(string? role) =>
        string.IsNullOrWhiteSpace(role) ? "Роль не указана" : $"Роли «{role}» нет";

    private static async Task<bool> IsLastAdmin(UserManager<ApplicationUser> users) =>
        (await users.GetUsersInRoleAsync("Admin")).Count <= 1;

    private static Guid CurrentUserId(ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(JwtRegisteredClaimNames.Sub)
                      ?? p.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : Guid.Empty;

    private static string DescribeErrors(IdentityResult r) =>
        string.Join("; ", r.Errors.Select(e => e.Description));

    /// <param name="Role">Техническое имя — им же роль и назначают.</param>
    /// <param name="RoleTitle">
    /// Название для человека: у заведённой администратором роли техническое имя нечитаемо, а у
    /// системной оно расходится с подписью («User» — «Инженер ИД»).
    /// </param>
    record UserDto(Guid Id, string Email, string DisplayName, string Role, string RoleTitle);
    record CreateUserRequest(string Email, string? DisplayName, string Password, string Role, bool? SendConfirmation);
    record ChangeRoleRequest(string Role);
    record ResetPasswordRequest(string NewPassword);
}
