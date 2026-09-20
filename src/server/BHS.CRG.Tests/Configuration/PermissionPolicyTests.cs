using BHS.CRG.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Options;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Политики собираются по имени (issue #946, ТЗ AUTH-8): <c>perm:&lt;право&gt;</c> и
/// <c>module:&lt;код&gt;</c> не перечисляются при запуске, иначе каждое новое право модуля
/// требовало бы правки в общем коде.
/// </summary>
public class PermissionPolicyTests
{
    private static AppPolicyProvider Provider() =>
        new(Options.Create(new AuthorizationOptions()));

    [Fact]
    public async Task Permission_policy_is_built_from_its_name()
    {
        var policy = await Provider().GetPolicyAsync(AppPolicies.Permission("core.users.manage"));

        var requirement = Assert.Single(policy!.Requirements.OfType<PermissionRequirement>());
        Assert.Equal("core.users.manage", requirement.Code);
    }

    [Fact]
    public async Task Module_policy_is_built_from_its_name()
    {
        var policy = await Provider().GetPolicyAsync(AppPolicies.Module("id"));

        var requirement = Assert.Single(policy!.Requirements.OfType<ModuleAccessRequirement>());
        Assert.Equal("id", requirement.Code);
    }

    /// <summary>
    /// Политика всегда требует и входа: требование права само по себе пропустило бы анонима, если
    /// бы счётчик прав однажды ответил ему непустым набором. Вход — первая половина ворот.
    /// </summary>
    [Fact]
    public async Task Built_policy_also_requires_a_signed_in_user()
    {
        var policy = await Provider().GetPolicyAsync(AppPolicies.Module("id"));

        Assert.Contains(policy!.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    /// <summary>
    /// Имя без кода — отказ, а не политика без требований. Такие ворота отвечают ровно как
    /// настоящие и не проверяют ничего: по ответу приложения их не отличить.
    /// </summary>
    [Theory]
    [InlineData("perm:")]
    [InlineData("module:")]
    [InlineData("perm:   ")]
    public async Task Policy_without_a_code_is_refused(string policyName)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider().GetPolicyAsync(policyName));

        Assert.Contains(policyName.Trim(), ex.Message);
    }

    /// <summary>Чужие имена достаются обычному механизму: политика «Admin» жива и работает.</summary>
    [Fact]
    public async Task Other_names_are_left_to_the_default_provider()
    {
        var options = new AuthorizationOptions();
        options.AddPolicy("Admin", p => p.RequireRole("Admin"));

        var policy = await new AppPolicyProvider(Options.Create(options)).GetPolicyAsync("Admin");

        Assert.NotNull(policy);
        Assert.Empty(policy.Requirements.OfType<PermissionRequirement>());
    }
}
