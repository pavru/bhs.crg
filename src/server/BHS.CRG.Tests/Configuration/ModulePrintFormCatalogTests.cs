using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Printing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

public class ModulePrintFormCatalogTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Startup_refuses_missing_and_duplicate_providers(int registrations)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Start(new ProbeModule(registrations)));
        Assert.Contains("Пробная форма", error.Message);
        Assert.Contains("ровно один поставщик контекста", error.Message);
    }

    [Fact]
    public void Startup_refuses_a_type_not_declared_by_the_module()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Start(new ProbeModule(1, declaresType: false)));
        Assert.Contains("Пробная форма", error.Message);
        Assert.Contains("RecordTypes", error.Message);
    }

    [Fact]
    public void Startup_accepts_one_provider_registered_by_the_owner()
    {
        using var services = Start(new ProbeModule(1)).BuildServiceProvider();
        using var scope = services.CreateScope();
        var entry = Assert.Single(services.GetRequiredService<ModulePrintFormCatalog>().All);
        Assert.IsType<MissingContext>(scope.ServiceProvider.GetRequiredKeyedService<IModulePrintContext>(entry.Form.TypeCode));
    }

    private static IServiceCollection Start(ProbeModule module) => new ServiceCollection().AddAppModules(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = module.Code }).Build(),
        module);

    private sealed class ProbeModule(int registrations, bool declaresType = true) : IAppModule
    {
        public string Code => "probe";
        public string Title => "Проба";
        public IReadOnlyList<AppPermission> Permissions =>
            [new("probe.form.read", "читать запись", "пробная запись", ReadAll: ReadAllMark.In)];
        public IReadOnlyList<string> RoutePrefixes => [];
        public IReadOnlyList<ModuleRecordType> RecordTypes => declaresType
            ? [new("ПробнаяЗапись", "Пробная запись", ModuleSchemaLevel.Closed, [])] : [];
        public IReadOnlyList<ModulePrintForm> PrintForms => [new("ПробнаяЗапись", "Пробная форма", "probe.form.read")];

        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            for (var index = 0; index < registrations; index++)
                services.AddKeyedScoped<IModulePrintContext, MissingContext>("ПробнаяЗапись");
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class MissingContext : IModulePrintContext
    {
        public Task<ModulePrintResult> BuildAsync(Guid recordId, IModuleUser user, CancellationToken ct) =>
            Task.FromResult<ModulePrintResult>(new ModulePrintResult.Missing());
    }
}
