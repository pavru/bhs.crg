using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Integration;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Work;

/// <summary>
/// Путь записи отчёта (<see cref="ReportWriter" />, change <c>work-13-report-model</c>): тот, которым пойдут адреса PR 2.
/// Здесь — то, что видно только на нём целиком: позиция перечня у новой строки, ключ на прежней стройке при переносе
/// отчёта, охрана сведений и «справочник не заведён».
/// </summary>
[Collection(AssignmentHost.Collection)]
public class ReportWriterTests(AssignmentHost fixture) : IAsyncLifetime
{
    private static readonly DateOnly Day16 = new(2026, 11, 16);
    private readonly WorkTypeSeed seed = new(fixture);

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Отчёт со строкой ложится: позицию ставит сервер по ключу, повтор правки той же строки позицию не меняет.</summary>
    [Fact]
    public async Task Отчёт_заводится_и_правится_строка_держит_позицию()
    {
        var s = await SeedAsync();
        var line = Line(s);
        var id = Guid.NewGuid();
        await WriteAsync((w, db) => w.CreateAsync(id, s.Employee, Header(s.SiteA), [line], 1, Now, default));

        var item = await PlanItemOfAsync(id);
        await WriteAsync(async (w, db) =>
        {
            var report = await LoadAsync(db, id);
            await w.ChangeAsync(report, Header(s.SiteA) with { Hours = 9.5m }, [line with { Volume = 300m }], 2, Now, default);
            return report;
        });

        Assert.Equal(item, await PlanItemOfAsync(id));
        using var scope = fixture.Services.CreateScope();
        var read = await LoadAsync(scope.ServiceProvider.GetRequiredService<WorkDbContext>(), id);
        Assert.Equal(9.5m, read.Hours);
        Assert.Equal(300m, Assert.Single(read.Lines).Volume);
    }

    /// <summary>
    /// Отчёт перенесли на другую стройку — ключ каждой строки другой, и позиция берётся на новой стройке, а не
    /// остаётся прежней (ключ лежащей строки считается на ПРЕЖНЕЙ стройке).
    /// </summary>
    [Fact]
    public async Task Перенос_отчёта_на_другую_стройку_меняет_позицию_строк()
    {
        var s = await SeedAsync();
        var line = Line(s) with { SectionId = null };
        var id = Guid.NewGuid();
        await WriteAsync((w, db) => w.CreateAsync(id, s.Employee, Header(s.SiteA), [line], 1, Now, default));
        var onA = await PlanItemOfAsync(id);

        await WriteAsync(async (w, db) =>
        {
            var report = await LoadAsync(db, id);
            await w.ChangeAsync(report, Header(s.SiteB), [line], 2, Now, default);
            return report;
        });

        Assert.NotEqual(onA, await PlanItemOfAsync(id));
    }

    /// <summary>Строки, которых нет в правке, удаляются вместе с материалами.</summary>
    [Fact]
    public async Task Строка_не_названная_в_правке_удаляется()
    {
        var s = await SeedAsync();
        var (keep, drop) = (Line(s), Line(s) with { Id = Guid.NewGuid() });
        var id = Guid.NewGuid();
        await WriteAsync((w, db) => w.CreateAsync(id, s.Employee, Header(s.SiteA), [keep, drop], 1, Now, default));

        await WriteAsync(async (w, db) =>
        {
            var report = await LoadAsync(db, id);
            await w.ChangeAsync(report, Header(s.SiteA), [keep], 2, Now, default);
            return report;
        });

        using var scope = fixture.Services.CreateScope();
        Assert.Equal(keep.Id, Assert.Single((await LoadAsync(scope.ServiceProvider.GetRequiredService<WorkDbContext>(), id)).Lines).Id);
    }

    /// <summary>Колонку строки в сведения не положить: значение поля модуля кладёт код (охрана записи).</summary>
    [Fact]
    public async Task Колонка_строки_в_сведениях_отвергается_поле_заказчика_принимается()
    {
        var s = await SeedAsync(withTypes: true);

        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() => WriteAsync((w, db) =>
            w.CreateAsync(Guid.NewGuid(), s.Employee, Header(s.SiteA), [Line(s) with { Details = """{"Объём":5}""" }], 1, Now, default)));
        Assert.Contains("Строка 1, сведения", refusal.Message);
        // Охрана — до порта перечня: отказ не оставил позиции без держателя.
        Assert.Equal(0, await PlanItemCountAsync());

