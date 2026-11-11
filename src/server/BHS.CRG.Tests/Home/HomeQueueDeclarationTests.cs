using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Home;

namespace BHS.CRG.Tests.Home;

/// <summary>
/// Объявления очередей Главной (shell-08, ТЗ UI-34): что каталог отвергает негодное и что перепись
/// клиента совпадает с объявлениями сборки — иначе новая очередь пришла бы карточкой без адреса.
/// </summary>
public class HomeQueueDeclarationTests
{
    private static readonly string Census = RepoPaths.File("src", "client", "src", "shared", "ui", "homeQueues.json");

    [Fact]
    public void Перепись_клиента_равна_объявлениям_сборки()
    {
        var declared = DeliveredModules.All()
            .SelectMany(m => m.HomeQueues.Select(q => $"{m.Code}/{q.Code}"))
            .ToList();
        using var file = JsonDocument.Parse(File.ReadAllText(Census));
        var listed = file.RootElement.GetProperty("queues").EnumerateArray()
            .Select(q => $"{q.GetProperty("module").GetString()}/{q.GetProperty("id").GetString()}")
            .ToList();

        Assert.Equal(declared, listed);
    }

    [Fact]
    public void Объявления_сборки_годны()
    {
        var catalog = new HomeQueueCatalog(DeliveredModules.All());
        Assert.NotEmpty(catalog.All);
    }

    [Fact]
    public void Каталог_отвергает_негодное_объявление_с_названием()
    {
        var module = new Declaring(
            new ModuleHomeQueue("a", "Очередь", "Пояснение", ["probe.thing.read"]),
            new ModuleHomeQueue("a", "Повтор", "Пояснение", ["probe.thing.read"]),
            new ModuleHomeQueue("b", "Без права", "Пояснение", []),
            new ModuleHomeQueue("c", "Опечатка", "Пояснение", ["probe.thing.raed"]),
            new ModuleHomeQueue("d", "Длинно", new string('я', ModuleHomeQueue.HintLimit + 1), ["probe.thing.read"]));

        var e = Assert.Throws<InvalidOperationException>(() => new HomeQueueCatalog([module]));
        Assert.Contains("«a» объявлена дважды", e.Message);
        Assert.Contains("«b»: не названо ни одного права", e.Message);
        Assert.Contains("права «probe.thing.raed» в сборке нет", e.Message);
        Assert.Contains("«d»: пояснение длиннее", e.Message);
    }

    private sealed class Declaring(params ModuleHomeQueue[] queues) : IAppModule
    {
        public string Code => "probe";
        public string Title => "Проба";
        public IReadOnlyList<AppPermission> Permissions => [new("probe.thing.read", "Читать", "Пробу")];
        public IReadOnlyList<ModuleHomeQueue> HomeQueues => queues;
        public void RegisterServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services,
            Microsoft.Extensions.Configuration.IConfiguration configuration) { }
        public void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
        public IReadOnlyList<string> RoutePrefixes => [];
    }
}
