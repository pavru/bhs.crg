using BHS.CRG.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Право объявляется вместе с объяснением — иначе приложение не стартует (issue #944, ТЗ AUTH-1,
/// AUTH-5).
///
/// Требование выглядит формальным ровно до первого редактора ролей. Администратор раздаёт доступ
/// галками, и галка с подписью <c>costs.invoice.approve</c> не даёт ему ни одного основания решить,
/// ставить её или нет. Написать объяснение «потом» не выйдет: потом его не напишет никто, а
/// проверить нечем — приложение работает и без него.
/// </summary>
public class PermissionCatalogTests
{
    [Fact]
    public void Permission_without_explanation_stops_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new PermissionCatalog([new AppPermission("costs.invoice.approve", "", "счета")]));

        Assert.Contains("costs.invoice.approve", ex.Message);
        Assert.Contains("что право даёт", ex.Message);
    }

    [Fact]
    public void Permission_without_data_scope_stops_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new PermissionCatalog([new AppPermission("costs.invoice.approve", "согласовать счёт", " ")]));

        Assert.Contains("к каким данным", ex.Message);
    }

    /// <summary>
    /// Код обязан быть вида «модуль.объект.действие»: по первой части работают ворота модуля, и
    /// право без модуля в имени не к чему привязать.
    /// </summary>
    [Theory]
    [InlineData("invoices")]
    [InlineData("costs.invoice")]
    [InlineData("costs.invoice.approve.now")]
    [InlineData("costs..approve")]
    public void Malformed_code_stops_startup(string code)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new PermissionCatalog([new AppPermission(code, "что-то", "какие-то данные")]));

        Assert.Contains("модуль.объект.действие", ex.Message);
    }

    /// <summary>
    /// Отказ перечисляет ВСЕ негодные объявления сразу: чинить по одному на перезапуск — это
    /// столько перезапусков, сколько ошибок.
    /// </summary>
    [Fact]
    public void All_broken_declarations_are_named_at_once()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new PermissionCatalog(
        [
            new AppPermission("costs.invoice.approve", "", "счета"),
            new AppPermission("плохой-код", "что-то", "данные"),
        ]));

        Assert.Contains("costs.invoice.approve", ex.Message);
        Assert.Contains("плохой-код", ex.Message);
    }

    /// <summary>
    /// Один код — одно объяснение. Два объявления означали бы, что в редакторе ролей у галки
    /// окажется случайное из двух, и какое именно — зависело бы от порядка модулей.
    /// </summary>
    [Fact]
    public void Duplicate_code_stops_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new PermissionCatalog(
        [
            new AppPermission("work.facts.read", "объёмы", "принятые объёмы"),
            new AppPermission("work.facts.read", "то же другими словами", "они же"),
        ]));

        Assert.Contains("дважды", ex.Message);
        Assert.Contains("work.facts.read", ex.Message);
    }

    /// <summary>Каталог собирается при подключении модулей, а не лениво: отказ обязан быть на старте.</summary>
    [Fact]
    public void Catalog_is_built_when_modules_are_added()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(
            () => services.AddAppModules(configuration, [], new BrokenModule()));
    }

    /// <summary>Права выключенного модуля в каталог не попадают: выдавать их не за что.</summary>
    [Fact]
    public void Disabled_module_permissions_are_not_offered()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "id" })
            .Build();

        services.AddAppModules(configuration, [], new GoodModule("id"), new GoodModule("costs"));

        var catalog = services.BuildServiceProvider().GetRequiredService<PermissionCatalog>();

        Assert.Contains("id.thing.read", catalog.Codes);
        Assert.DoesNotContain("costs.thing.read", catalog.Codes);
    }

    private sealed class BrokenModule : TestModule
    {
        public override string Code => "id";
        public override IReadOnlyList<AppPermission> Permissions =>
            [new AppPermission("id.thing.read", "", "")];
    }

    private sealed class GoodModule(string code) : TestModule
    {
        public override string Code => code;
        public override IReadOnlyList<AppPermission> Permissions =>
            [new AppPermission($"{code}.thing.read", "видеть вещи", "вещи модуля")];
    }

    private abstract class TestModule : IAppModule
    {
        public abstract string Code { get; }
        public string Title => Code;
        public abstract IReadOnlyList<AppPermission> Permissions { get; }
        public IReadOnlyList<string> RoutePrefixes => ["/api/" + Code];
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
