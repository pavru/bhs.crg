using BHS.CRG.Api.Modules;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using MediatR;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Свой справочник на один тест (issue #1101): своя роль, свой тэг поля и порт, который их читает.
///
/// <para><b>Зачем.</b> Порт находит справочник по роли, а роль несёт один тип на всю базу. Тесты
/// порта заводят по типу на тест — и поделить «справочник организаций» общей тестовой базы они не
/// могут: второй носитель роли — уже нарушение. Поэтому у каждого теста роль своя.</para>
///
/// <para>Порт — НАСТОЯЩИЙ (<see cref="ModuleCatalogPort" />), собран руками с реестром, в котором
/// есть эта роль, и с модулем, который объявил её нужной. Подменено объявление, а не поведение.</para>
/// </summary>
internal sealed class TestDirectory
{
    private readonly TagCatalog _tags;
    private readonly ModuleNeeds _needs;

    public TestDirectory()
    {
        var mark = Guid.NewGuid().ToString("N")[..8];
        Role = $"role.test.{mark}";
        FieldTag = $"test.{mark}.field";

        _tags = TagCatalog.Build(TagRegistry.Core,
        [
            new(Role, "Тестовый справочник", "Справочник одного теста.", TagScope.Type, ["Composite"], Multiple: false,
                Restriction: new(MaxBearers: 1), Owner: Probe.ModuleCode, Directory: true),
            new(FieldTag, "Тестовое поле", "Поле справочника одного теста.", TagScope.Field,
                ["string", "text", "number", "primitive"], Multiple: false, Owner: Probe.ModuleCode, Role: Role),
        ]);
        _needs = new ModuleNeeds(new ModuleRegistry([new Probe(Role, FieldTag)], []), _tags);
    }

    /// <summary>Реестр тэгов, в котором эта роль есть, — для проверок, читающих роли из реестра.</summary>
    public TagCatalog Tags => _tags;

    /// <summary>Роль справочника — её называют порту вместо кода типа.</summary>
    public string Role { get; }

    /// <summary>Тэг поля, которое порт читает у записей.</summary>
    public string FieldTag { get; }

    /// <summary>Схема типа-справочника: роль — тэгом типа, тэг поля — на поле с ключом <paramref name="taggedKey" />.</summary>
    public string Schema(string taggedKey = "ИНН", string kind = "string") =>
        $$"""{"tags":["{{Role}}"],"fields":[{"key":"{{taggedKey}}","type":"{{kind}}","title":"{{taggedKey}}","tags":["{{FieldTag}}"]},{"key":"Наименование","type":"complex","title":"Наименование"}]}""";

    /// <summary>Схема справочника без помеченного поля.</summary>
    public string BareSchema() => $$"""{"tags":["{{Role}}"],"fields":[]}""";

    public IModuleCatalog Port(IServiceProvider services) => new ModuleCatalogPort(
        services.GetRequiredService<IMediator>(), services.GetRequiredService<IRepository<DocumentType>>(),
        _needs, _tags, services.GetRequiredService<ISimilarityRecordsLoader>());

    /// <summary>Модуль, объявивший потребность: без объявления порт не читает ничего.</summary>
    private sealed class Probe(string role, string fieldTag) : IAppModule
    {
        public const string ModuleCode = "probe";

        public string Code => ModuleCode;
        public string Title => "Проба";
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => [];

        public IReadOnlyList<ModuleRoleNeed> Needs =>
            [new(role, "Проверка порта", "Не работает проверка порта.",
                [new(fieldTag, Required: true, "Не работает чтение поля в проверке порта.")])];

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
