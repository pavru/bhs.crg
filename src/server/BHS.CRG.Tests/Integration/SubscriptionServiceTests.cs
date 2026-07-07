using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Subscriptions;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Подписки + резолв получателей с наследованием по иерархии (комплект наследует раздел и стройку).
/// </summary>
[Collection("Integration")]
public class SubscriptionServiceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private ISubscriptionService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<ISubscriptionService>();

    private async Task<Guid> CreateUserAsync(string display)
    {
        using var scope = fixture.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = display };
        var r = await um.CreateAsync(user, "Passw0rd!");
        Assert.True(r.Succeeded);
        return user.Id;
    }

    private async Task<(Guid constructionId, Guid sectionId, Guid setId)> HierarchyAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var c = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var s = await m.Send(new CreateSectionCommand(c.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(s.Id, "Комплект"));
        return (c.Id, s.Id, set.Id);
    }

    [Fact]
    public async Task Resolve_ForSet_IncludesConstructionAndSectionSubscribers()
    {
        var (conId, secId, setId) = await HierarchyAsync();
        var conUser = await CreateUserAsync("Стройка-подписчик");
        var secUser = await CreateUserAsync("Раздел-подписчик");
        var setUser = await CreateUserAsync("Комплект-подписчик");
        var other = await CreateUserAsync("Чужой");

        using (var scope = fixture.Services.CreateScope())
        {
            var svc = Svc(scope);
            await svc.AddAsync(conUser, CatalogScope.Construction, conId);
            await svc.AddAsync(secUser, CatalogScope.Section, secId);
            await svc.AddAsync(setUser, CatalogScope.Set, setId);
            await svc.AddAsync(other, CatalogScope.Construction, Guid.NewGuid()); // другая стройка
        }

        using var s2 = fixture.Services.CreateScope();
        var recipients = await Svc(s2).ResolveRecipientsAsync(CatalogScope.Set, setId);
        var ids = recipients.Select(r => r.UserId).ToHashSet();

        Assert.Contains(conUser, ids);   // унаследован со стройки
        Assert.Contains(secUser, ids);   // унаследован с раздела
        Assert.Contains(setUser, ids);   // прямой
        Assert.DoesNotContain(other, ids); // чужая стройка
    }

    [Fact]
    public async Task Resolve_ForConstruction_ExcludesSetLevelSubscribers()
    {
        var (conId, _, setId) = await HierarchyAsync();
        var conUser = await CreateUserAsync("Стройка");
        var setUser = await CreateUserAsync("Комплект");

        using (var scope = fixture.Services.CreateScope())
        {
            await Svc(scope).AddAsync(conUser, CatalogScope.Construction, conId);
            await Svc(scope).AddAsync(setUser, CatalogScope.Set, setId);
        }

        using var s2 = fixture.Services.CreateScope();
        var ids = (await Svc(s2).ResolveRecipientsAsync(CatalogScope.Construction, conId)).Select(r => r.UserId).ToHashSet();

        Assert.Contains(conUser, ids);
        Assert.DoesNotContain(setUser, ids); // нижний уровень не поднимается вверх
    }

    [Fact]
    public async Task Add_Idempotent_ListReturnsSingle()
    {
        var (conId, _, _) = await HierarchyAsync();
        var user = await CreateUserAsync("Подписчик");

        using var scope = fixture.Services.CreateScope();
        await Svc(scope).AddAsync(user, CatalogScope.Construction, conId);
        await Svc(scope).AddAsync(user, CatalogScope.Construction, conId); // повтор

        var list = await Svc(scope).ListAsync(CatalogScope.Construction, conId);
        Assert.Single(list);
        Assert.Equal(user, list[0].UserId);
    }
}