        await WriteAsync((w, db) =>
            w.CreateAsync(Guid.NewGuid(), s.Employee, Header(s.SiteA), [Line(s) with { Details = "{}" }], 1, Now, default));
    }

    /// <summary>Классификатора видов работ нет — отказ «справочник не заведён» с кодом, а не «вид не найден» (ТЗ CORE-20.4).</summary>
    [Fact]
    public async Task Без_классификатора_видов_работ_отказ_справочник_не_заведён()
    {
        var site = await SendAsync(new CreateConstructionCommand("Стройка А", Guid.NewGuid()));
        var line = new ReportLineValues(Guid.NewGuid(), Guid.NewGuid(), null, 1m, Guid.NewGuid(), null, null, null, false, null, [], []);

        var refusal = await Assert.ThrowsAsync<WorkDirectoryMissingException>(() => WriteAsync((w, db) =>
            w.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), Header(site.Id), [line], 1, Now, default)));

        Assert.Equal(IModuleEmployees.NotSetUpCode, refusal.Code);
        Assert.Equal(IModuleWorkTypes.NotSetUp, refusal.Message);
    }

    /// <summary>Четвёрка занята другим отчётом сотрудника — отказ с его идентификатором, и позиции не заведены.</summary>
    [Fact]
    public async Task Занятая_четвёрка_отказ_с_существующим_отчётом()
    {
        var s = await SeedAsync();
        var first = Guid.NewGuid();
        await WriteAsync((w, db) => w.CreateAsync(first, s.Employee, Header(s.SiteA), [], 1, Now, default));

        var twice = await Assert.ThrowsAsync<ReportKeyTakenException>(() => WriteAsync((w, db) =>
            w.CreateAsync(Guid.NewGuid(), s.Employee, Header(s.SiteA), [Line(s)], 2, Now, default)));
        Assert.Equal(first, twice.ExistingId);
        Assert.Equal(0, await PlanItemCountAsync());

        // Ночная смена той же даты свободна; её перенос на дневную — тот же отказ.
        var night = Guid.NewGuid();
        await WriteAsync((w, db) =>
            w.CreateAsync(night, s.Employee, Header(s.SiteA) with { Shift = ReportShift.Night }, [], 3, Now, default));
        var moved = await Assert.ThrowsAsync<ReportKeyTakenException>(() => ChangeLinesAsync(night, s, [], 4));
        Assert.Equal(first, moved.ExistingId);
    }

    /// <summary>Идентификатор строки занят строкой другого отчёта — 409 с номером строки, а не нарушение ключа базы.</summary>
    [Fact]
    public async Task Идентификатор_строки_другого_отчёта_отвергается()
    {
        var s = await SeedAsync();
        var line = Line(s) with { SectionId = null };
        await WriteAsync((w, db) => w.CreateAsync(Guid.NewGuid(), s.Employee, Header(s.SiteA), [line], 1, Now, default));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => WriteAsync((w, db) =>
            w.CreateAsync(Guid.NewGuid(), s.Employee, Header(s.SiteB), [Line(s) with { SectionId = null }, line], 2, Now, default)));

        Assert.Contains("Строка 2", refusal.Message);
        Assert.Contains("занят", refusal.Message);
    }

    /// <summary>Часы в сведениях строки и сведения не объектом — отказ с номером строки (ТЗ WORK-7).</summary>
    [Theory]
    [InlineData("""{"часы":4}""", "часы")]
    [InlineData("[1,2]", "не JSON-объект")]
    [InlineData("{не json", "не JSON")]
    public async Task Сведения_с_часами_или_не_объектом_отвергаются(string details, string expected)
    {
        var s = await SeedAsync(withTypes: true);

        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() => WriteAsync((w, db) =>
            w.CreateAsync(Guid.NewGuid(), s.Employee, Header(s.SiteA), [Line(s) with { Details = details }], 1, Now, default)));

        Assert.Contains("Строка 1", refusal.Message);
        Assert.Contains(expected, refusal.Message);
    }

    /// <summary>Правка объёма не пересобирает материалы — записи те же; смена материала — новые записи.</summary>
    [Fact]
    public async Task Материалы_пересобираются_только_при_изменении()
    {
        var s = await SeedAsync();
        var nomenclatureType = await seed.TypeAsync("НоменклатураТест", "Номенклатура");
        var cable = await seed.RecordAsync(nomenclatureType.Id, "Кабель ВВГнг 3х2.5");
        var tray = await seed.RecordAsync(nomenclatureType.Id, "Лоток 100х50");
        var line = Line(s) with { Materials = [new(cable.Id, "остаток бухты")] };
        var id = Guid.NewGuid();
        await WriteAsync((w, db) => w.CreateAsync(id, s.Employee, Header(s.SiteA), [line], 1, Now, default));
        var before = await MaterialIdsAsync(id);

        await ChangeLinesAsync(id, s, [line with { Volume = 300m }], 2);
        Assert.Equal(before, await MaterialIdsAsync(id));

        await ChangeLinesAsync(id, s, [line with { Materials = [new(tray.Id, null)] }], 3);
        Assert.NotEqual(before, await MaterialIdsAsync(id));
    }

    // ── Подготовка ──

    private Task ChangeLinesAsync(Guid id, Seeded s, IReadOnlyList<ReportLineValues> lines, long seq) =>
        WriteAsync(async (w, db) =>
        {
            var report = await LoadAsync(db, id);
            await w.ChangeAsync(report, Header(s.SiteA), lines, seq, Now, default);
            return report;
        });

    private async Task<List<Guid>> MaterialIdsAsync(Guid reportId)
    {
        using var scope = fixture.Services.CreateScope();
        var report = await LoadAsync(scope.ServiceProvider.GetRequiredService<WorkDbContext>(), reportId);
        return [.. report.Lines.SelectMany(l => l.Materials).Select(m => m.Id)];
    }

    private async Task<int> PlanItemCountAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>().GetAllAsync()).Count;
    }

    private static readonly DateTimeOffset Now = AssignmentHost.Now;

    private sealed record Seeded(Guid SiteA, Guid SiteB, Guid Section, Guid WorkType, Guid Unit, Guid Employee);

    private async Task<Seeded> SeedAsync(bool withTypes = false)
    {
        var a = await SendAsync(new CreateConstructionCommand("Стройка А", Guid.NewGuid()));
        var b = await SendAsync(new CreateConstructionCommand("Стройка Б", Guid.NewGuid()));
        var section = await SendAsync(new CreateSectionCommand(a.Id, "ЭОМ-1"));
        var (unitType, workType) = await seed.ClassifierAsync();
        var unit = await seed.RecordAsync(unitType.Id, "м");
        var work = await seed.RecordAsync(workType.Id, "Прокладка кабеля", WorkTypeSeed.WorkTypeData("01.02", "Прокладка кабеля", unit));
        var person = await seed.TypeAsync(CoreRecordTypes.PersonCode, "Лицо");
        if (withTypes)
        {
            using var scope = fixture.Services.CreateScope();
            await scope.ServiceProvider.ProjectModuleTypesAsync();
        }
        // Сотрудник — любая живая запись общих данных: ссылку проверяет ядро по состоянию записи, а не по типу.
        var employee = await seed.RecordAsync(person.Id, "Петров Пётр Петрович");
        return new Seeded(a.Id, b.Id, section.Id, work.Id, unit.Id, employee.Id);
    }

    private static ReportHeaderValues Header(Guid site) => new(site, Day16, ReportShift.Day, 8m, null);

    private static ReportLineValues Line(Seeded s) =>
        new(Guid.NewGuid(), s.WorkType, null, 120m, s.Unit, s.Section, "оси 3–4", null, false, null, [], []);

    private async Task WriteAsync(Func<ReportWriter, WorkDbContext, Task<Report>> write)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        await write(scope.ServiceProvider.GetRequiredService<ReportWriter>(), db);
        await db.SaveChangesAsync();
    }

    private static Task<Report> LoadAsync(WorkDbContext db, Guid id) =>
        db.Set<Report>().Include(r => r.Lines).ThenInclude(l => l.Materials)
            .Include(r => r.Lines).ThenInclude(l => l.Participants).SingleAsync(r => r.Id == id);

    private async Task<Guid> PlanItemOfAsync(Guid reportId)
    {
        using var scope = fixture.Services.CreateScope();
        return Assert.Single((await LoadAsync(scope.ServiceProvider.GetRequiredService<WorkDbContext>(), reportId)).Lines).WorkPlanItemId;
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
