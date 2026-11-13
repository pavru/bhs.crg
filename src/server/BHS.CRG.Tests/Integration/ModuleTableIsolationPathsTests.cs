using BHS.CRG.Api.Modules.Tables;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Settings;
using BHS.CRG.Application.Tables;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using static BHS.CRG.Tests.Configuration.ModuleTableIsolationProbe;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Один человек — одни строки и одна строка границы на ВСЕХ путях чтения таблицы с отбором по правам:
/// экран, выгрузка XLSX, набор данных и MCP (work-07; ТЗ WORK-40, CORE-24.1, CORE-24.3).
///
/// <para>Пути разные по устройству: экран зовёт службу таблиц прямо, выгрузка — дважды (счёт, затем
/// строки), набор — через поставщика и ворота набора, MCP — через снимок источника. Подпись, потерянная
/// на любом из них, читалась бы как «все отчёты» у того, кому отдали четыре стройки.</para>
/// </summary>
[Collection("Integration")]
public class ModuleTableIsolationPathsTests(IntegrationTestFixture fixture)
{
    private const string Expected = "Отдаёт то, что видно вам: отчёты ваших строек (4)";
    private static readonly string[] ManagerNumbers = ["ОР-1", "ОР-2", "ОР-3", "ОР-4"];

    [Fact]
    public async Task Экран_выгрузка_набор_и_MCP_дают_одни_строки_и_одну_границу()
    {
        var rows = new ReportRows();
        var access = Access(Manager, "Менеджер Пробный", Review);

        // Хост с поставщиком набора над пробной таблицей — тем же, что регистрирует AddModuleTables для
        // каждой объявленной таблицы. Модуль «work» включён в параметре доступа — какие модули включены у
        // хоста прогона, пробе безразлично.
        await using var host = fixture.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped<ISystemDataProvider>(_ => new ModuleTableDataProvider(Entry(), Service(rows)))));
        _ = host.CreateClient();
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var service = Service(rows, db: sp.GetRequiredService<AppDbContext>());

        // 1. Экран.
        var (screen, refusal) = await service.ReadAsync(Address, access, new TableRequest(), default);
        Assert.Null(refusal);
        Assert.Equal(ManagerNumbers, screen!.Rows.Select(r => (string)r["Номер"]!));
        Assert.Equal(Expected, screen.Boundary);
        Assert.Equal("sites", screen.BoundaryOutcome);

        // 2. Выгрузка XLSX — строка границы сразу за названием таблицы.
        var export = new ModuleTableExport(service, sp.GetRequiredService<IAppSettingsStore>(),
            sp.GetRequiredService<IActivityLog>(), sp.GetRequiredService<AppDbContext>());
        var (file, exportRefusal) = await export.ExportAsync(Address, access, new TableExportBody(null, null, null, null), default);
        Assert.Null(exportRefusal);
        var sheet = new XSSFWorkbook(new MemoryStream(file!.Bytes)).GetSheetAt(0);
        var preamble = Lines(sheet);
        Assert.Equal(Title, preamble[0]);
        Assert.Equal(Expected, preamble[1]);
        var header = preamble.Count + 1;
        Assert.Equal(ManagerNumbers, Enumerable.Range(header + 1, 4).Select(r => sheet.GetRow(r).GetCell(0).StringCellValue));

        // 3. Набор данных — поставщик и ворота набора (BoundaryFor выбирает подпись из названной).
        var provider = new ModuleTableDataProvider(Entry(), service);
        var provided = await provider.ProvideAsync(ModuleTableDataProvider.MarkerOf(Entry()), CatalogScope.System, null, access, default);
        Assert.Equal(ManagerNumbers, provided.Rows.Select(r => r["Номер"]));
        Assert.Equal(Expected, provider.Declaration.BoundaryFor(provided.Boundary));

        // 4. MCP — снимок источника на этой таблице.
        var datasets = sp.GetRequiredService<IDataSetService>();
        var dataFile = await datasets.CreateSystemFileAsync(new CreateSystemFileInput("System", null, null), default);
        var source = await datasets.CreateSourceAsync(dataFile.Id,
            new CreateSourceInput("Отчёты", ModuleTableDataProvider.MarkerOf(Entry()), null), access, default);
        var mcp = await sp.GetRequiredService<IDataSnapshotService>().GetRowsAsync(source.Id, 0, 10, access, ct: default);
        Assert.Equal(ManagerNumbers, mcp!.Rows.Select(r => r["Номер"]));
        Assert.Equal(Expected, mcp.Boundary);
    }

    /// <summary>Строки шапки — до первой пустой строки.</summary>
    private static List<string> Lines(ISheet sheet)
    {
        var lines = new List<string>();
        for (var r = 0; sheet.GetRow(r) is { } row && row.GetCell(0) is { CellType: CellType.String } cell; r++)
            lines.Add(cell.StringCellValue);
        return lines;
    }
}
