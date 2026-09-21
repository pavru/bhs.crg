using System.Security.Claims;
using BHS.CRG.Application.Activity;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Identity;

namespace BHS.CRG.Api.Auth;

/// <summary>Роль такой, какой её видит редактор: техническое имя, слова для человека и состав прав.</summary>
/// <param name="Name">Техническое имя — то, что уходит в токен и в связь с пользователем.</param>
/// <param name="System">Системная: правится, но не удаляется (ТЗ AUTH-5).</param>
/// <param name="Edited">
/// Состав прав правил администратор. Признак нужен синхронизатору при старте: с этого момента
/// состав роли принадлежит не коду (см. <see cref="RoleSynchronizer" />).
/// </param>
/// <param name="Users">Сколько людей носит роль: снятие права затронет ровно их.</param>
/// <param name="AllPermissions">
/// Роль «все права»: состав не перечислен, он равен справочнику и пополняется вместе с ним, а
/// потому не правится вовсе (см. <see cref="RoleEditor.SetPermissionsAsync" />).
///
/// ⚠️ Признак отдаётся КЛИЕНТУ нарочно. Без него редактор рисовал бы у такой роли обычные галки,
/// щёлкал ими, а сервер на сохранение отвечал бы 409 — запрет, о котором узнаёшь, только нарушив
/// его (нашло ревью #983).
/// </param>
public sealed record RoleView(
    string Name, string Title, string? Summary, bool System, bool Edited,
    IReadOnlyList<string> Permissions, int Users, bool AllPermissions);

/// <summary>
/// Роль в ответах о ПОЛЬЗОВАТЕЛЕ: техническое имя и название для человека — и больше ничего
/// (issue #984).
///
/// Отдельно от <see cref="RoleView" /> потому, что тот считает носителей роли: список
/// пользователей спрашивают на каждой загрузке экрана, и полный вид роли там поднимал бы всех её
/// носителей по разу на каждого пользователя в списке (ревью #983).
///
/// ⚠️ Имя И название вместе, а не что-то одно. Имя нужно, чтобы роль назначить; название — чтобы
/// её прочитать, и совпадают они далеко не всегда: <c>User</c> — это «Инженер ИД», а у роли,
/// заведённой администратором, имя вида <c>role-1a2b3c4d</c> не читается вовсе.
/// </summary>
public sealed record RoleRef(string Name, string Title);

/// <summary>Итог правки: либо роль, либо отказ с кодом ответа и причиной для человека.</summary>
/// <remarks>
/// Отказ возвращается значением, а не исключением: слой API отвечает кодами (это проверяет
/// <c>DomainExceptionPolicyTests.ApiLayer_DoesNotThrowDomainRefusals</c>), а доменных типов здесь
/// нет — речь не о предметной области, а об устройстве доступа.
/// </remarks>
public sealed record RoleResult(RoleView? Role, int Status = StatusCodes.Status200OK, string? Error = null)
{
    public static RoleResult Refuse(int status, string error) => new(null, status, error);
}

