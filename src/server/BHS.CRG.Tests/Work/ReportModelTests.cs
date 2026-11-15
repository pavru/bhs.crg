using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Integration;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Work;

/// <summary>
/// Модель отчёта монтажника на живой базе (change <c>work-13-report-model</c>, PR 1): ключ отчёта, позиция перечня
/// через порт этапа 3, ссылки на архивные записи, держание и типы модуля. Адресов у модели ещё нет (PR 2), поэтому
/// записи кладутся прямо в контекст модуля, а службы берутся из области запроса, как их получит адрес.
///
/// <para>Хост — назначений (<see cref="AssignmentHost" />, только <c>work</c>): общий хост тестов модуль не
/// включает, а отчёт без назначений в PR 2 не проверить. Классы одной коллекции идут друг за другом.</para>
/// </summary>
[Collection(AssignmentHost.Collection)]
public class ReportModelTests(AssignmentHost fixture) : IAsyncLifetime
{
    private static readonly DateOnly Day16 = new(2026, 11, 16);
    private readonly WorkTypeSeed seed = new(fixture);

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Ключ отчёта (ТЗ WORK-4, MOB-35) ──

    /// <summary>
    /// СТОРОЖ КЛЮЧА. Дневной и ночной отчёт одной даты сосуществуют; второй дневной — нарушение уникального индекса.
    /// Ключ без смены краснеет здесь: ночная смена затёрла бы дневную.
    /// </summary>
    [Fact]
    public async Task Дневной_и_ночной_отчёт_одной_даты_сосуществуют_второй_дневной_нет()
    {
        var (employee, site) = (Guid.NewGuid(), Guid.NewGuid());
        await SaveAsync(NewReport(employee, site, ReportShift.Day));
        await SaveAsync(NewReport(employee, site, ReportShift.Night));

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(NewReport(employee, site, ReportShift.Day)));

        var postgres = Assert.IsType<PostgresException>(duplicate.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("ux_reports_key", postgres.ConstraintName);
        // Другая стройка или другой сотрудник той же даты — другой ключ.
        await SaveAsync(NewReport(employee, Guid.NewGuid(), ReportShift.Day));
        await SaveAsync(NewReport(Guid.NewGuid(), site, ReportShift.Day));
    }

    /// <summary>Строки с материалами и участниками ложатся и читаются обратно в порядке ввода; версия отслеживаемой строки — не «0».</summary>
    [Fact]
    public async Task Отчёт_со_строками_материалами_и_участниками_сохраняется()
    {
        var s = await SeedAsync();
        var report = NewReport(Guid.NewGuid(), s.Site, ReportShift.Day);
        var nomenclature = Guid.NewGuid();
        report.Lines.Add(ReportLine.Create(report.Id, 0, Values(s, materials: [new(nomenclature, "остаток бухты")]), Guid.NewGuid()));
        report.Lines.Add(ReportLine.Create(report.Id, 1, Values(s, crew: true, participants: [Guid.NewGuid(), Guid.NewGuid()]), Guid.NewGuid()));
        await SaveAsync(report);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        var read = await db.Set<Report>().Include(r => r.Lines).ThenInclude(l => l.Materials)
            .Include(r => r.Lines).ThenInclude(l => l.Participants).SingleAsync(r => r.Id == report.Id);

        Assert.Equal([0, 1], read.Lines.OrderBy(l => l.Position).Select(l => l.Position));
        Assert.Equal(nomenclature, Assert.Single(read.Lines.Single(l => l.Position == 0).Materials).NomenclatureId);
        Assert.Equal(2, read.Lines.Single(l => l.Position == 1).Participants.Count);
        Assert.NotEqual("0", db.VersionOf(read));
    }

    /// <summary>Протокол изменений только дописывается: правка записи — отказ контекста (IAppendOnly).</summary>
    [Fact]
    public async Task Протокол_изменений_не_правится()
    {
        var report = NewReport(Guid.NewGuid(), Guid.NewGuid(), ReportShift.Day);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        db.Add(report);
        var change = ReportChange.Of(report.Seq, report, Guid.NewGuid(), ReportChangeKind.Created, null, DateTimeOffset.UtcNow, "1");
        db.Add(change);
        await db.SaveChangesAsync();

        db.Remove(change);
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
    }

