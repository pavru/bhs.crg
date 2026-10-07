using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Tables;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Сторожа объявления готовых отборов таблицы (issue #1186).
///
/// <para>Готовый отбор называет колонку и значение её перечня, и держится это только проверкой при
/// старте. Без неё переименованное слово перечня превратило бы отбор в отказ на первом же открытии
/// реестра — у пользователя, а число на чипе не посчиталось бы вовсе.</para>
/// </summary>
public class ModuleTableShortcutTests
{
    [Theory]
    [MemberData(nameof(Broken))]
    public void Негодный_готовый_отбор_назван_и_останавливает_старт(string what, ModuleTableShortcut shortcut, string expected)
    {
        var problems = Table(shortcut).Problems();
        Assert.True(problems.Any(p => p.StartsWith("готовый отбор ") && p.Contains(expected)),
            $"{what}: ждали «{expected}», а названо: {string.Join("; ", problems)}");

        // Через AddAppModules — тем путём, которым идёт настоящий запуск.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "probe" })
            .Build();
        var refused = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddAppModules(config, new ProbeModule(Table(shortcut))));
        Assert.Contains("«probe.docs»", refused.Message);
        Assert.Contains(expected, refused.Message);
    }

    public static TheoryData<string, ModuleTableShortcut, string> Broken() => new()
    {
        { "нет кода", Good() with { Code = " " }, "не назван код" },
        { "нет подписи", Good() with { Title = "" }, "нет подписи для человека" },
        { "нет колонки", Good() with { Column = "" }, "не названа колонка" },
        { "колонки нет в таблице", Good() with { Column = "Порядок" }, "колонки «Порядок» в таблице нет" },
        // Опечатка в слове перечня: без проверки отбор отказал бы у человека, а не у сборщика.
        { "значение вне перечня", Good() with { Value = "Есть" }, "значения «Есть» нет в перечне колонки «Ссылки»" },
        { "колонка не выбор", Good() with { Column = "Номер" }, "она не выбор из закрытого перечня" },
        { "колонка зависит от отбора", Good() with { Column = "Доля" }, "а по ней не отбирают" },
        // Тому, у кого права на колонку нет, отбор по ней отказывает — число ему не посчиталось бы.
        { "колонка закрыта правом", Good() with { Column = "Тайна" }, "она закрыта правом «probe.money»" },
    };

    [Fact]
    public void Годный_готовый_отбор_проблем_не_называет()
    {
        Assert.Empty(Table(Good()).Problems());
        Assert.Empty(Table(Good(), Good() with { Code = "second", Value = "есть, но заперто" }).Problems());
    }

    [Fact]
    public void Два_готовых_отбора_с_одним_кодом_и_пустое_место_названы()
    {
        var problems = Table(Good(), Good() with { Code = "LOST" }, null!).Problems();

        Assert.Contains(problems, p => p.Contains("готовый отбор «lost» объявлен дважды"));
        Assert.Contains(problems, p => p.Contains("в списке готовых отборов пустое место"));
    }

    /// <summary>
    /// Настоящие отборы счетов: оба стоят на слове «есть» своей колонки, оба — только тому, кто счёт
    /// может править, и архив объявлен тихим.
    /// </summary>
    [Fact]
    public void Готовые_отборы_счетов_объявлены_годно()
    {
        var invoices = Assert.Single(new ModuleTableCatalog([new CostsModule()]).All).Table;

        Assert.Equal(["lost", "archived"], invoices.Shortcuts!.Select(s => s.Code));
        Assert.All(invoices.Shortcuts!, s =>
        {
            Assert.Empty(s.Problems(invoices.Columns));
            Assert.Equal(InvoiceTable.TroubleFixable, s.Value);
            Assert.Equal("costs.invoice.edit", s.Requires);
            // Колонка отбора приходит только по требованию: иначе опрос ядра ехал бы в каждое чтение.
            Assert.True(invoices.Columns.Single(c => c.Key == s.Column).OnDemand);
        });
        Assert.Equal([InvoiceTable.LostKey, InvoiceTable.ArchivedKey], invoices.Shortcuts!.Select(s => s.Column));
        Assert.Equal([false, true], invoices.Shortcuts!.Select(s => s.Quiet));
    }

    private static ModuleTableShortcut Good() => new("lost", "Потерянные ссылки", "Ссылки", "есть", "Замените запись");

    private static ModuleTable Table(params ModuleTableShortcut[] shortcuts) => new(
        "docs", "Документы", "документ", "probe", ModuleTableIsolation.None, "Отдаёт всё",
        [
            new("Номер", "Номер", ModuleTableColumnKind.Text),
            new("Доля", "Доля", ModuleTableColumnKind.Number, DependsOnFilter: true),
            new("Ссылки", "Ссылки", ModuleTableColumnKind.Choice, Options: ["есть", "есть, но заперто"]),
            new("Тайна", "Тайна", ModuleTableColumnKind.Choice, "probe.money", "тайны", Options: ["есть"]),
        ],
        typeof(ProbeRows), Shortcuts: shortcuts);

    private sealed class ProbeRows : IModuleTableRows
    {
        public Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct) =>
            Task.FromResult(new ModuleTablePage([], 0, new Dictionary<string, TableTotal>()));
    }

    private sealed class ProbeModule(ModuleTable table) : IAppModule
    {
        public string Code => "probe";
        public string Title => "Проба";
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => ["/api/probe"];
        public IReadOnlyList<ModuleTable> Tables => [table];

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
            services.AddScoped<ProbeRows>();

        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
