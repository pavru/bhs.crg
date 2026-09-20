using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace BHS.CRG.Modules;

/// <summary>Требование «у пользователя есть право <paramref name="code" />».</summary>
public sealed class PermissionRequirement(string code) : IAuthorizationRequirement
{
    public string Code { get; } = code;
    public override string ToString() => AppPolicies.Permission(Code);
}

/// <summary>Требование «у пользователя есть доступ к модулю <paramref name="code" />».</summary>
public sealed class ModuleAccessRequirement(string code) : IAuthorizationRequirement
{
    public string Code { get; } = code;
    public override string ToString() => AppPolicies.Module(Code);
}

/// <summary>
/// Политики <c>perm:</c> и <c>module:</c> не перечисляются при запуске, а собираются по имени
/// (ТЗ AUTH-8).
///
/// Перечисление означало бы, что каждое новое право модуля требует правки в общем коде запуска —
/// то есть ровно то, от чего уходит модульность. Права объявляет модуль, а ворота ставятся его
/// именем.
///
/// ⚠️ Пустое имя после двоеточия — ОТКАЗ ПРИ СБОРКЕ политики, а не политика «без требований».
/// <c>perm:</c> с обрезанным кодом выглядит как закрытая дверь и при этом не проверяет ничего;
/// заметить такую дверь по ответу приложения нельзя — она отвечает так же, как настоящая.
/// </summary>
public sealed class AppPolicyProvider(IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    // Политика строится один раз на имя. Имена берутся из ворот на адресах, то есть их конечное
    // число и задаёт его код, а не запрос: расти этому словарю неоткуда.
    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _built = new(StringComparer.OrdinalIgnoreCase);

    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(AppPolicies.PermissionPrefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<AuthorizationPolicy?>(_built.GetOrAdd(policyName,
                name => Build(new PermissionRequirement(Tail(name, AppPolicies.PermissionPrefix)))));

        if (policyName.StartsWith(AppPolicies.ModulePrefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<AuthorizationPolicy?>(_built.GetOrAdd(policyName,
                name => Build(new ModuleAccessRequirement(Tail(name, AppPolicies.ModulePrefix)))));

        return base.GetPolicyAsync(policyName);
    }

    private static AuthorizationPolicy Build(IAuthorizationRequirement requirement) =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(requirement)
            .Build();

    private static string Tail(string policyName, string prefix)
    {
        var code = policyName[prefix.Length..].Trim();
        return code.Length > 0
            ? code
            : throw new InvalidOperationException(
                $"Политика «{policyName}» не называет ни права, ни модуля. Ворота с пустым именем " +
                "ничего не проверяют, а выглядят как проверка.");
    }
}

/// <summary>
/// Проверка права: есть ли <see cref="PermissionRequirement.Code" /> среди действующих прав
/// пользователя.
///
/// ⚠️ Составное право <c>*.read.all</c> здесь НЕ раскрывается (AUTH-5.2): что в него входит,
/// объявляет каждый модуль, и раскрытие приходит вместе со вторым модулем. До тех пор владелец
/// составного права имеет именно его — и ни одного чужого права по умолчанию. Раскрыть заранее
/// «как будет» значило бы выдать доступ, которого никто не проверял.
/// </summary>
public sealed class PermissionHandler(IUserPermissions permissions)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var granted = await permissions.ForAsync(context.User, CancellationToken.None);
        if (granted.Contains(requirement.Code, StringComparer.OrdinalIgnoreCase))
            context.Succeed(requirement);
    }
}

/// <summary>
/// Доступ к модулю: у пользователя есть хотя бы одно право этого модуля
/// (<c>id.document.read</c> открывает модуль <c>id</c>).
///
/// Отдельного права «войти в модуль» нет намеренно: оно завелось бы у каждой роли рядом с
/// настоящими правами и однажды разошлось бы с ними — роль с правом входа и без единого права
/// внутри видит пустые экраны, а роль с правами и без права входа не видит ничего. Здесь эти два
/// ответа не могут разойтись: доступ к модулю — следствие прав, а не вторая запись о том же.
///
/// Обратная сторона названа вслух: роль «Руководитель» с одним составным правом <c>*.read.all</c>
/// прав конкретного модуля не имеет — и модули ей не откроются, пока составное право не
/// раскрывается по модулям (AUTH-5.2, этап 2). Закрытая дверь до раскрытия честнее открытой.
/// </summary>
public sealed class ModuleAccessHandler(IUserPermissions permissions)
    : AuthorizationHandler<ModuleAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ModuleAccessRequirement requirement)
    {
        var prefix = requirement.Code + ".";
        var granted = await permissions.ForAsync(context.User, CancellationToken.None);
        if (granted.Any(code => code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            context.Succeed(requirement);
    }
}