    // ── Позиция перечня (ТЗ CORE-12, MOB-37, CORE-4) ──

    /// <summary>СТОРОЖ D9. Две строки разных отчётов с одним ключом, сохраняемые одновременно, — одна позиция.</summary>
    [Fact]
    public async Task Две_параллельные_строки_одного_ключа_дают_одну_позицию()
    {
        var s = await SeedAsync();

        var resolved = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var scope = fixture.Services.CreateScope();
            var values = Values(s);
            var items = await scope.ServiceProvider.GetRequiredService<WorkPlanItemLink>()
                .ResolveAsync(s.Site, [new PlanItemDemand(0, values, null, null)], default);
            return items[values.Id];
        })));

        Assert.Single(resolved.Distinct());
        Assert.Equal(1, await PlanItemCountAsync());
    }

    /// <summary>
    /// Сбой между ядром и модулем: позиция заведена, строка не сохранилась — повтор встаёт на ту же позицию, второй нет.
    /// </summary>
    [Fact]
    public async Task Сбой_сохранения_строки_и_повтор_не_заводят_второй_позиции()
    {
        var s = await SeedAsync();
        var values = Values(s);
        var first = await ResolveAsync(s, new PlanItemDemand(0, values, null, null));
        // Сохранение строки «упало» — до модуля дело не дошло; клиент повторяет тот же запрос.
        var again = await ResolveAsync(s, new PlanItemDemand(0, values, null, null));

        Assert.Equal(first, again);
        Assert.Equal(1, await PlanItemCountAsync());

        var report = NewReport(Guid.NewGuid(), s.Site, ReportShift.Day);
        report.Lines.Add(ReportLine.Create(report.Id, 0, values, again));
        await SaveAsync(report);
        Assert.Equal(1, await PlanItemCountAsync());
    }

    /// <summary>
    /// Вид работы ушёл в архив: строка с прежним ключом остаётся на прежней позиции (порт не спрашивается), а новый
    /// ключ с ним — отказ с полем «вид работы» (ТЗ CORE-34.4).
    /// </summary>
    [Fact]
    public async Task Архивный_вид_работы_прежний_ключ_держит_позицию_новый_отвергается()
    {
        var s = await SeedAsync();
        var values = Values(s);
        var item = await ResolveAsync(s, new PlanItemDemand(0, values, null, null));
        await seed.ArchiveAsync(s.WorkType);

        var kept = await ResolveAsync(s, new PlanItemDemand(0, values with { Comment = "поправил" }, values.KeyOn(s.Site), item));
        Assert.Equal(item, kept);

        var moved = values with { SectionId = null };
        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() =>
            ResolveAsync(s, new PlanItemDemand(2, moved, values.KeyOn(s.Site), item)));
        Assert.Contains("Строка 3, поле «вид работы»", refusal.Message);
    }

    // ── Ссылки на архивные записи (ТЗ CORE-34.4) и держание (ТЗ CORE-34.1) ──

    /// <summary>Новый архивный материал — 400 с именем поля; материал, ушедший в архив после сохранения, правку объёма не останавливает.</summary>
    [Fact]
    public async Task Новый_архивный_материал_отвергается_прежний_нет()
    {
        var s = await SeedAsync();
        var nomenclatureType = await seed.TypeAsync("НоменклатураТест", "Номенклатура");
        var cable = await seed.RecordAsync(nomenclatureType.Id, "Кабель ВВГнг 3х2.5");
        var stored = ReportLine.Create(Guid.NewGuid(), 0, Values(s, materials: [new(cable.Id, null)]), Guid.NewGuid());
        await seed.ArchiveAsync(cable.Id);

        using var scope = fixture.Services.CreateScope();
        var check = scope.ServiceProvider.GetRequiredService<ReportReferenceCheck>();

        var fresh = Values(s, materials: [new(cable.Id, null)]);
        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() =>
            check.EnsureAsync([], [.. ReportReferenceCheck.OfLine(fresh, 2)], default));
        Assert.Contains("«материал»", refusal.Message);
        Assert.Contains("в архиве", refusal.Message);
        // Человек ищет строку по номеру, а не по идентификатору.
        Assert.Contains("(строка 3)", refusal.Message);
        Assert.DoesNotContain(fresh.Id.ToString(), refusal.Message);

        // Правка объёма той же строки, переставленной на другое место, — не новая ссылка.
        var volumeOnly = Values(s, materials: [new(cable.Id, null)]) with { Id = stored.Id, Volume = 250m };
        await check.EnsureAsync([.. ReportReferenceCheck.OfLine(stored)], [.. ReportReferenceCheck.OfLine(volumeOnly, 1)], default);
    }

    /// <summary>Пропавшая запись — тоже отказ: ссылка в никуда хуже ссылки в архив.</summary>
    [Fact]
    public async Task Новая_ссылка_на_несуществующую_запись_отвергается()
    {
        var s = await SeedAsync();
        using var scope = fixture.Services.CreateScope();
        var check = scope.ServiceProvider.GetRequiredService<ReportReferenceCheck>();

        var refusal = await Assert.ThrowsAsync<InvalidRequestException>(() => check.EnsureAsync(
            [], [.. ReportReferenceCheck.OfLine(Values(s, crew: true, participants: [Guid.NewGuid()]), 0)], default));
        Assert.Contains("«участник»", refusal.Message);
        Assert.Contains("не найдена", refusal.Message);
    }

    /// <summary>Строки отчётов держат вид работы: удалить занятый нельзя, отказ называет держателя словами (ТЗ CORE-34.1).</summary>
    [Fact]
    public async Task Удаление_занятого_вида_работы_отказ_называет_строки_отчётов()
    {
        var s = await SeedAsync();
        var report = NewReport(Guid.NewGuid(), s.Site, ReportShift.Day);
        report.Lines.Add(ReportLine.Create(report.Id, 0, Values(s), Guid.NewGuid()));
        report.Lines.Add(ReportLine.Create(report.Id, 1, Values(s), Guid.NewGuid()));
        await SaveAsync(report);

        var refusal = await Assert.ThrowsAnyAsync<Exception>(() => SendAsync(new DeleteCommonDataEntryCommand(s.WorkType)));

        Assert.Contains("строки отчётов монтажников — 2", refusal.Message);
    }

    // ── Типы модуля живьём (ТЗ CORE-19.2, CORE-20.2) ──

    /// <summary>
    /// СТОРОЖ ЗАКРЫТОГО ТИПА — первого живьём. Поле в шапку отчёта не добавить: схему закрытого типа задаёт модуль.
    /// </summary>
    [Fact]
    public async Task Поле_в_закрытом_типе_отчёта_отвергается()
    {
        var (report, _) = await WorkTypesAsync();

        var refusal = await Assert.ThrowsAsync<ConflictException>(() => AddFieldAsync(report, "ДопускИнженера", required: false));

        Assert.Contains("схему закрытого типа задаёт модуль", refusal.Message);
    }

    /// <summary>Необязательное поле в сведениях строки — сохраняется и принимается охраной записи; обязательное — отказ.</summary>
    [Fact]
    public async Task Сведения_строки_принимают_только_необязательные_поля()
    {
        var (_, details) = await WorkTypesAsync();

        await AddFieldAsync(details, "НомерПротокола", required: false);
        var required = await Assert.ThrowsAsync<ConflictException>(() => AddFieldAsync(details, "Допуск", required: true));
        Assert.Contains("обязательным", required.Message);

        using var scope = fixture.Services.CreateScope();
        var guard = scope.ServiceProvider.GetRequiredService<IModuleWriteGuard>();
        Assert.Empty(await guard.RefusalsAsync(details, null, """{"НомерПротокола":"П-12"}"""));
        // Колонку строки в сведения не положить: значение поля модуля кладёт код.
        Assert.NotEmpty(await guard.RefusalsAsync(details, null, """{"Объём":5}"""));
    }

    /// <summary>Колонки отчёта и строки видны в типах полями модуля с замком.</summary>
    [Fact]
    public async Task Колонки_видны_в_типах_полями_модуля_с_замком()
    {
        var (report, details) = await WorkTypesAsync();

        var reportType = (await SendAsync(new GetDocumentTypeQuery(report)))!;
        var detailsType = (await SendAsync(new GetDocumentTypeQuery(details)))!;

        Assert.Equal(SchemaEditLevel.Closed, reportType.EditLevel);
        Assert.Equal(SchemaEditLevel.Extendable, detailsType.EditLevel);
        foreach (var (type, keys) in new[] { (reportType, new[] { "Сотрудник", "Дата", "Смена", "Часы" }), (detailsType, ["ВидРаботы", "Объём", "Место"]) })
        {
            var fields = type.Schema.RootElement.GetProperty("fields").EnumerateArray().ToList();
            foreach (var key in keys)
            {
                var field = fields.Single(f => f.GetProperty("key").GetString() == key);
                Assert.Equal("module", field.GetProperty("origin").GetString());
                Assert.True(field.GetProperty("locked").GetBoolean(), $"{type.Code}.{key} без замка");
            }
        }
    }

    // ── Подготовка ──

    private sealed record Seeded(Guid Site, Guid Section, Guid WorkType, Guid Unit);

    private async Task<Seeded> SeedAsync()
    {
        var site = await SendAsync(new CreateConstructionCommand("Стройка А", Guid.NewGuid()));
        var section = await SendAsync(new CreateSectionCommand(site.Id, "ЭОМ-1"));
        var (unitType, workType) = await seed.ClassifierAsync();
        var unit = await seed.RecordAsync(unitType.Id, "м");
        var work = await seed.RecordAsync(workType.Id, "Прокладка кабеля", WorkTypeSeed.WorkTypeData("01.02", "Прокладка кабеля", unit));
        return new Seeded(site.Id, section.Id, work.Id, unit.Id);
    }

    /// <summary>Справочники-цели и проекция типов, как на запуске: «Персона» и классификатор → типы модуля.</summary>
    private async Task<(Guid Report, Guid Details)> WorkTypesAsync()
    {
        await seed.TypeAsync(CoreRecordTypes.PersonCode, "Лицо");
        await seed.ClassifierAsync();
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.ProjectModuleTypesAsync();

        using var types = fixture.Services.CreateScope();
        var lookup = types.ServiceProvider.GetRequiredService<IModuleTypes>();
        return ((await lookup.FindAsync(WorkRecordTypes.ReportCode))!.Value, (await lookup.FindAsync(WorkRecordTypes.LineDetailsCode))!.Value);
    }

    private async Task AddFieldAsync(Guid typeId, string key, bool required)
    {
        var type = (await SendAsync(new GetDocumentTypeQuery(typeId)))!;
        var schema = JsonNode.Parse(type.Schema.RootElement.GetRawText())!.AsObject();
        schema["fields"]!.AsArray().Add(new JsonObject { ["key"] = key, ["title"] = key, ["type"] = "string", ["required"] = required });
        await SendAsync(new UpdateDocumentTypeSchemaCommand(typeId, JsonDocument.Parse(schema.ToJsonString())));
    }

    private static long _seq;

    private static Report NewReport(Guid employee, Guid site, ReportShift shift) =>
        Report.Create(Guid.NewGuid(), employee, site, Day16, shift, 8, null, Interlocked.Increment(ref _seq), DateTimeOffset.UtcNow);

    private static ReportLineValues Values(
        Seeded s, bool crew = false, IReadOnlyList<Guid>? participants = null, IReadOnlyList<ReportMaterialValues>? materials = null) =>
        new(Guid.NewGuid(), s.WorkType, null, 120m, s.Unit, s.Section, "оси 3–4", null, crew, null, materials ?? [], participants ?? []);

    private async Task<Guid> ResolveAsync(Seeded s, PlanItemDemand demand)
    {
        using var scope = fixture.Services.CreateScope();
        var items = await scope.ServiceProvider.GetRequiredService<WorkPlanItemLink>().ResolveAsync(s.Site, [demand], default);
        return items[demand.Line.Id];
    }

    private async Task SaveAsync(Report report)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        db.Add(report);
        await db.SaveChangesAsync();
    }

    private async Task<int> PlanItemCountAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>().GetAllAsync()).Count;
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    private async Task SendAsync(IRequest request)
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }
}
