using BHS.CRG.Api.Modules.Tables;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Ключ строки таблицы — правила ЯДРА, на поддельной службе строк (задача G1e, issue #1092).
///
/// <para>Строку в боковой панели экран называет ключом и читает снова. Ключи называет служба модуля,
/// и ошибиться она может двумя способами, оба — молча: отдать ключей меньше, чем строк, и не заметить
/// ключ в запросе вовсе. В обоих случаях панель открыла бы ЧУЖУЮ строку под именем выбранной —
/// поэтому ядро на них останавливается, а не отвечает.</para>
/// </summary>
public class ModuleTableRowKeyTests
{
    [Fact]
    public async Task Ключи_строк_доходят_до_потребителя_в_порядке_строк()
    {
        var (table, refusal) = await Service(new ProbeRows(q => Page(["a", "b"], ["a", "b"])))
            .ReadAsync("probe.registry", Access, new TableRequest(), default);

        Assert.Null(refusal);
        Assert.Equal(["a", "b"], table!.Keys);
        Assert.Equal(["a", "b"], table.Rows.Select(r => r["Номер"]));
    }

    /// <summary>Служба без ключей — не ошибка: таблица читается, строка по ключу — отказ словами.</summary>
    [Fact]
    public async Task Служба_без_ключей_читается_а_строку_по_ключу_не_отдаёт()
    {
        var service = Service(new ProbeRows(q => Page(["a", "b"], null)));

        var (table, _) = await service.ReadAsync("probe.registry", Access, new TableRequest(), default);
        Assert.Null(table!.Keys);

        var (row, refusal) = await service.ReadAsync("probe.registry", Access, new TableRequest(Row: "a"), default);
        Assert.Null(row);
        Assert.Equal(StatusCodes.Status409Conflict, refusal!.Status);
        Assert.Contains("ключей строк не называет", refusal.Error);
    }

    [Fact]
    public async Task Строка_по_ключу_приходит_одна_и_ключ_доходит_до_службы()
    {
        string? asked = null;
        var service = Service(new ProbeRows(q =>
        {
            asked = q.Row;
            return Page(["b"], ["b"]);
        }));

        var (table, _) = await service.ReadAsync("probe.registry", Access, new TableRequest(Row: "b"), default);

        Assert.Equal("b", asked);
        Assert.Equal(["b"], table!.Keys);
    }

    /// <summary>Пустой ключ — «строку не просили»: до службы он не доходит условием «строки с пустым ключом».</summary>
    [Fact]
    public async Task Пустой_ключ_строки_значит_что_строку_не_просили()
    {
        string? asked = "не спрашивали";
        var service = Service(new ProbeRows(q =>
        {
            asked = q.Row;
            return Page(["a", "b"], ["a", "b"]);
        }));

        var (table, _) = await service.ReadAsync("probe.registry", Access, new TableRequest(Row: " "), default);

        Assert.Null(asked);
        Assert.Equal(2, table!.Rows.Count);
    }

    /// <summary>
    /// Служба, не заметившая ключа, отдаёт всю страницу — и первая её строка сошла бы за запрошенную.
    /// </summary>
    [Fact]
    public async Task Служба_не_заметившая_ключа_останавливает_чтение()
    {
        var service = Service(new ProbeRows(q => Page(["a", "b"], ["a", "b"])));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReadAsync("probe.registry", Access, new TableRequest(Row: "b"), default));
        Assert.Contains("«Реестр»", error.Message);
        Assert.Contains("другие строки", error.Message);
    }

    /// <summary>Ключей меньше, чем строк, — ключи сдвинулись бы на строку.</summary>
    [Fact]
    public async Task Ключей_не_столько_сколько_строк_останавливает_чтение()
    {
        var service = Service(new ProbeRows(q => Page(["a", "b"], ["a"])));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReadAsync("probe.registry", Access, new TableRequest(), default));
        Assert.Contains("строк — 2, а ключей — 1", error.Message);
    }

    private static readonly DataAccess Access = DataAccess.Of(Guid.NewGuid(), "Проба", ["probe"], ["probe"]);

    private static ModuleTablePage Page(string[] numbers, string[]? keys) => new(
        [.. numbers.Select(n => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Номер"] = n })],
        numbers.Length, new Dictionary<string, TableTotal>(), Keys: keys);

    /// <summary>
    /// Служба таблиц на поддельном модуле. База ей не нужна: к типам она идёт только за полями схемы, а
    /// у таблицы без типа записи их нет.
    /// </summary>
    private static ModuleTableService Service(ProbeRows rows)
    {
        var table = new ModuleTable(
            "registry", "Реестр", "запись", "probe", ModuleTableIsolation.None, "Отдаёт всё",
            [new("Номер", "Номер", ModuleTableColumnKind.Text)], typeof(ProbeRows));
        var services = new ServiceCollection().AddSingleton(rows).BuildServiceProvider();
        return new ModuleTableService(new ModuleTableCatalog([new ProbeModule(table)]), null!, services);
    }

    private sealed class ProbeRows(Func<ModuleTableQuery, ModuleTablePage> read) : IModuleTableRows
    {
        public Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct) =>
            Task.FromResult(read(query));
    }

    private sealed class ProbeModule(ModuleTable table) : IAppModule
    {
        public string Code => "probe";
        public string Title => "Проба";
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => ["/api/probe"];
        public IReadOnlyList<ModuleTable> Tables => [table];

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
