using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Reconciliation;
using BHS.CRG.Application.Reconciliation;
using MediatR;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// План по документам (issue #796): план задаётся на комплекте, уровни выше консолидируются.
///
/// Главное, что здесь проверяется, — не «сохранилось ли», а границы честности процента: сверх
/// плана не считается, комплекты без плана не выдают себя за готовые, «100 %» не показывается при
/// неразобранной сверке.
/// </summary>
[Collection("Integration")]
public class DocumentPlanTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private readonly Guid _userId = Guid.NewGuid();

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();

    private async Task<(Construction C, Section S, DocumentSet Set)> TreeAsync(string name = "Комплект")
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var c = await m.Send(new CreateConstructionCommand("Объект", _userId));
        var s = await m.Send(new CreateSectionCommand(c.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(s.Id, name));
        return (c, s, set);
    }

    private async Task<Guid> SetInAsync(Guid sectionId, string name)
    {
        using var scope = fixture.Services.CreateScope();
        return (await M(scope).Send(new CreateDocumentSetCommand(sectionId, name))).Id;
    }

    private async Task<Guid> TypeAsync(string code)
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await M(scope).Send(new CreateDocumentTypeCommand(
            code, code, DocumentTypeKind.Document, null, JsonDocument.Parse("""{"fields":[]}""")));
        return dt.Id;
    }

    /// <summary>Документ в комплекте; <paramref name="generated"/> — «выпущен», а не просто заведён.</summary>
    private async Task<Guid> DocumentAsync(Guid setId, Guid typeId, bool generated)
    {
        using var scope = fixture.Services.CreateScope();
        var doc = await M(scope).Send(new AddDocumentToSetCommand(setId, typeId));
        if (!generated) return doc.Id;

        // Через тот же путь, что и настоящая генерация: файл добавляется отдельной записью
        // (GenerateDocumentHandler делает так же), а объект уже отслеживается — Update() на нём
        // пометил бы Modified и новорождённый файл, и SaveChanges попытался бы его ОБНОВИТЬ.
        var repo = scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
        var files = scope.ServiceProvider.GetRequiredService<IRepository<GeneratedFile>>();
        var tracked = (await repo.GetSetDocumentsAsync(setId, tracked: true)).First(d => d.Id == doc.Id);
        await files.AddAsync(tracked.AddGeneratedFile(OutputFormat.Pdf, $"blob/{doc.Id}.pdf"));
        await repo.SaveChangesAsync();
        return doc.Id;
    }

    private async Task PlanAsync(Guid setId, params (Guid TypeId, int Count)[] rows)
    {
        using var scope = fixture.Services.CreateScope();
        await M(scope).Send(new ReplaceDocumentSetPlanCommand(setId,
            [.. rows.Select(r => new PlanRow(r.TypeId, r.Count))]));
    }

    private async Task<PlanSummary> SummaryAsync(CatalogScope scope_, Guid? id)
    {
        using var scope = fixture.Services.CreateScope();
        return await M(scope).Send(new GetPlanSummaryQuery(scope_, id));
    }

    // ── План комплекта ────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_IsReplacedWholesale_AndShowsActualCounts()
    {
        var (_, _, set) = await TreeAsync();
        var aosr = await TypeAsync("AOSR");
        var journal = await TypeAsync("JOURNAL");

        await PlanAsync(set.Id, (aosr, 3), (journal, 1));
        await DocumentAsync(set.Id, aosr, generated: true);
        await DocumentAsync(set.Id, aosr, generated: false);   // черновик фактом не считается

        using (var scope = fixture.Services.CreateScope())
        {
            var rows = await M(scope).Send(new GetDocumentSetPlanQuery(set.Id));
            Assert.Equal(2, rows.Count);
            var aosrRow = rows.Single(r => r.DocumentTypeId == aosr);
            Assert.Equal(3, aosrRow.PlannedCount);
            Assert.Equal(1, aosrRow.ActualCount);
        }

        // Замена целиком: прислали одну строку — вторая ушла.
        await PlanAsync(set.Id, (aosr, 2));

        using var check = fixture.Services.CreateScope();
        var after = await M(check).Send(new GetDocumentSetPlanQuery(set.Id));
        Assert.Equal(2, Assert.Single(after).PlannedCount);
    }

    /// <summary>Пустой список — это «плана нет»: проценты должны исчезнуть с экранов совсем.</summary>
    [Fact]
    public async Task EmptyPlan_MeansNoPlan_NotZeroPercent()
    {
        var (_, _, set) = await TreeAsync();
        var type = await TypeAsync("AOSR");
        await PlanAsync(set.Id, (type, 2));
        await PlanAsync(set.Id);

        var summary = await SummaryAsync(CatalogScope.Set, set.Id);
        Assert.False(summary.Own.HasPlan);
        Assert.Null(summary.Own.Percent);
    }

    [Fact]
    public async Task Plan_RejectsUnknownType_ZeroCount_AndDuplicates()
    {
        var (_, _, set) = await TreeAsync();
        var type = await TypeAsync("AOSR");
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);

        await Assert.ThrowsAsync<InvalidRequestException>(() => m.Send(
            new ReplaceDocumentSetPlanCommand(set.Id, [new PlanRow(Guid.NewGuid(), 1)])));
        await Assert.ThrowsAsync<InvalidRequestException>(() => m.Send(
            new ReplaceDocumentSetPlanCommand(set.Id, [new PlanRow(type, 0)])));
        await Assert.ThrowsAsync<InvalidRequestException>(() => m.Send(
            new ReplaceDocumentSetPlanCommand(set.Id, [new PlanRow(type, 1), new PlanRow(type, 2)])));
    }

    // ── Процент ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Percent_CountsOnlyGeneratedDocuments_AndNotBeyondThePlan()
    {
        var (_, _, set) = await TreeAsync();
        var aosr = await TypeAsync("AOSR");
        await PlanAsync(set.Id, (aosr, 2));

        // Три выпущенных при плане в два: закрыто два, а не «150 %».
        for (var i = 0; i < 3; i++) await DocumentAsync(set.Id, aosr, generated: true);
        await DocumentAsync(set.Id, aosr, generated: false);

        var summary = await SummaryAsync(CatalogScope.Set, set.Id);
        Assert.Equal(2, summary.Own.Planned);
        Assert.Equal(2, summary.Own.Ready);
        Assert.Equal(100, summary.Own.Percent);
    }

    /// <summary>Типы вне плана процент не трогают: внеплановая работа — не выполнение плана.</summary>
    [Fact]
    public async Task DocumentsOfUnplannedTypes_DoNotAffectPercent()
    {
        var (_, _, set) = await TreeAsync();
        var planned = await TypeAsync("AOSR");
        var other = await TypeAsync("JOURNAL");
        await PlanAsync(set.Id, (planned, 2));
        await DocumentAsync(set.Id, other, generated: true);

        var summary = await SummaryAsync(CatalogScope.Set, set.Id);
        Assert.Equal(0, summary.Own.Ready);
        Assert.Equal(0, summary.Own.Percent);
    }

    // ── Консолидация вверх ────────────────────────────────────────────────────

    [Fact]
    public async Task SectionAndConstruction_ConsolidateTheirSets()
    {
        var (construction, section, first) = await TreeAsync("Первый");
        var second = await SetInAsync(section.Id, "Второй");
        var type = await TypeAsync("AOSR");

        await PlanAsync(first.Id, (type, 2));
        await PlanAsync(second, (type, 2));
        await DocumentAsync(first.Id, type, generated: true);
        await DocumentAsync(second, type, generated: true);
        await DocumentAsync(second, type, generated: true);

        var atSection = await SummaryAsync(CatalogScope.Section, section.Id);
        Assert.Equal(4, atSection.Own.Planned);
        Assert.Equal(3, atSection.Own.Ready);
        Assert.Equal(75, atSection.Own.Percent);
        Assert.Equal(2, atSection.Children.Count);

        var atConstruction = await SummaryAsync(CatalogScope.Construction, construction.Id);
        Assert.Equal(4, atConstruction.Own.Planned);
        Assert.Equal(3, atConstruction.Own.Ready);

        // Стройка не должна посчитать свои комплекты дважды — сама и через разделы.
        Assert.Equal(atSection.Own.Planned, atConstruction.Own.Planned);
    }

    /// <summary>
    /// Комплект без плана в проценте не участвует, но и не молчит. Иначе «раздел на 100 %» означало
    /// бы «единственный расписанный комплект закрыт», а про остальные экран не сказал бы ничего.
    /// </summary>
    [Fact]
    public async Task SetsWithoutPlan_AreExcludedFromPercent_ButCounted()
    {
        var (_, section, planned) = await TreeAsync("С планом");
        await SetInAsync(section.Id, "Без плана");
        var type = await TypeAsync("AOSR");

        await PlanAsync(planned.Id, (type, 1));
        await DocumentAsync(planned.Id, type, generated: true);

        var summary = await SummaryAsync(CatalogScope.Section, section.Id);
        Assert.Equal(1, summary.Own.Planned);
        Assert.Equal(100, summary.Own.Percent);
        Assert.Equal(1, summary.Own.SetsWithoutPlan);
        Assert.Single(summary.Children);   // в разбивку попал только расписанный
    }

    // ── Тип из плана нельзя удалить молча ─────────────────────────────────────

    /// <summary>
    /// Внешнего ключа от плана к типу нет намеренно (каскад унёс бы строки плана вместе с типом, и
    /// процент поехал бы молча). Значит проверка занятости — единственная защита, и она обязана
    /// про план знать.
    /// </summary>
    [Fact]
    public async Task DeletingTypeUsedInPlan_IsRefused()
    {
        var (_, _, set) = await TreeAsync();
        var type = await TypeAsync("AOSR");
        await PlanAsync(set.Id, (type, 1));

        using var scope = fixture.Services.CreateScope();
        var usage = await M(scope).Send(new GetDocumentTypeUsageQuery(type));
        Assert.Contains(usage.Reasons, r => r.Kind == "plan");

        await Assert.ThrowsAsync<ConflictException>(() => M(scope).Send(new DeleteDocumentTypeCommand(type)));
    }

    /// <summary>
    /// Комплекта нет — это НЕ «комплект без плана». Разница видна на устаревшей навигации: клиент
    /// получил бы пустую форму плана, заполнил и упёрся в 404 уже на сохранении.
    /// </summary>
    [Fact]
    public async Task PlanOfMissingSet_IsNotFound_NotAnEmptyPlan()
    {
        using var scope = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<NotFoundException>(
            () => M(scope).Send(new GetDocumentSetPlanQuery(Guid.NewGuid())));
    }

    /// <summary>
    /// Процент не показывает «сто» при неразобранном НИ НА ОДНОМ уровне — включая верхний, где
    /// расписанная стройка складывается с бесплановой соседкой. Счётчик разбора берётся у сводки
    /// проблем, а она знает его и по детям без плана.
    /// </summary>
    [Fact]
    public async Task SystemLevel_DoesNotClaimHundred_WhenAnotherConstructionHasUnreviewedProblems()
    {
        var (_, _, planned) = await TreeAsync("Расписанный");
        var type = await TypeAsync("AOSR");
        await PlanAsync(planned.Id, (type, 1));
        await DocumentAsync(planned.Id, type, generated: true);

        // Вторая стройка: плана нет, зато есть неразобранное замечание.
        var (_, _, other) = await TreeAsync("Без плана");
        using (var scope = fixture.Services.CreateScope())
            await M(scope).Send(new ReportObservationCommand(
                CatalogScope.Set, other.Id, "key-1", "Замечание", null,
                ObservationSeverity.Warning, JsonDocument.Parse("{}"), "agent"));

        var atSystem = await SummaryAsync(CatalogScope.System, null);
        Assert.Equal(1, atSystem.Own.Planned);
        Assert.Equal(1, atSystem.Own.Ready);
        Assert.Equal(1, atSystem.Own.NeedsAttention);
        Assert.Equal(99, atSystem.Own.Percent);
    }

    /// <summary>
    /// Восстановление поверх системы, где план УЖЕ правили, не должно ронять весь импорт.
    ///
    /// Правка плана удаляет строки и заводит новые со свежими идентификаторами, а пара
    /// (комплект, тип) остаётся прежней и защищена уникальным индексом. Раскладка по одному лишь
    /// Id упиралась бы в 23505 — а он здесь означает не «пропустим строку», а откат ВСЕЙ
    /// транзакции: администратор получил бы «Ошибка восстановления БД» и пустую систему из-за
    /// одной строки плана. Проверяем именно связку «сняли копию → поправили план → восстановили».
    /// </summary>
    [Fact]
    public async Task Restore_AfterPlanWasEdited_SucceedsAndKeepsOneRowPerType()
    {
        var (_, _, set) = await TreeAsync();
        var type = await TypeAsync("AOSR");
        await PlanAsync(set.Id, (type, 3));

        byte[] copy;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zip, _) = await BackupOf(scope).ExportAsync(BackupScope.Full);
            await using var _handle = zip;
            using var ms = new MemoryStream();
            await zip.CopyToAsync(ms);
            copy = ms.ToArray();
        }

        // Та же позиция плана, другое количество — и, из-за замены целиком, ДРУГОЙ идентификатор.
        await PlanAsync(set.Id, (type, 4));

        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await BackupOf(scope).ImportAsync(new MemoryStream(copy));

        Assert.True(report.Success, string.Join("; ", report.Warnings));

        using var check = fixture.Services.CreateScope();
        var rows = await M(check).Send(new GetDocumentSetPlanQuery(set.Id));
        var row = Assert.Single(rows);
        Assert.Equal(3, row.PlannedCount);   // вернулось значение из копии, а не осталось правленое
    }

    private static BackupService BackupOf(IServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        scope.ServiceProvider.GetRequiredService<IBlobStorage>(),
        NullLogger<BackupService>.Instance);

    /// <summary>Комплект удалён — его план уходит с ним: строки без носителя не оставляем.</summary>
    [Fact]
    public async Task DeletingSet_TakesItsPlanWithIt()
    {
        var (_, _, set) = await TreeAsync();
        var type = await TypeAsync("AOSR");
        await PlanAsync(set.Id, (type, 1));

        using (var scope = fixture.Services.CreateScope())
            await M(scope).Send(new DeleteDocumentSetCommand(set.Id));

        using var check = fixture.Services.CreateScope();
        var plans = check.ServiceProvider.GetRequiredService<IRepository<DocumentSetPlanItem>>();
        Assert.Empty(await plans.FindAsync(p => p.DocumentSetId == set.Id));
    }
}
