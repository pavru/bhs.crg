using BHS.CRG.Api.Modules;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Work;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Разделы ядра, которыми пользуются модули (ТЗ AUTH-16.1, issue #1001): из них рабочее пространство
/// собирает навигацию. Опечатка в коде тихо убрала бы раздел из пространства — поэтому негодное
/// объявление останавливает сборку реестра модулей, то есть запуск.
/// </summary>
public class CoreSectionsTests
{
    [Fact]
    public void Объявления_модулей_сборки_годны()
    {
        // Реестр из настоящих модулей собирается — значит, их объявления прошли проверку.
        var registry = new ModuleRegistry([new IdModule(), new CostsModule(), new PlanModule(), new WorkModule()], []);

        Assert.All(registry.Enabled, m => Assert.NotEmpty(m.UsesSections));
        // Счета без строек и справочников: пространство «Счета» не показало бы, куда разносить счёт.
        Assert.Equal(["constructions", "catalog", "employees", "periods"], new CostsModule().UsesSections);
        // Планирование без строек — пространство без «Строек» и без карточки с «Работами» («Сметы»,
        // «График»); без справочников — без видов работ и номенклатуры, с которыми сопоставляют смету.
        // Учётного периода у смет нет; «Ход работ» дописывает plan-25 своей константой.
        Assert.Equal(["constructions", "catalog", "employees"], new PlanModule().UsesSections);
        // Учёт работ без строек — без назначений (WORK-21); без справочников — без классификатора видов
        // работ, из которого строка отчёта выбирает работу (CORE-8); монтажник — сотрудник (WORK-47).
        // Учётный период дописывает work-19, «Ход работ» — work-27, каждый своей строкой.
        Assert.Equal(["constructions", "catalog", "employees"], new WorkModule().UsesSections);
        // Сотрудники — в каждом пространстве со справочниками: после C15 «Общие данные» сотрудника не
        // пишут, и пространство без раздела «Сотрудники» осталось бы без двери к ним (work-01).
        Assert.All(registry.Enabled.Where(m => m.UsesSections.Contains("catalog")),
            m => Assert.Contains("employees", m.UsesSections));
    }

    [Fact]
    public void Неизвестный_раздел_останавливает_запуск()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new ModuleRegistry([new Declaring(["constructions", "стройки"])], []));

        Assert.Contains("стройки", error.Message);
        Assert.Contains("costs", error.Message);
    }

    /// <summary>
    /// Выключенный модуль проверяется тоже: включат его обратно — и негодное объявление всплыло бы у
    /// заказчика, а не при сборке поставки.
    /// </summary>
    [Fact]
    public void Объявление_выключенного_модуля_проверяется_тоже()
    {
        Assert.Throws<InvalidOperationException>(
            () => new ModuleRegistry([new IdModule()], [new Declaring(["warehouse"])]));
    }

    [Fact]
    public void Повтор_раздела_останавливает_запуск()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new ModuleRegistry([new Declaring(["periods", "periods"])], []));

        Assert.Contains("дважды", error.Message);
    }

    private sealed class Declaring(IReadOnlyList<string> sections) : IAppModule
    {
        public string Code => "costs";
        public string Title => "Счета и накладные";
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => [];
        public IReadOnlyList<string> UsesSections => sections;
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
