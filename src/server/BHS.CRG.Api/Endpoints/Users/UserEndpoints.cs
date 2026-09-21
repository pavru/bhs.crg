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
                result.Add(new UserDto(u.Id, u.Email ?? "", u.DisplayName,
                    Refs(await users.GetRolesAsync(u), titles)));
            return Results.Ok(result);
        });

        g.MapPost("/", async (CreateUserRequest req,
            UserManager<ApplicationUser> users, AccountEmailService emails, IActivityLog journal,
            RoleEditor editor, CancellationToken ct) =>
        {
            var (wanted, unknown) = await ResolveAsync(editor, req.Roles);
            if (unknown is not null) return Results.BadRequest(new { error = unknown });

            // При ЗАВЕДЕНИИ роль обязательна, а при смене — нет, и это не разнобой (issue #984).
            // Завести человека без единой роли — почти всегда промах: он войдёт и не увидит ничего,
            // а выглядеть это будет как поломка доступа. Снять же все роли у работающего —
            // осознанное действие, и другого способа отозвать доступ, не удаляя учётную запись
            // вместе с её следом в журнале, в системе нет.
            if (wanted.Count == 0)
                return Results.BadRequest(new { error = "Выберите хотя бы одну роль: она и есть набор прав, который получит человек" });
            if (string.IsNullOrWhiteSpace(req.Email)) return Results.BadRequest(new { error = "Email обязателен" });

            var user = new ApplicationUser
            {
                UserName = req.Email.Trim(),
                Email = req.Email.Trim(),
                DisplayName = (req.DisplayName ?? "").Trim(),
            };
            var created = await users.CreateAsync(user, req.Password);
            if (!created.Succeeded) return Results.BadRequest(new { error = DescribeErrors(created) });
            await users.AddToRolesAsync(user, wanted.Select(r => r.Name));

            // Заведение пользователя — это и выдача прав (ТЗ CORE-28): роли названы прямо здесь, и
            // без этой записи в журнале было бы видно только последующие СМЕНЫ ролей, а начальная
            // выдача — самая широкая из всех — оставалась бы неизвестно чьей.
            await journal.RecordAsync(ActivityActions.UserCreated,
                user.Id.ToString(), user.Email, after: Describe(wanted), ct: ct);

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
            return Results.Ok(new UserDto(user.Id, user.Email!, user.DisplayName, Refs(wanted)));
        });

        // Адрес назначения ОДИН и принимает список (ТЗ AUTH-3, issue #984). Одиночный
        // PUT /{id}/role убран, а не оставлен рядом: два пути назначения разошлись бы в первый же
        // день — в том, снимают ли они прочие роли, и в том, какие проверки при этом делают.
        g.MapPut("/{id:guid}/roles", async (Guid id, ChangeRolesRequest req,
            UserManager<ApplicationUser> users, ClaimsPrincipal principal, IActivityLog journal,
            RoleEditor editor, CancellationToken ct) =>
        {
            var (wanted, unknown) = await ResolveAsync(editor, req.Roles);
            if (unknown is not null) return Results.BadRequest(new { error = unknown });

            var user = await users.FindByIdAsync(id.ToString());
            if (user is null) return Results.NotFound();

            var current = await users.GetRolesAsync(user);

            // Обе защиты считаются ПО ПРАВУ, а не по имени роли «Admin» (ТЗ AUTH-8.2, issues
            // #952/#989). С несколькими ролями имя перестаёт отвечать на вопрос вовсе: у человека
            // может быть «Администратор» плюс «Сметчик», и снятие второй — не понижение; а право
            // управлять пользователями администратор вправе выдать и своей роли, заведённой руками.
            // Имя роли тогда молчит, а право — отвечает.
            var keepsManage = Grants(wanted, CorePermissions.UsersManage);
            if (id == CurrentUserId(principal) && !keepsManage)
                return Results.BadRequest(new { error =
                    "Нельзя снять с себя право управлять пользователями: вернуть его будет некому" });
            if (!keepsManage && await GrantsAsync(editor, current, CorePermissions.UsersManage)
                             && !await SomeoneElseManagesAsync(editor, users, id))
                return Results.BadRequest(new { error =
                    "Это последний, кто может управлять пользователями, — экземпляр остался бы без управления" });

            if (current.Count > 0) await users.RemoveFromRolesAsync(user, current);
            if (wanted.Count > 0) await users.AddToRolesAsync(user, wanted.Select(r => r.Name));

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
                before: await TitlesAsync(editor, current), after: Describe(wanted), ct: ct);

            return Results.Ok(new UserDto(user.Id, user.Email ?? "", user.DisplayName, Refs(wanted)));
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

            // По праву, а не по имени роли (issue #984) — и «у него оно есть» проверяется отдельно
            // от «больше ни у кого нет». Без первой половины удаление любого постороннего упиралось
            // бы в отказ на экземпляре, который и так остался без управления.
            var roles = await users.GetRolesAsync(user);
            if (await GrantsAsync(editor, roles, CorePermissions.UsersManage)
                && !await SomeoneElseManagesAsync(editor, users, id))
                return Results.BadRequest(new { error =
                    "Это последний, кто может управлять пользователями, — экземпляр остался бы без управления" });

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

    /// <summary>
    /// Разбирает заявленный список ролей. Возвращает найденные роли либо причину отказа.
    ///
    /// ⚠️ Неизвестное имя — ОТКАЗ, а не тихий пропуск. Пропущенное имя означало бы, что опечатка в
    /// одной из трёх ролей выдаёт человеку две и отвечает «готово»: доступ оказался бы уже, чем
    /// показала форма, и заметилось бы это тогда, когда человек не смог что-то сделать.
    /// </summary>
    private static async Task<(List<RoleView> Roles, string? Error)> ResolveAsync(
        RoleEditor editor, IReadOnlyList<string>? names)
    {
        var result = new List<RoleView>();
        foreach (var name in (names ?? []).Select(n => (n ?? "").Trim()).Where(n => n.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var role = await editor.FindAsync(name);
            if (role is null) return ([], Unknown(name));
            result.Add(role);
        }
        return (result, null);
    }

    private static string Unknown(string? role) =>
        string.IsNullOrWhiteSpace(role) ? "Роль не указана" : $"Роли «{role}» нет";

    /// <summary>
    /// Даёт ли этот НАБОР ролей такое право. Роль «все права» состава не перечисляет — он равен
    /// справочнику, поэтому она даёт всё (см. <see cref="RoleView.AllPermissions" />).
    /// </summary>
    private static bool Grants(IEnumerable<RoleView> roles, string permission) =>
        roles.Any(r => r.AllPermissions
                       || r.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase));

    /// <summary>То же для ролей, названных именами, — состав каждой поднимается редактором.</summary>
    private static async Task<bool> GrantsAsync(
        RoleEditor editor, IEnumerable<string> names, string permission)
    {
        foreach (var name in names)
            if (await editor.FindAsync(name) is { } role && Grants([role], permission))
                return true;
        return false;
    }

    /// <summary>
    /// Есть ли КРОМЕ этого человека кто-то, кто может управлять пользователями.
    ///
    /// Считается по праву и по всем ролям сразу: право <c>core.users.manage</c> администратор
    /// вправе выдать и роли, заведённой руками, — и тогда «последний администратор» по имени роли
    /// запрещал бы то, что на деле безопасно. Обратное опаснее: носитель роли с этим правом,
    /// не названной «Администратором», остался бы незамеченным, и экземпляр потерял бы управление
    /// с формальным «проверка прошла».
    /// </summary>
    private static async Task<bool> SomeoneElseManagesAsync(
        RoleEditor editor, UserManager<ApplicationUser> users, Guid exceptId)
    {
        foreach (var role in await editor.ListAsync())
        {
            if (!Grants([role], CorePermissions.UsersManage)) continue;
            foreach (var holder in await users.GetUsersInRoleAsync(role.Name))
                if (holder.Id != exceptId) return true;
        }
        return false;
    }

    /// <summary>Роли для ответа — по названию, то есть в том порядке, в каком их читают.</summary>
    private static IReadOnlyList<RoleRef> Refs(IEnumerable<RoleView> roles) =>
        [.. roles.Select(r => new RoleRef(r.Name, r.Title)).OrderBy(r => r.Title, StringComparer.CurrentCulture)];

    /// <summary>То же по именам и готовому словарю подписей — для списка пользователей.</summary>
    private static IReadOnlyList<RoleRef> Refs(IEnumerable<string> names, IReadOnlyDictionary<string, string> titles) =>
        [.. names.Select(n => new RoleRef(n, titles.GetValueOrDefault(n, n)))
                 .OrderBy(r => r.Title, StringComparer.CurrentCulture)];

    /// <summary>Роли одной строкой для журнала: читают её глазами, и названия отвечают на вопрос.</summary>
    private static string Describe(IEnumerable<RoleView> roles) =>
        Refs(roles) is { Count: > 0 } refs ? string.Join(", ", refs.Select(r => r.Title)) : "без ролей";

    private static Guid CurrentUserId(ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(JwtRegisteredClaimNames.Sub)
                      ?? p.FindFirstValue(ClaimTypes.NameIdentifier), out var g) ? g : Guid.Empty;

    private static string DescribeErrors(IdentityResult r) =>
        string.Join("; ", r.Errors.Select(e => e.Description));

    /// <param name="Roles">
    /// ВСЕ роли человека, а не первая (ТЗ AUTH-3, issue #984). Первая означала бы, что экран
    /// показывает часть выданного доступа как весь: человек с «Администратором» и «Сметчиком»
    /// выглядел бы носителем одной роли, и снятие «лишней» убрало бы права, о которых на экране
    /// не сказано ни слова.
    ///
    /// Пустой список — законное состояние: доступ отозван, учётная запись цела.
    /// </param>
    record UserDto(Guid Id, string Email, string DisplayName, IReadOnlyList<RoleRef> Roles);
    record CreateUserRequest(
        string Email, string? DisplayName, string Password, IReadOnlyList<string>? Roles, bool? SendConfirmation);
    record ChangeRolesRequest(IReadOnlyList<string>? Roles);
    record ResetPasswordRequest(string NewPassword);
}