/// <summary>
/// Редактор матрицы ролей (ТЗ AUTH-5, AUTH-5.1, issue #951).
///
/// Роли и их состав живут там же, где жили: роль — запись Identity, право роли — её утверждение
/// <c>perm</c> (<see cref="RoleSynchronizer" />). Редактор не заводит своего хранилища, иначе
/// рядом с механизмом ролей появился бы второй, и расходиться они начали бы в первый же день.
///
/// ⚠️ Правка состава прав действует НЕМЕДЛЕННО для всех, у кого эта роль (AUTH-5.1). Делается это
/// тем же способом, что и смена ролей пользователя (AUTH-7): обновлением отметки безопасности у
/// носителей роли. Своего «сбросить кэш» здесь нет намеренно — отдельный сброс можно забыть
/// позвать, и забытый выглядит как работающая система со старыми правами
/// (см. <see cref="PermissionCache" />).
/// </summary>
public sealed class RoleEditor(
    RoleManager<IdentityRole<Guid>> roles,
    UserManager<ApplicationUser> users,
    PermissionCatalog catalog,
    IActivityLog journal)
{
    /// <summary>
    /// Роль по техническому имени; null — такой роли нет. Через неё же проверяется, можно ли
    /// назначить роль пользователю: живой список ролей — единственный ответ на этот вопрос.
    /// </summary>
    public async Task<RoleView?> FindAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var role = await roles.FindByNameAsync(name.Trim());
        return role is null ? null : await ViewAsync(role);
    }

    /// <summary>
    /// Все роли, упорядоченные ПО НАЗВАНИЮ — тому, которое видит человек.
    ///
    /// ⚠️ Сортировка по техническому имени выглядела здесь безобидной мелочью и стоила регрессии:
    /// список ролей — это ещё и выпадающий список на экране «Пользователи», а диалог создания брал
    /// из него первую роль умолчанием. По именам первой шла <c>Accountant</c>, и новый сотрудник
    /// заводился «Бухгалтером» — с правом отмечать оплату и закрывать период (нашло ревью #983).
    /// Умолчание в диалоге с тех пор убрано, но порядок всё равно обязан быть тем, который читают.
    /// </summary>
    public async Task<IReadOnlyList<RoleView>> ListAsync()
    {
        var result = new List<RoleView>();
        foreach (var role in roles.Roles.ToList())
            result.Add(await ViewAsync(role));
        return [.. result.OrderBy(r => r.Title, StringComparer.CurrentCulture)];
    }

    /// <summary>
    /// Название роли — без пересчёта её носителей.
    ///
    /// ⚠️ Существует отдельно от <see cref="FindAsync" /> ровно поэтому: подпись роли нужна в
    /// профиле и в каждой строке списка пользователей, а <see cref="ViewAsync" /> ради неё
    /// поднимал ВСЕХ носителей роли — соединением по таблице связи, по разу на пользователя
    /// (нашло ревью #983). Здесь читаются только утверждения самой роли.
    /// </summary>
    public async Task<string> TitleAsync(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "";
        var role = await roles.FindByNameAsync(name);
        return role is null ? name : Title(await roles.GetClaimsAsync(role), name);
    }

    /// <summary>
    /// Названия всех ролей по техническим именам — одним проходом, для списков.
    /// Неизвестное имя в словарь не попадает: подписывать его нечем, и подставляется само имя.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> TitlesAsync()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles.Roles.ToList())
        {
            var name = role.Name ?? "";
            map[name] = Title(await roles.GetClaimsAsync(role), name);
        }
        return map;
    }

    public async Task<RoleResult> CreateAsync(
        string? title, string? summary, IReadOnlyList<string>? permissions, CancellationToken ct)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0)
            return RoleResult.Refuse(StatusCodes.Status400BadRequest, "Название роли обязательно.");

        var existing = await ListAsync();
        if (existing.Any(r => string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase)))
            return RoleResult.Refuse(StatusCodes.Status409Conflict,
                $"Роль «{title}» уже есть. Два одинаковых названия в списке ролей означают, что " +
                "выдавать их будут наугад.");

        var (granted, unknown) = Split(permissions);
        if (unknown.Count > 0) return UnknownPermissions(unknown);

        // Имя техническое и непроизносимое НАРОЧНО: оно уходит в токен и в связь с пользователем,
        // а название роли администратор меняет — имя пережило бы переименование только в виде
        // «Кладовщик», которого больше нет. Человеку имя не показывается нигде.
        var role = new IdentityRole<Guid>($"role-{Guid.NewGuid():N}"[..13]);
        var created = await roles.CreateAsync(role);
        if (!created.Succeeded)
            return RoleResult.Refuse(StatusCodes.Status400BadRequest, Describe(created));

        await SetClaimAsync(role, RoleSynchronizer.TitleClaim, title);
        await SetClaimAsync(role, RoleSynchronizer.SummaryClaim, summary);
        // Заведённая администратором роль составом коду не подчиняется вовсе — отмечаем сразу,
        // чтобы синхронизатор при старте не увидел роль «без объявления» и не тронул её.
        await SetClaimAsync(role, RoleSynchronizer.EditedClaim, "да");
        foreach (var code in granted) await roles.AddClaimAsync(role, Permission(code));

        await journal.RecordAsync(ActivityActions.RoleCreated,
            role.Name, title, after: Describe(granted), ct: ct);

        return new RoleResult(await ViewAsync(role));
    }

    public async Task<RoleResult> RenameAsync(string name, string? title, string? summary, CancellationToken ct)
    {
        var role = await roles.FindByNameAsync(name);
        if (role is null) return NotFound(name);

        title = (title ?? "").Trim();
        if (title.Length == 0)
            return RoleResult.Refuse(StatusCodes.Status400BadRequest, "Название роли обязательно.");

        var view = await ViewAsync(role);

        // Название системной роли объявлено кодом, и синхронизатор возвращает его при каждом
        // старте. Переименование «сработало бы» до первого перезапуска — самый неприятный вид
        // отказа: сделанное само отменяется, и непонятно, кто отменил.
        if (view.System)
            return RoleResult.Refuse(StatusCodes.Status409Conflict,
                $"«{view.Title}» — системная роль, её название задано кодом и возвращается при " +
                "каждом запуске. Состав прав менять можно, название — нет.");

        var taken = (await ListAsync()).Any(r =>
            r.Name != role.Name && string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase));
        if (taken)
            return RoleResult.Refuse(StatusCodes.Status409Conflict, $"Роль «{title}» уже есть.");

        await SetClaimAsync(role, RoleSynchronizer.TitleClaim, title);
        await SetClaimAsync(role, RoleSynchronizer.SummaryClaim, summary);

        if (!string.Equals(view.Title, title, StringComparison.Ordinal))
            await journal.RecordAsync(ActivityActions.RoleRenamed,
                role.Name, title, before: view.Title, after: title, ct: ct);

        return new RoleResult(await ViewAsync(role));
    }

    public async Task<RoleResult> SetPermissionsAsync(
        string name, IReadOnlyList<string>? permissions, CancellationToken ct)
    {
        var role = await roles.FindByNameAsync(name);
        if (role is null) return NotFound(name);

        var (wanted, unknown) = Split(permissions);
        if (unknown.Count > 0) return UnknownPermissions(unknown);

        var view = await ViewAsync(role);

        // Роль «все права» составом не правится вовсе (ТЗ AUTH-5 — «Администратора» нельзя лишить
        // core.users.manage; здесь причина шире).
        //
        // Состав у неё не перечислен: он РАВЕН справочнику прав и пополняется вместе с ним. Разреши
        // мы правку — состав замер бы на сегодняшнем справочнике, и право следующего выпуска, как и
        // права нового модуля, не достались бы никому, включая администратора. Отказ доступа всем
        // сразу, без причины в логе и без связи с той давней галкой.
        //
        // Нужен «администратор с ограничениями» — это другая роль, и заводится она рядом.
        if (Declared(role.Name)?.AllPermissions == true)
            return RoleResult.Refuse(StatusCodes.Status409Conflict,
                $"«{view.Title}» — роль «все права»: её состав не перечислен, он равен справочнику " +
                "прав и пополняется вместе с ним. Снятое здесь право вернулось бы при следующем " +
                $"запуске, а снятое навсегда {SystemRoles.AdminCannotLose} оставило бы экземпляр " +
                "без управления пользователями. Заведите отдельную роль с нужным набором.");

        var was = view.Permissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = wanted.Except(was, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var removed = was.Except(wanted, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        if (added.Count == 0 && removed.Count == 0) return new RoleResult(view);

        foreach (var code in added) await roles.AddClaimAsync(role, Permission(code));
        foreach (var code in removed) await roles.RemoveClaimAsync(role, Permission(code));
        await SetClaimAsync(role, RoleSynchronizer.EditedClaim, "да");

        await journal.RecordAsync(ActivityActions.RolePermissionsChanged,
            role.Name, view.Title,
            before: Describe(view.Permissions),
            after: Change(added, removed), ct: ct);

        await RefreshHoldersAsync(role.Name!);

        return new RoleResult(await ViewAsync(role));
    }

    public async Task<RoleResult> DeleteAsync(string name, CancellationToken ct)
    {
        var role = await roles.FindByNameAsync(name);
        if (role is null) return NotFound(name);

        var view = await ViewAsync(role);
        if (view.System)
            return RoleResult.Refuse(StatusCodes.Status409Conflict,
                $"«{view.Title}» — системная роль, её нельзя удалить (ТЗ AUTH-4). " +
                "Чтобы она никому ничего не давала, снимите её у людей или уберите права.");

        // Удаление роли, которую кто-то носит, снимает доступ у этих людей разом и молча. Отказ
        // называет число: решение «оставить их без доступа» принимает администратор, а не список.
        if (view.Users > 0)
            return RoleResult.Refuse(StatusCodes.Status409Conflict,
                $"Роль «{view.Title}» носят {view.Users} чел. Сначала снимите её у них — " +
                "удаление забрало бы доступ сразу у всех и без следа, кому именно.");

        var deleted = await roles.DeleteAsync(role);
        if (!deleted.Succeeded)
            return RoleResult.Refuse(StatusCodes.Status400BadRequest, Describe(deleted));

        await journal.RecordAsync(ActivityActions.RoleDeleted,
            name, view.Title, before: Describe(view.Permissions), ct: ct);

        return new RoleResult(null, StatusCodes.Status204NoContent);
    }

    /// <summary>
    /// Права экземпляра, сгруппированные по модулям, с объяснениями (ТЗ AUTH-5: «редактор
    /// показывает права сгруппированными по модулям и объяснение рядом с галкой»).
    ///
    /// Группа — это часть кода до первой точки. Отдельного поля «модуль» у права нет и не нужно:
    /// код обязан быть вида <c>модуль.объект.действие</c>, это проверяет сам каталог, и вторая
    /// запись о принадлежности начала бы расходиться с первой.
    /// </summary>
    public IReadOnlyList<PermissionGroupView> PermissionGroups(ModuleRegistry modules) =>
        [.. catalog.All
            .GroupBy(p => p.Code.Split('.')[0], StringComparer.OrdinalIgnoreCase)
            .Select(g => new PermissionGroupView(
                g.Key,
                GroupTitle(g.Key, modules),
                [.. g.Select(p => new PermissionView(p.Code, p.Gives, p.Opens, p.UsuallyWith))]))
            .OrderBy(g => g.Module switch
            {
                "core" => 0,   // ядро первым: его прав больше всего и раздают их чаще прочих
                "*" => 2,      // составные — в конце: они про все модули сразу
                _ => 1,
            })
            .ThenBy(g => g.Title, StringComparer.CurrentCulture)];

    private static string GroupTitle(string module, ModuleRegistry modules) => module switch
    {
        "core" => "Ядро",
        "*" => "Составные права",
        _ => modules.Find(module)?.Title ?? module,
    };

    /// <summary>
    /// Отметка безопасности у всех носителей роли — то же, что делает смена ролей человека
    /// (AUTH-7). Их выданные токены перестают приниматься на следующем же запросе, клиент молча
    /// меняет токен, а посчитанный набор прав теряет силу вместе с отметкой: она входит в ключ
    /// кэша.
    ///
    /// ⚠️ Без этого правка состава прав действовала бы «когда-нибудь в ближайшие тридцать секунд» —
    /// ровно тот случай, который AUTH-5.1 и запрещает. Проверяется живым прогоном
    /// (<c>RoleEditorTests</c>): снимите вызов — и человек продолжит ходить со старыми правами.
    /// </summary>
    private async Task RefreshHoldersAsync(string roleName)
    {
        foreach (var holder in await users.GetUsersInRoleAsync(roleName))
            await users.UpdateSecurityStampAsync(holder);
    }

    private async Task<RoleView> ViewAsync(IdentityRole<Guid> role)
    {
        var claims = await roles.GetClaimsAsync(role);
        var name = role.Name ?? "";
        var declared = Declared(name);

        return new RoleView(
            name,
            Title(claims, name),
            claims.FirstOrDefault(c => c.Type == RoleSynchronizer.SummaryClaim)?.Value ?? declared?.Summary,
            System: declared is not null,
            Edited: claims.Any(c => c.Type == RoleSynchronizer.EditedClaim),
            [.. claims.Where(c => c.Type == RoleSynchronizer.PermissionClaim)
                      .Select(c => c.Value).Order(StringComparer.Ordinal)],
            (await users.GetUsersInRoleAsync(name)).Count,
            AllPermissions: declared?.AllPermissions == true);
    }

    /// <summary>Название роли из её утверждений; нет — из объявления; нет и его — техническое имя.</summary>
    private static string Title(IList<Claim> claims, string name) =>
        claims.FirstOrDefault(c => c.Type == RoleSynchronizer.TitleClaim)?.Value
        ?? Declared(name)?.Title ?? name;

    /// <summary>
    /// Объявленные коды отдельно от неизвестных: неизвестный — опечатка, а не «просто нет».
    ///
    /// ⚠️ Известный код приводится к написанию ИЗ СПРАВОЧНИКА. Справочник отвечает про право без
    /// учёта регистра, а дальше код живёт строкой: записанный как <c>CORE.USERS.MANAGE</c>, он
    /// разошёлся бы и с галкой в редакторе, и с проверкой «нельзя снять» — право выглядело бы
    /// выданным и не совпадало бы ни с чем.
    /// </summary>
    private (List<string> Granted, List<string> Unknown) Split(IReadOnlyList<string>? permissions)
    {
        var codes = (permissions ?? [])
            .Select(c => (c ?? "").Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var canonical = catalog.All.ToDictionary(p => p.Code, p => p.Code, StringComparer.OrdinalIgnoreCase);

        return ([.. codes.Where(catalog.Declares).Select(c => canonical[c])],
                [.. codes.Where(c => !catalog.Declares(c))]);
    }

    /// <summary>Объявление системной роли по имени; null — роль завёл администратор.</summary>
    private static RoleDefinition? Declared(string? name) =>
        SystemRoles.All.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    private static RoleResult UnknownPermissions(IReadOnlyList<string> unknown) =>
        RoleResult.Refuse(StatusCodes.Status400BadRequest,
            "Таких прав нет в справочнике этого экземпляра: " + string.Join(", ", unknown) +
            ". Выданная галка с несуществующим кодом не открыла бы ни одной двери.");

    private static RoleResult NotFound(string name) =>
        RoleResult.Refuse(StatusCodes.Status404NotFound, $"Роли «{name}» нет.");

    private static Claim Permission(string code) => new(RoleSynchronizer.PermissionClaim, code);

    /// <summary>Утверждение с одним значением: сначала убираем прежнее, иначе их станет два.</summary>
    private async Task SetClaimAsync(IdentityRole<Guid> role, string type, string? value)
    {
        foreach (var old in (await roles.GetClaimsAsync(role)).Where(c => c.Type == type))
            await roles.RemoveClaimAsync(role, old);

        value = (value ?? "").Trim();
        if (value.Length > 0) await roles.AddClaimAsync(role, new Claim(type, value));
    }

    private static string Describe(IReadOnlyCollection<string> codes) =>
        codes.Count == 0 ? "прав нет" : "права: " + string.Join(", ", codes.Order(StringComparer.Ordinal));

    private static string Change(IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        var parts = new List<string>();
        if (added.Count > 0) parts.Add("выдано: " + string.Join(", ", added));
        if (removed.Count > 0) parts.Add("снято: " + string.Join(", ", removed));
        return string.Join("; ", parts);
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));
}

/// <summary>Право с объяснением — то, что редактор показывает рядом с галкой (ТЗ AUTH-5).</summary>
public sealed record PermissionView(
    string Code, string Gives, string Opens, IReadOnlyList<string> UsuallyWith);

/// <summary>Права одного модуля (или ядра) под его названием.</summary>
public sealed record PermissionGroupView(
    string Module, string Title, IReadOnlyList<PermissionView> Permissions);
