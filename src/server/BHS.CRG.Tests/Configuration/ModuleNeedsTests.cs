using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Потребности модулей в справочниках ядра (ТЗ TYPE-23, issue #1101): негодное объявление
/// останавливает запуск, а необъявленное порт не читает.
///
/// <para>Объявление здесь — граница, а не памятка. Экран сопоставления строится по нему, и порт
/// отвечает только на него: разойдись они, администратор не увидел бы справочник, который код читает.</para>
/// </summary>
public class ModuleNeedsTests
{
    private const string Role = FunctionalTag.RoleOrganization;
    private static readonly TagCatalog Tags = TagCatalog.Build(TagRegistry.Core, []);

    [Fact]
    public void Объявленное_читается_необъявленное_нет()
    {
        var needs = Needs(new ModuleRoleNeed(Role, "Поставщик", "Счёт не завести.",
            [new(FunctionalTag.OrgTaxId, Required: true, "Поставщик не находится по ИНН.")]));

        needs.EnsureDeclared(Role);
        needs.EnsureDeclared(Role, FunctionalTag.OrgTaxId);

        // Роль есть в реестре, но этот состав модулей её не объявил.
        Assert.Throws<InvalidOperationException>(() => needs.EnsureDeclared(FunctionalTag.RoleNomenclature));
        // Тэг этой же роли, но не объявленный: читать его модулю нечем.
        Assert.Throws<InvalidOperationException>(() => needs.EnsureDeclared(Role, FunctionalTag.OrgName));
        // Прежняя адресация — кодом типа.
        Assert.Throws<InvalidOperationException>(() => needs.EnsureDeclared("Организация"));
    }

    /// <summary>
    /// Потолок носителей есть и у профиля уровня, но справочником он не является: роль — признак
    /// явный, а не выводимый из потолка.
    /// </summary>
    [Theory]
    [InlineData("role.organisation")]              // опечатка
    [InlineData(FunctionalTag.ProfileConstruction)] // тэг типа с потолком 1, но не справочник
    [InlineData(FunctionalTag.OrgTaxId)]            // тэг поля
    public void Роль_которой_нет_в_реестре_останавливает_запуск(string role)
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => Needs(new ModuleRoleNeed(role, "Зачем", "Что не работает.")));

        Assert.Contains(role, refusal.Message);
    }

    [Theory]
    [InlineData(FunctionalTag.Identity)]         // общий тэг поля, к роли не относится
    [InlineData(FunctionalTag.RoleNomenclature)] // тэг типа
    [InlineData("org.kpp")]                      // такого тэга нет
    public void Тэг_не_этой_роли_останавливает_запуск(string tag)
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => Needs(new ModuleRoleNeed(Role, "Зачем", "Что не работает.", [new(tag, true, "Следствие.")])));

        Assert.Contains(tag, refusal.Message);
    }

    /// <summary>Строка «не назначено» без следствия не говорит администратору, срочно ли это.</summary>
    [Fact]
    public void Потребность_без_слов_о_следствии_останавливает_запуск()
    {
        Assert.Throws<InvalidOperationException>(() => Needs(new ModuleRoleNeed(Role, "Зачем", " ")));
        Assert.Throws<InvalidOperationException>(() => Needs(new ModuleRoleNeed(Role, "", "Что не работает.")));
        Assert.Throws<InvalidOperationException>(() => Needs(
            new ModuleRoleNeed(Role, "Зачем", "Что не работает.", [new(FunctionalTag.OrgTaxId, true, "")])));
    }

    [Fact]
    public void Тэг_названный_дважды_останавливает_запуск()
    {
        Assert.Throws<InvalidOperationException>(() => Needs(new ModuleRoleNeed(Role, "Зачем", "Что не работает.",
        [
            new(FunctionalTag.OrgTaxId, true, "Следствие."),
            new(FunctionalTag.OrgTaxId, false, "Другое следствие."),
        ])));
    }

    /// <summary>
    /// Справочник, в который модуль ПИШЕТ, обязан стоять и в потребностях: иначе поле, в которое он
    /// кладёт значение, администратору негде было бы сопоставить.
    /// </summary>
    [Fact]
    public void Заведение_записей_без_потребности_в_том_же_поле_останавливает_запуск()
    {
        var module = new Declaring(
            [new ModuleRoleNeed(Role, "Зачем", "Что не работает.", [new(FunctionalTag.OrgTaxId, true, "Следствие.")])],
            [new ModuleIntakeType(Role, FunctionalTag.OrgName, FunctionalTag.OrgTaxId)]);

        var refusal = Assert.Throws<InvalidOperationException>(
            () => new ModuleNeeds(new ModuleRegistry([module], []), Tags));

        Assert.Contains(FunctionalTag.OrgName, refusal.Message);
    }

    /// <summary>
    /// Настоящее объявление модуля счетов проходит проверку — и называет ровно то, что его код читает:
    /// два справочника и два поля организаций.
    /// </summary>
    [Fact]
    public void Объявление_модуля_счетов_годно_и_называет_четыре_потребности()
    {
        var costs = new CostsModule();
        var needs = new ModuleNeeds(new ModuleRegistry([costs], []), Tags);

        Assert.Equal(
            new[] { FunctionalTag.RoleNomenclature, FunctionalTag.RoleOrganization },
            needs.All.Select(n => n.Need.Role).Order());
        Assert.Equal(
            new[] { FunctionalTag.OrgTaxId, FunctionalTag.OrgName }.Order(),
            needs.All.SelectMany(n => n.Need.Fields).Select(f => f.Tag).Order());
        Assert.All(needs.All.SelectMany(n => n.Need.Fields), f => Assert.True(f.Required));
    }

    private static ModuleNeeds Needs(params ModuleRoleNeed[] needs) =>
        new(new ModuleRegistry([new Declaring(needs, [])], []), Tags);

    private sealed class Declaring(IReadOnlyList<ModuleRoleNeed> needs, IReadOnlyList<ModuleIntakeType> intake) : IAppModule
    {
        public string Code => "probe";
        public string Title => "Проба";
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => [];
        public IReadOnlyList<ModuleRoleNeed> Needs => needs;
        public IReadOnlyList<ModuleIntakeType> IntakeTypes => intake;
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
