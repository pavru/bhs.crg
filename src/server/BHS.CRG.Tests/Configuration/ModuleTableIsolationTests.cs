using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Tables;
using Microsoft.AspNetCore.Http;
using static BHS.CRG.Tests.Configuration.ModuleTableIsolationProbe;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Изоляция в чтении таблицы модуля — правила ЯДРА на пробной таблице с отбором по правам (work-07;
/// ТЗ WORK-39, WORK-40, CORE-24.1, CORE-24.3).
///
/// <para>Служба строк вычисляет охват сама, а ядро держит три вещи: параметр доступа доходит до неё
/// явным, система до неё не доходит вовсе, и данные не уходят без подписи того исхода, что применился.
/// Четыре пути чтения на живой базе — <see cref="Integration.ModuleTableIsolationPathsTests" />.</para>
/// </summary>
public class ModuleTableIsolationTests
{
    private static readonly DataAccess InstallerAccess = Access(Installer, "Монтажник Пробный");
    private static readonly DataAccess ManagerAccess = Access(Manager, "Менеджер Пробный", Review);
    private static readonly DataAccess ChiefAccess = Access(Chief, "Главный Пробный", ReadAll);

    [Fact]
    public async Task Служба_строк_получает_параметр_доступа_спросившего()
    {
        var rows = new ReportRows();
        await Service(rows).ReadAsync(Address, ManagerAccess, new TableRequest(), default);

        var reader = rows.Last!.Reader;
        Assert.Equal(Manager, reader.UserId);
        Assert.Equal("Менеджер Пробный", reader.Who);
        Assert.True(reader.Allows(Review));
        Assert.False(reader.IsSystem);
    }

    /// <summary>
    /// Задание без человека — отказ с названием таблицы и причиной задания, а не исключение и не пустая
    /// страница; до службы строк оно не доходит вовсе (ТЗ WORK-41, CORE-24.1).
    /// </summary>
    [Fact]
    public async Task Служебное_задание_получает_отказ_с_причиной_и_службу_строк_не_зовёт()
    {
        var rows = new ReportRows();
        var (table, refusal) = await Service(rows).ReadAsync(
            Address, DataAccess.OfSystem("ночная автоподача отчётов"), new TableRequest(), default);

        Assert.Null(table);
        Assert.Equal(StatusCodes.Status409Conflict, refusal!.Status);
        Assert.Contains($"«{Title}»", refusal.Error);
        Assert.Contains("ночная автоподача отчётов", refusal.Error);
        Assert.Equal(0, rows.Calls);
    }

    [Fact]
    public async Task Служебное_задание_не_получает_и_готовых_отборов_и_описания()
    {
        var service = Service(new ReportRows());
        var system = DataAccess.OfSystem("ночная автоподача отчётов");

        Assert.Equal(StatusCodes.Status409Conflict, (await service.DescribeAsync(Address, system, default)).Refusal!.Status);
        Assert.Equal(StatusCodes.Status409Conflict, (await service.ShortcutsAsync(Address, system, default)).Refusal!.Status);
    }

    /// <summary>Граница — словами того исхода, что применился, с числом строек (ТЗ CORE-24.3).</summary>
    [Theory]
    [InlineData("installer", "own", "Отдаёт то, что видно вам: ваши отчёты", 2)]
    [InlineData("manager", "sites", "Отдаёт то, что видно вам: отчёты ваших строек (4)", 4)]
    [InlineData("chief", "all", "Отдаёт все отчёты — право «читать всё»", 6)]
    public async Task Граница_выдачи_по_исходу_отбора(string who, string outcome, string boundary, int count)
    {
        var access = who switch { "installer" => InstallerAccess, "manager" => ManagerAccess, _ => ChiefAccess };
        var (table, refusal) = await Service(new ReportRows()).ReadAsync(Address, access, new TableRequest(), default);

        Assert.Null(refusal);
        Assert.Equal(boundary, table!.Boundary);
        Assert.Equal(outcome, table.BoundaryOutcome);
        Assert.Equal(count, table.Count);
    }

    /// <summary>
    /// Описание таблицы читается без строк — и исхода у него нет. Подпись «первым исходом» сказала бы
    /// менеджеру «ваши отчёты», поэтому у таблицы с несколькими исходами описание границы не называет.
    /// </summary>
    [Fact]
    public async Task Описание_таблицы_с_несколькими_исходами_границу_не_угадывает()
    {
        var (decl, _) = await Service(new ReportRows()).DescribeAsync(Address, ManagerAccess, default);
        Assert.Null(decl!.Boundary);

        var (single, _) = await Service(new ReportRows(), Table(ModuleTableBoundary.Only("Отдаёт всё")))
            .DescribeAsync(Address, ManagerAccess, default);
        Assert.Equal("Отдаёт всё", single!.Boundary);
    }

    [Fact]
    public async Task Одна_граница_подписывает_и_без_названного_исхода()
    {
        var rows = new ReportRows { Outcome = _ => null };
        var (table, refusal) = await Service(rows, Table(ModuleTableBoundary.Only("Отдаёт всё")))
            .ReadAsync(Address, ChiefAccess, new TableRequest(), default);

        Assert.Null(refusal);
        Assert.Equal("Отдаёт всё", table!.Boundary);
        Assert.Equal(ModuleTableBoundary.OnlyCode, table.BoundaryOutcome);
    }

    /// <summary>Ошибка модуля в исходе — отказ с названием таблицы: данные без подписи не отдаются.</summary>
    [Theory]
    [MemberData(nameof(BrokenOutcomes))]
    public async Task Неверно_названный_исход_останавливает_чтение(
        string what, Func<ModuleReach, ModuleTableOutcome?> outcome, string expected)
    {
        var (table, refusal) = await Service(new ReportRows { Outcome = outcome })
            .ReadAsync(Address, ManagerAccess, new TableRequest(), default);

        Assert.Null(table);
        Assert.Equal(StatusCodes.Status409Conflict, refusal!.Status);
        Assert.Contains($"«{Title}»", refusal.Error);
        Assert.True(refusal.Error.Contains(expected), $"{what}: ждали «{expected}», а названо: {refusal.Error}");
    }

    public static TheoryData<string, Func<ModuleReach, ModuleTableOutcome?>, string> BrokenOutcomes() => new()
    {
        { "не назван", _ => null, "не назвала, какой применился" },
        { "не объявлен", _ => new("region"), "исход «region», а таблица его не объявляла" },
        { "нет числа строек", _ => new("sites"), "считает стройки, а служба строк их число не назвала" },
    };

    /// <summary>
    /// Чужая строка по ключу — тот же ответ, что несуществующая: пустая выдача, а не 403 (ТЗ WORK-39). По
    /// ответу не узнать, есть ли такой отчёт у соседа.
    /// </summary>
    [Fact]
    public async Task Чужая_строка_по_ключу_неотличима_от_несуществующей()
    {
        var service = Service(new ReportRows());

        var (foreign, foreignRefusal) = await service.ReadAsync(Address, InstallerAccess, new TableRequest(Row: "r2"), default);
        var (missing, missingRefusal) = await service.ReadAsync(Address, InstallerAccess, new TableRequest(Row: "r404"), default);

        Assert.Null(foreignRefusal);
        Assert.Null(missingRefusal);
        Assert.Empty(foreign!.Rows);
        Assert.Equal(0, foreign.Count);
        Assert.Equal(missing!.Rows.Count, foreign.Rows.Count);
        Assert.Equal(missing.Count, foreign.Count);
        Assert.Equal(missing.Boundary, foreign.Boundary);

        // Своя — открывается.
        var (own, _) = await service.ReadAsync(Address, InstallerAccess, new TableRequest(Row: "r1"), default);
        Assert.Equal(["r1"], own!.Keys);
    }

    [Fact]
    public void Охват_по_стройкам_пустым_списком_не_становится_всеми()
    {
        var none = ModuleReach.OfSites([]);

        Assert.False(none.Covers(ManagerSites[0], own: true));
        Assert.Equal(new ModuleTableOutcome("sites", 0), ModuleTableOutcome.Of(none));
        Assert.Equal("Отдаёт то, что видно вам: отчёты ваших строек (0)", Boundaries[1].Render(0));
    }
}
