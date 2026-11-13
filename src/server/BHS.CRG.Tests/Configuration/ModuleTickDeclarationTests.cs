using BHS.CRG.Api.Modules;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Объявление такта проверяется при старте (ТЗ WORK-41, work-08): такт без обработчика, повтор кода,
/// период короче минуты и чужая операция не стартуют, и отказ называет модуль и такт.
///
/// <para>Что проверка обработчиков действительно стоит на старте приложения, а не только в каталоге,
/// сверяет живой хост — <c>ModuleTickServiceTests.Такт_без_обработчика_не_даёт_приложению_стартовать</c>.</para>
/// </summary>
public class ModuleTickDeclarationTests
{
    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);

    [Fact]
    public void Такт_без_обработчика_не_стартует_и_называет_модуль_такт_и_операцию()
    {
        var catalog = new ModuleTickCatalog([new TickingModule("work",
            new ModuleTick("autosubmit", "Автоподача отчётов", "work.autosubmit", Five))]);

        var refusal = Assert.Throws<InvalidOperationException>(
            () => catalog.EnsureHandled([new Handler("work.другое")]));

        Assert.Contains("«work»", refusal.Message);
        Assert.Contains("«autosubmit»", refusal.Message);
        Assert.Contains("Автоподача отчётов", refusal.Message);
        Assert.Contains("work.autosubmit", refusal.Message);
    }

    [Fact]
    public void Два_такта_одного_кода_не_стартуют_и_называют_код()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => new ModuleTickCatalog([new TickingModule("work",
            new ModuleTick("autosubmit", "Автоподача отчётов", "work.autosubmit", Five),
            new ModuleTick("AutoSubmit", "Автоподача ещё раз", "work.autosubmit2", Five))]));

        Assert.Contains("модуль «work»", refusal.Message);
        Assert.Contains("такт «autosubmit» объявлен дважды", refusal.Message);
    }

    [Fact]
    public void Период_короче_минуты_не_стартует()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => new ModuleTickCatalog([new TickingModule("work",
            new ModuleTick("autosubmit", "Автоподача отчётов", "work.autosubmit", TimeSpan.FromSeconds(30)))]));

        Assert.Contains("модуль «work»", refusal.Message);
        Assert.Contains("такт «autosubmit»", refusal.Message);
        Assert.Contains("короче шага службы", refusal.Message);
    }

    /// <summary>
    /// Такт ставит только работу своего модуля. Чужая операция прошла бы постановку (её модуль включён),
    /// но тогда выключение модуля-владельца операции молча ломало бы такт соседа.
    /// </summary>
    [Fact]
    public void Операция_чужого_модуля_не_стартует()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => new ModuleTickCatalog([new TickingModule("work",
            new ModuleTick("autosubmit", "Автоподача отчётов", "costs.проба", Five))]));

        Assert.Contains("модуль «work»", refusal.Message);
        Assert.Contains("операция «costs.проба» не модуля «work»", refusal.Message);
    }

    /// <summary>Регистр кода операции — как у поиска обработчика: «Work.autosubmit» своему модулю не чужая.</summary>
    [Fact]
    public void Операция_своего_модуля_узнаётся_без_учёта_регистра() =>
        _ = new ModuleTickCatalog([new TickingModule("work",
            new ModuleTick("autosubmit", "Автоподача отчётов", "Work.autosubmit", Five))]);

    [Fact]
    public void Годное_объявление_проходит_и_цель_такта_постоянна()
    {
        var tick = new ModuleTick("autosubmit", "Автоподача отчётов", "work.autosubmit", Five);
        var catalog = new ModuleTickCatalog([new TickingModule("work", tick)]);
        catalog.EnsureHandled([new Handler("work.autosubmit")]);

        var entry = Assert.Single(catalog.All);
        // Цель — из «модуль:код», а не случайная: по ней находятся прежние задачи такта после перезапуска.
        Assert.Equal(entry.TargetId, ModuleTickCatalog.TargetOf("work", "autosubmit"));
        Assert.Equal(entry.TargetId, new ModuleTickCatalog([new TickingModule("work", tick)]).All[0].TargetId);
        Assert.NotEqual(entry.TargetId, ModuleTickCatalog.TargetOf("work", "другой"));
        Assert.NotEqual(entry.TargetId, ModuleTickCatalog.TargetOf("plan", "autosubmit"));
        // UUID версии 5: по ней цель такта отличима глазом от случайных целей задач (версия 4).
        Assert.Equal('5', entry.TargetId.ToString()[14]);
    }

    /// <summary>
    /// Такт узнаётся по цели, операции и ПУСТОМУ владельцу: работа, поставленная человеком над тем же
    /// идентификатором, — не такт, и подпись «Система» спрятала бы того, кто нажал.
    /// </summary>
    [Fact]
    public void Задача_такта_узнаётся_только_без_владельца()
    {
        var catalog = new ModuleTickCatalog([new TickingModule("work",
            new ModuleTick("autosubmit", "Автоподача отчётов", "work.autosubmit", Five))]);
        var target = ModuleTickCatalog.TargetOf("work", "autosubmit");

        Assert.NotNull(catalog.Find(target, "work.autosubmit", Guid.Empty));
        Assert.Null(catalog.Find(target, "work.autosubmit", Guid.NewGuid()));
        Assert.Null(catalog.Find(target, "work.другое", Guid.Empty));
        Assert.Null(catalog.Find(Guid.NewGuid(), "work.autosubmit", Guid.Empty));
    }

    /// <summary>Объявления поставки годны — иначе приложение не стартовало бы у заказчика.</summary>
    [Fact]
    public void Такты_модулей_поставки_объявлены_годно() =>
        _ = new ModuleTickCatalog(BHS.CRG.Api.Modules.DeliveredModules.All());

    private sealed class Handler(string operation) : IModuleJobHandler
    {
        public string Operation => operation;
        public Task RunAsync(ModuleJobRun run, CancellationToken ct) => Task.CompletedTask;
    }
}

/// <summary>Пробный модуль, объявляющий такты. Только объявление: служб и адресов у него нет.</summary>
internal sealed class TickingModule(string code, params ModuleTick[] ticks) : IAppModule
{
    public string Code => code;
    public string Title => "Пробный модуль " + code;
    public IReadOnlyList<AppPermission> Permissions => [];
    public IReadOnlyList<string> RoutePrefixes => [];
    public IReadOnlyList<ModuleTick> Ticks => ticks;
    public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
    public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
}
