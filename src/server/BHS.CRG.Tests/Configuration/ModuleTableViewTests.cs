using BHS.CRG.Modules;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Tables;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Сторожа объявления готовых представлений таблицы (задача G4, issue #1097; ТЗ CORE-33, COST-20.1).
///
/// <para>Представление ссылается на колонки таблицы по ключам, и ссылка эта ничем, кроме проверки при
/// старте, не держится: колонку переименовали — и «Реестр счетов» открылся бы с дырой либо отказал бы
/// на первом же чтении, у пользователя. Поэтому негодное представление останавливает старт и называет
/// себя.</para>
/// </summary>
public class ModuleTableViewTests
{
    /// <summary>
    /// Через <c>AddAppModules</c> — тем путём, которым идёт настоящий запуск: сторож, стоящий в стороне
    /// от старта, старт не сторожит.
    /// </summary>
    [Fact]
    public void Представление_с_колонкой_которой_нет_роняет_старт_и_называет_себя()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = "probe" })
            .Build();
        var table = Table(new ModuleTableView("registry", "Реестр", ["Номер", "Сумма счёта"]));

        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddAppModules(config, new ProbeModule(table)));

        Assert.Contains("«probe.docs»", error.Message);
        Assert.Contains("представление «registry»", error.Message);
        Assert.Contains("колонки «Сумма счёта» в таблице нет", error.Message);
    }

    /// <summary>Каждая поломка представления названа своими словами — и все разом, а не первая.</summary>
    [Theory]
    [MemberData(nameof(Broken))]
    public void Негодное_представление_названо(string what, ModuleTableView view, string expected)
    {
        var problems = Table(view).Problems();

        Assert.True(problems.Any(p => p.Contains(expected)),
            $"{what}: ждали «{expected}», а названо: {string.Join("; ", problems)}");
    }

    public static TheoryData<string, ModuleTableView, string> Broken() => new()
    {
        { "нет кода", Good() with { Code = " " }, "не назван код" },
        { "нет названия", Good() with { Title = "" }, "нет названия для человека" },
        { "нет колонок", Good() with { Columns = [], Totals = null, Pinned = 0 }, "не названо ни одной колонки" },
        { "колонка дважды", Good() with { Columns = ["Номер", "Номер", "Сумма"] }, "колонка «Номер» названа дважды" },
        { "закреплено больше, чем есть", Good() with { Pinned = 4 }, "закреплено колонок — 4" },
        { "сортировка по несуществующей", Good() with { Sort = [new("Дата счёта")] }, "сортировка по колонке «Дата счёта», которой в таблице нет" },
        { "сортировка по зависящей от отбора", Good() with { Sort = [new("Доля")] }, "её значение зависит от отбора" },
        { "итог под колонкой вне представления", Good() with { Totals = [new("Дата", "max")] }, "итог под колонкой «Дата», которой в представлении нет" },
        { "сумма у текста", Good() with { Totals = [new("Номер", "sum")] }, "итог «sum» под колонкой «Номер» не считается" },
        { "сумма у даты", Good() with { Columns = ["Дата"], Totals = [new("Дата", "sum")], Pinned = 0 }, "итог «sum» под колонкой «Дата» не считается" },
        { "незнакомый итог", Good() with { Totals = [new("Сумма", "total")] }, "итог «total» под колонкой «Сумма» не считается" },
        { "два итога под колонкой", Good() with { Totals = [new("Сумма", "sum"), new("Сумма", "max")] }, "под колонкой «Сумма» два итога" },
        { "отбор по несуществующей", Good() with { Filters = ["Поставщик"] }, "отбор предложен по колонке «Поставщик», которой в таблице нет" },
        { "отбор по зависящей от отбора", Good() with { Filters = ["Доля"] }, "а по ней не отбирают" },
        { "отбор дважды", Good() with { Filters = ["Номер", "Номер"] }, "отбор по колонке «Номер» предложен дважды" },
    };

    /// <summary>Годное представление не называет ничего — иначе сторожа выше краснели бы на всём.</summary>
    [Fact]
    public void Годное_представление_проблем_не_называет()
    {
        Assert.Empty(Table(Good()).Problems());
        // Итог под колонкой, чей смысл зависит от отбора, годен: её итог таблица считает (доля по отбору).
        Assert.Empty(Table(Good() with { Columns = ["Доля"], Totals = [new("Доля", "sum")], Pinned = 0 }).Problems());
    }

    [Fact]
    public void Два_представления_с_одним_кодом_названы()
    {
        var table = Table(Good(), Good() with { Code = "REGISTRY", Title = "Второй" });

        Assert.Contains(table.Problems(), p => p.Contains("представление «registry» объявлено дважды"));
    }

    /// <summary>
    /// Настоящий «Реестр счетов»: порядок колонок — как в таблице заказчика (ТЗ COST-20.1), и полная
    /// сумма стоит сразу за суммой по отбору — под отбором по объекту первая становится долей.
    /// </summary>
    [Fact]
    public void Реестр_счетов_объявлен_годно_и_в_порядке_таблицы_заказчика()
    {
        var invoices = Assert.Single(new ModuleTableCatalog([new CostsModule()]).All).Table;
        var registry = Assert.Single(invoices.Views!);

        Assert.Equal(InvoiceTable.RegistryView, registry.Code);
        Assert.Equal("Реестр счетов", registry.Title);
        Assert.Empty(registry.Problems(invoices.Columns));

        var titles = invoices.Columns.ToDictionary(c => c.Key, c => c.Title);
        Assert.Equal(
            [
                "Поставщик", "Сумма", "Сумма к оплате", "Номер счёта", "Дата счёта", "Дата отгрузки",
                "Отсрочка, дней", "Оплатить до", "Осталось дней", "Состояние оплаты", "Объект", "Плательщик",
                "Назначение", "Строк без позиции",
            ],
            registry.Columns.Select(k => titles[k]));

        // Обе суммы — с итогом: под отбором по объекту рядом стоят «сколько на объект» и «сколько всего».
        Assert.Equal([InvoiceTable.AmountKey, "Итого"], registry.Totals!.Select(t => t.Column));
        Assert.All(registry.Totals!, t => Assert.Equal("sum", t.Aggregate));

        // Отборы реестра по ТЗ: период, плательщик, поставщик, стройка, состояние оплаты.
        Assert.Equal(
            ["Дата счёта", "Плательщик", "Поставщик", "Объект", "Состояние оплаты"],
            registry.Filters!.Select(k => titles[k]));
    }

    private static ModuleTableView Good() => new(
        "registry", "Реестр", ["Номер", "Сумма"],
        Sort: [new("Дата", Descending: true)], Totals: [new("Сумма", "sum")], Pinned: 1, Filters: ["Дата"]);

    private static ModuleTable Table(params ModuleTableView[] views) => new(
        "docs", "Документы", "документ", "probe", ModuleTableIsolation.None, "Отдаёт всё",
        [
            new("Номер", "Номер", ModuleTableColumnKind.Text),
            new("Дата", "Дата", ModuleTableColumnKind.Date),
            new("Сумма", "Сумма", ModuleTableColumnKind.Number),
            new("Доля", "Доля", ModuleTableColumnKind.Number, DependsOnFilter: true),
        ],
        typeof(ProbeRows), Views: views);

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
