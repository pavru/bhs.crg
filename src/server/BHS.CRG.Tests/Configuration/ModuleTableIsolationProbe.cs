using BHS.CRG.Api.Modules.Tables;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Tables;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Пробный модуль с таблицей, отбирающей строки по правам (work-07): отчёты о работах, которые
/// монтажник видит свои, менеджер — своих четырёх строек, а читающий всё — все.
///
/// <para>Таблиц с построчной изоляцией в поставке пока нет (их заводит <c>work-20</c>), а правила ядра
/// — параметр доступа, граница по исходу, отказ системе — обязаны держаться с первого объявления.
/// Поэтому они проверяются на этой пробе. Люди и стройки — вымышленные.</para>
///
/// <para><b>Под кодом модуля <c>work</c> и его правами</b>, а не своим модулем: путь набора данных при
/// старте сверяет ключи и модуль объявления со сборкой (<c>SystemDataSetDeclarations</c>), и выдуманный
/// модуль хост не поднял бы — сторож прав. Своих таблиц у <c>work</c> пока нет, и имя пробы с ними не
/// столкнётся.</para>
/// </summary>
internal static class ModuleTableIsolationProbe
{
    public const string Module = "work";
    public const string Address = "work.probe_reports";
    public const string Title = "Отчёты о работах";

    /// <summary>Право «читать всё» (<c>WorkModule.ReportRead</c>).</summary>
    public const string ReadAll = "work.report.read";

    /// <summary>Право менеджера — отчёты своих строек (<c>WorkModule.ReportReview</c>).</summary>
    public const string Review = "work.report.review";

    public static readonly Guid Installer = Guid.Parse("00000000-0000-0000-0000-00000000b701");
    public static readonly Guid OtherInstaller = Guid.Parse("00000000-0000-0000-0000-00000000b702");
    public static readonly Guid Manager = Guid.Parse("00000000-0000-0000-0000-00000000b703");
    public static readonly Guid Chief = Guid.Parse("00000000-0000-0000-0000-00000000b704");

    /// <summary>Стройки менеджера — четыре; пятая чужая.</summary>
    public static readonly Guid[] ManagerSites =
    [
        Guid.Parse("00000000-0000-0000-0000-00000000c701"), Guid.Parse("00000000-0000-0000-0000-00000000c702"),
        Guid.Parse("00000000-0000-0000-0000-00000000c703"), Guid.Parse("00000000-0000-0000-0000-00000000c704"),
    ];

    public static readonly Guid ForeignSite = Guid.Parse("00000000-0000-0000-0000-00000000c705");

    public static readonly IReadOnlyList<ModuleTableBoundary> Boundaries =
    [
        new("own", "Отдаёт то, что видно вам: ваши отчёты"),
        new("sites", "Отдаёт то, что видно вам: отчёты ваших строек ({sites})"),
        new("all", "Отдаёт все отчёты — право «читать всё»"),
    ];

    /// <summary>Отчёты: ключ, номер, автор, стройка.</summary>
    public static readonly IReadOnlyList<(string Key, string Number, Guid Author, Guid Site)> Reports =
    [
        ("r1", "ОР-1", Installer, ManagerSites[0]),
        ("r2", "ОР-2", OtherInstaller, ManagerSites[1]),
        ("r3", "ОР-3", OtherInstaller, ManagerSites[2]),
        ("r4", "ОР-4", OtherInstaller, ManagerSites[3]),
        ("r5", "ОР-5", Installer, ForeignSite),
        ("r6", "ОР-6", OtherInstaller, ForeignSite),
    ];

    public static ModuleTable Table(IReadOnlyList<ModuleTableBoundary>? boundaries = null) => new(
        "probe_reports", Title, "отчёт", Module, ModuleTableIsolation.PerUser, boundaries ?? Boundaries,
        [new("Номер", "Номер", ModuleTableColumnKind.Text)], typeof(ReportRows));

    /// <summary>Параметр доступа человека: ключ модуля и права наборов — чтобы пройти и путь набора.</summary>
    public static DataAccess Access(Guid user, string who, params string[] keys) => DataAccess.Of(
        user, who, [Module, "core.datasets.read", "core.datasets.edit", .. keys], [Module]);

    /// <summary>Служба таблиц над пробой — та же, что у экрана, выгрузки и набора.</summary>
    public static ModuleTableService Service(ReportRows rows, ModuleTable? table = null,
        BHS.CRG.Infrastructure.Persistence.AppDbContext? db = null)
    {
        var services = new ServiceCollection().AddSingleton(rows).BuildServiceProvider();
        return new ModuleTableService(new ModuleTableCatalog([new ProbeModule(table ?? Table())]), db!, services);
    }

    public static ModuleTableEntry Entry() => new ModuleTableCatalog([new ProbeModule(Table())]).Find(Address)!;

    /// <summary>
    /// Служба строк пробы: охват вычисляет САМА, по параметру доступа (как это будет делать <c>work</c>),
    /// и называет исход. <see cref="Outcome" /> подменяет исход — для проверок ядра на ошибке модуля.
    /// </summary>
    public sealed class ReportRows : IModuleTableRows
    {
        public int Calls { get; private set; }
        public ModuleTableQuery? Last { get; private set; }
        public Func<ModuleReach, ModuleTableOutcome?> Outcome { get; init; } = ModuleTableOutcome.Of;

        public static ModuleReach Reach(ModuleReader reader) =>
            reader.Allows(ReadAll) ? ModuleReach.All
            : reader.Allows(Review) && reader.UserId == Manager ? ModuleReach.OfSites(ManagerSites)
            : ModuleReach.Own;

        public Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct)
        {
            Calls++;
            Last = query;
            // Система сюда не доходит: ядро отказывает ей в воротах. Дошла — это ошибка ядра, а не пустота.
            if (query.Reader.IsSystem)
                throw new InvalidOperationException("Служба строк получила систему — ворота таблицы её пропустили.");

            var reach = Reach(query.Reader);
            var visible = Reports
                .Where(r => reach.Covers(r.Site, r.Author == query.Reader.UserId))
                .Where(r => query.Row is null || r.Key == query.Row)
                .ToList();
            var page = visible.Skip(query.Offset).Take(query.Limit ?? int.MaxValue).ToList();
            return Task.FromResult(new ModuleTablePage(
                [.. page.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Номер"] = r.Number })],
                visible.Count, new Dictionary<string, TableTotal>(),
                Keys: [.. page.Select(r => r.Key)],
                Outcome: Outcome(reach)));
        }
    }

    public sealed class ProbeModule(ModuleTable table) : IAppModule
    {
        public string Code => Module;
        public string Title => "Проба учёта работ";
        public IReadOnlyList<AppPermission> Permissions => [];
        public IReadOnlyList<string> RoutePrefixes => ["/api/work"];
        public IReadOnlyList<ModuleTable> Tables => [table];

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
        public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
    }
}
