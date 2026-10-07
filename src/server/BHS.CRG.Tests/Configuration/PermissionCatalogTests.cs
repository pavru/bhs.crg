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

    // ── Составное «читать всё» (задача A3 этапа 2, issue #1074, ТЗ AUTH-5.2) ──────────────────

    /// <summary>
    /// Право модуля без пометки останавливает старт и названо в отказе. Иначе оно молча выпало бы
    /// из составного: «Руководитель» не увидел бы раздела, а выглядело бы это как «прав нет».
    /// </summary>
    [Fact]
    public void Module_permission_without_read_all_mark_stops_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAppModules(
            new ConfigurationBuilder().Build(), [], new UnmarkedModule()));

        Assert.Contains("id.thing.export", ex.Message);
        Assert.Contains("читать всё", ex.Message);
    }

    /// <summary>
    /// Проверяются права модулей СБОРКИ, а не включённых: иначе отказ пришёл бы на том экземпляре,
    /// где модуль включили, — то есть у заказчика, а не у автора права.
    /// </summary>
    [Fact]
    public void Mark_is_required_for_disabled_modules_too()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "costs" })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAppModules(
            configuration, [], new UnmarkedModule(), new GoodModule("costs")));

        Assert.Contains("id.thing.export", ex.Message);
    }

    /// <summary>У прав ядра пометки нет, и это не отказ: составное право раскрывается по модулям.</summary>
    [Fact]
    public void Core_permission_needs_no_mark()
    {
        var services = new ServiceCollection().AddAppModules(
            new ConfigurationBuilder().Build(),
            [new AppPermission("core.catalog.read", "справочники", "общие данные")],
            new GoodModule("id"));

        Assert.Empty(services.BuildServiceProvider().GetRequiredService<PermissionCatalog>()
            .Expand(["core.catalog.read"]).Except(["core.catalog.read"]));
    }

    [Fact]
    public void Excluded_mark_without_reason_stops_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new PermissionCatalog(
            [new AppPermission("costs.invoice.pay", "отмечать оплату", "суммы", ReadAll: ReadAllMark.Out(" "))]));

        Assert.Contains("costs.invoice.pay", ex.Message);
        Assert.Contains("почему право не входит", ex.Message);
    }

    /// <summary>
    /// Раскрытие: владелец составного права получает входящие права — и ни одного не входящего.
    /// Второе важнее первого: это и есть «прав на правку составное право не даёт».
    /// </summary>
    [Fact]
    public void Read_all_expands_to_included_permissions_only()
    {
        var catalog = Composite();

        var granted = catalog.Expand([PermissionCatalog.ReadAllCode, "core.files.use"]);

        Assert.Equal(
            ["*.read.all", "core.files.use", "costs.invoice.read", "costs.report.read"],
            granted.Order(StringComparer.Ordinal));
    }

    /// <summary>Без составного права набор не меняется: чужое чтение не раскрывает ничего.</summary>
    [Fact]
    public void Nothing_expands_without_the_composite_permission()
    {
        var granted = Composite().Expand(["costs.invoice.read"]);

        Assert.Equal(["costs.invoice.read"], granted);
    }

    /// <summary>
    /// Раскрытие открывает модуль: доступ к модулю судят по началу кода права, и без раскрытия
    /// набор из одного <c>*.read.all</c> не открывал ни одного модуля.
    /// </summary>
    [Fact]
    public void Expanded_read_all_opens_the_module()
    {
        IReadOnlyCollection<string> bare = [PermissionCatalog.ReadAllCode];

        Assert.False(ModuleAccess.IsOpen("costs", bare));
        Assert.True(ModuleAccess.IsOpen("costs", Composite().Expand(bare)));
    }

    private static PermissionCatalog Composite() => new(
    [
        new AppPermission(PermissionCatalog.ReadAllCode, "читать всё", "данные модулей"),
        new AppPermission("core.files.use", "файлы", "файлы хранилища"),
        new AppPermission("costs.invoice.read", "видеть счета", "счета", ReadAll: ReadAllMark.In),
        new AppPermission("costs.report.read", "отчёты", "суммы", ReadAll: ReadAllMark.In),
        new AppPermission("costs.invoice.edit", "править счета", "счета", ReadAll: ReadAllMark.Out("правит счета")),
    ]);

    private sealed class UnmarkedModule : TestModule
    {
        public override string Code => "id";
        public override IReadOnlyList<AppPermission> Permissions =>
            [new AppPermission("id.thing.export", "выгружать вещи", "вещи модуля")];
    }

    private sealed class BrokenModule : TestModule
    {
        public override string Code => "id";
        public override IReadOnlyList<AppPermission> Permissions =>
            [new AppPermission("id.thing.read", "", "", ReadAll: ReadAllMark.In)];
    }

    private sealed class GoodModule(string code) : TestModule
    {
        public override string Code => code;
        public override IReadOnlyList<AppPermission> Permissions =>
            [new AppPermission($"{code}.thing.read", "видеть вещи", "вещи модуля", ReadAll: ReadAllMark.In)];
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
