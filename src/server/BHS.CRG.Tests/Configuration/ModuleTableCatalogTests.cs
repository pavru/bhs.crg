using BHS.CRG.Api.Modules.Tables;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Tables;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Сторожа объявления таблиц модулей (задача G1b, issue #1089, ТЗ CORE-33).
/// </summary>
public class ModuleTableCatalogTests
{
    /// <summary>
    /// Таблица без параметра доступа — приложение НЕ СТАРТУЕТ и называет таблицу. Проверяется через
    /// <c>AddAppModules</c>, то есть тем путём, которым идёт настоящий запуск, а не конструктором
    /// каталога: сторож, который стоит в стороне от старта, не сторожит старт.
    /// </summary>
    [Fact]
    public void Таблица_без_параметра_доступа_роняет_старт_и_называет_таблицу()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "probe" })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddAppModules(config, new ProbeModule(Table(requires: ""))));

        Assert.Contains("«probe.registry»", error.Message);
        Assert.Contains("параметр доступа", error.Message);
    }

    /// <summary>Выключенный модуль проверяется тоже: его таблица отвечает «модуль выключен», а не пропадает.</summary>
    [Fact]
    public void Негодная_таблица_выключенного_модуля_тоже_роняет_старт()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "costs" })
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddAppModules(config, new CostsModule(), new ProbeModule(Table(requires: " "))));
    }

    /// <summary>Объявлена таблица, а служба строк не зарегистрирована — тоже отказ старта.</summary>
    [Fact]
    public void Таблица_без_службы_строк_роняет_старт()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "probe" })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddAppModules(config, new ProbeModule(Table(), registerReader: false)));

        Assert.Contains("probe.registry", error.Message);
    }

    /// <summary>
    /// Пустые ссылки в объявлении (служба строк, список колонок) — тоже названная ошибка таблицы, а не
    /// NullReferenceException без имени: модуль может собираться без проверки nullable (ревью PR #1130).
    /// Через <c>AddAppModules</c>: проверка служб стоит там РАНЬШЕ каталога и падала первой.
    /// </summary>
    [Fact]
    public void Пустая_служба_и_пустые_колонки_названы_а_не_роняют_старт_без_имени()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "probe" })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAppModules(
            config, new ProbeModule(Table() with { Reader = null!, Columns = null! })));

        Assert.Contains("«probe.registry»", error.Message);
        Assert.Contains("не названа служба строк", error.Message);
        Assert.Contains("ни одной колонки", error.Message);
    }

    /// <summary>
    /// Колонки по праву делают таблицу «разной у разных» — к печатной форме и сверке она не
    /// подключается, хотя построчной изоляции у неё нет (ревью PR #1130). Проверяется на настоящем
    /// объявлении счетов: суммы закрыты правом, значит и объявление набора обязано это нести.
    /// </summary>
    [Fact]
    public void Таблица_с_колонками_по_праву_не_подключается_к_печати_и_сверке()
    {
        var invoices = Assert.Single(new ModuleTableCatalog([new CostsModule()]).All);
        var declaration = new ModuleTableDataProvider(invoices, null!).Declaration;

        Assert.Equal(SystemDataSetIsolation.None, declaration.Isolation);
        Assert.True(declaration.ColumnsByRight);
        foreach (var what in new[] { "печатная форма", "сверка" })
            Assert.Contains(what, Assert.Throws<ConflictException>(() =>
                SystemDataSetRules.EnsureShared(declaration, what)).Message);

        // Без колонок по праву — по-прежнему годится.
        var plain = new ModuleTableDataProvider(new("probe", "Проба", Table()), null!).Declaration;
        Assert.False(plain.ColumnsByRight);
        SystemDataSetRules.EnsureShared(plain, "печатная форма");
    }

    /// <summary>
    /// Источник таблицы — только на уровне системы, и отказ стоит в самом поставщике: кандидатом ниже
    /// он не предлагается, но создать его можно запросом мимо списка (ревью PR #1130).
    /// </summary>
    [Fact]
    public async Task Источник_таблицы_ниже_уровня_системы_отказывает()
    {
        var invoices = Assert.Single(new ModuleTableCatalog([new CostsModule()]).All);
        var provider = new ModuleTableDataProvider(invoices, null!);

        foreach (var scope in new[] { CatalogScope.Construction, CatalogScope.Section, CatalogScope.Set })
        {
            var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() => provider.ProvideAsync(
                ModuleTableDataProvider.MarkerOf(invoices), scope, Guid.NewGuid(), null!, CancellationToken.None));
            Assert.Contains("«Система»", refusal.Message);
        }
    }

    /// <summary>Маркер — без учёта регистра, как адрес у экрана: один адрес на обоих путях.</summary>
    [Fact]
    public void Маркер_таблицы_узнаётся_без_учёта_регистра()
    {
        var invoices = Assert.Single(new ModuleTableCatalog([new CostsModule()]).All);
        var provider = new ModuleTableDataProvider(invoices, null!);

        Assert.True(provider.Handles("system:table:Costs.Invoices"));
        Assert.False(provider.Handles("system:table:costs.waybills"));
    }

    /// <summary>Ошибки собираются все, а не первая: чинить по одной на перезапуск дорого.</summary>
    [Fact]
    public void Каталог_называет_все_ошибки_разом()
    {
        var broken = Table(requires: "") with { Boundary = "", Isolation = ModuleTableIsolation.Unset };

        var error = Assert.Throws<InvalidOperationException>(() => new ModuleTableCatalog([new ProbeModule(broken)]));

        Assert.Contains("параметр доступа", error.Message);
        Assert.Contains("границы выдачи", error.Message);
        Assert.Contains("вид отбора", error.Message);
    }

    /// <summary>Право колонки без слов «на что» — отказ: иначе причина звучала бы «нет права на …».</summary>
    [Fact]
    public void Право_колонки_без_того_что_оно_закрывает_негодно()
    {
        var table = Table() with
        {
            Columns = [new("Сумма", "Сумма", ModuleTableColumnKind.Number, Requires: "probe.money.read")],
        };

        Assert.Contains(table.Problems(), p => p.Contains("«Сумма»"));
    }

    /// <summary>Настоящее объявление счетов годно — сторож не выдуман на подставных модулях.</summary>
    [Fact]
    public void Таблица_счетов_объявлена_годно()
    {
        var catalog = new ModuleTableCatalog([new CostsModule()]);

        var invoices = Assert.Single(catalog.All);
        Assert.Equal("costs.invoices", invoices.Address);
        Assert.Empty(invoices.Table.Problems());
    }

    /// <summary>
    /// Три причины недоступности ПОПАРНО неравны — кодом и текстом. Совпади две, три состояния из
    /// пяти стали бы на экране одним дефисом (ТЗ CORE-33).
    /// </summary>
    [Fact]
    public void Три_причины_недоступности_попарно_неравны()
    {
        string[] codes = [TableColumnReasons.NoRight, TableColumnReasons.Removed, TableColumnReasons.ModuleOff];
        string[] texts =
        [
            TableColumnReasons.NoRightText("суммы"),
            TableColumnReasons.RemovedText,
            TableColumnReasons.ModuleOffText("Счета и накладные"),
        ];

        Assert.Equal(3, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, texts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
    }

    /// <summary>
    /// Список операторов — один на двух исполнителей. Исполнитель в памяти умеет ровно то, что
    /// предлагает список, — ни больше, ни меньше; исполнитель запроса к базе (G1c) встанет сюда же.
    /// </summary>
    [Fact]
    public void Исполнитель_в_памяти_умеет_ровно_операторы_общего_списка()
    {
        Assert.Equal(
            TableOperators.All.OrderBy(o => o, StringComparer.Ordinal),
            DataSetRowFilterExecutor.Operators.OrderBy(o => o, StringComparer.Ordinal));
    }

    /// <summary>
    /// У каждого вида колонки модуля есть имя для потребителя, операторы — и обратный перевод даёт тот
    /// же вид. Вид, забытый в таблице соответствия, иначе молча стал бы текстом.
    /// </summary>
    [Fact]
    public void У_каждого_вида_колонки_есть_операторы_и_перевод_в_обе_стороны()
    {
        foreach (var kind in Enum.GetValues<ModuleTableColumnKind>())
        {
            Assert.NotEmpty(TableOperators.For(TableKinds.Name(kind)));
            Assert.Equal(kind, TableKinds.Parse(TableKinds.Name(kind)));
        }
    }

    /// <summary>Зеркало вида отбора совпадает с исходным по составу — см. <see cref="ModuleTableIsolation" />.</summary>
    [Fact]
    public void Зеркало_вида_отбора_совпадает_с_исходным()
    {
        Assert.Equal(
            Enum.GetNames<SystemDataSetIsolation>().OrderBy(x => x, StringComparer.Ordinal),
            Enum.GetNames<ModuleTableIsolation>().OrderBy(x => x, StringComparer.Ordinal));
    }

    private static ModuleTable Table(string requires = "probe") => new(
        "registry", "Реестр", "запись", requires, ModuleTableIsolation.None, "Отдаёт всё",
        [new("Номер", "Номер", ModuleTableColumnKind.Text)], typeof(ProbeRows));

    private sealed class ProbeRows : IModuleTableRows
    {
        public Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct) =>
            Task.FromResult(new ModuleTablePage([], 0, new Dictionary<string, TableTotal>()));
    }

    private sealed class ProbeModule(ModuleTable table, bool registerReader = true) : IAppModule
    {
        public string Code => "probe";
        public string Title => "Проба";
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => ["/api/probe"];
        public IReadOnlyList<ModuleTable> Tables => [table];

        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
            if (registerReader) services.AddScoped<ProbeRows>();
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
