using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Код модуля — владелец его файлов (ТЗ CORE-37.2), и порты файлов строятся по нему при сборке
/// контейнера. Код, которого реестр файлов владельцем не примет, обязан остановить старт, а не первую
/// загрузку: та отказала бы уже после записи объекта в хранилище, оставив его сиротой.
/// </summary>
public class ModulePortCodesTests
{
    public static TheoryData<string> Unfit => new()
    {
        BlobOwner.Core,
        BlobOwner.Any,
        " ",
        new string('w', BlobOwner.MaxLength + 1),
    };

    [Theory]
    [MemberData(nameof(Unfit))]
    public void Код_не_годный_владельцем_файла_останавливает_старт(string code)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ModuleRegistry([new Named(code)], []));

        var refusal = Assert.Throws<InvalidOperationException>(() => services.AddModulePorts());

        Assert.Contains($"«{code}»", refusal.Message);
    }

    [Fact]
    public void Годный_код_получает_порт_файлов_по_ключу()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ModuleRegistry([new Named("work")], []));

        services.AddModulePorts();

        Assert.Contains(services, d => d.IsKeyedService && Equals(d.ServiceKey, "work")
                                       && d.ServiceType == typeof(IModuleBlobs));
    }

    private sealed class Named(string code) : IAppModule
    {
        public string Code => code;
        public string Title => "Проба";
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => [];
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
