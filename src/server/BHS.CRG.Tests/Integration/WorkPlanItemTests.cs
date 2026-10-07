using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Перечень работ стройки: таблица, ключ и правила CORE-11 (ТЗ CORE-10, CORE-11, CORE-13, issue #964).
///
/// <para>Позиция — якорь всех модулей: план держит смета, факт — учёт работ, акт — исполнительная
/// документация, и каждый ссылается на позицию. Отсюда все три правила, которые здесь проверяются:
/// в позицию нельзя добавить чужое поле, ключ уникален целиком, удаляется позиция только когда на
/// неё никто не ссылается.</para>
///
/// <para>⚠️ Держателей ссылок сегодня НЕТ: модули, которые будут ссылаться, приходят в этапе 2.
/// Поэтому правило проверяется ПОДСТАВНЫМ держателем — иначе сторож был бы зелёным всегда, как это
/// уже случилось в #962, где «завести внешний ключ с каскадом» проверяло ключ, которого в этом
/// хранении не существует.</para>
/// </summary>
[Collection("Integration")]
public class WorkPlanItemTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Подставной держатель ссылок: столько ссылок, сколько велел тест.</summary>
    private sealed class FakeReferrer(string what, int count) : IRecordHolders
    {
        public IReadOnlyCollection<Guid>? Asked { get; private set; }

        public Task<RecordHoldings> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        {
            Asked = ids;
            return Task.FromResult(count == 0 ? RecordHoldings.None : new RecordHoldings([$"{what}: {count}"]));
        }

        public Task<HeldRecords> HeldAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult(new HeldRecords(count == 0 ? new HashSet<Guid>() : ids.ToHashSet(), Verified: true));
    }

    // ── Сторож задачи: в позицию не добавить чужого поля ───────────────────────

    /// <summary>
    /// СТОРОЖ ЗАДАЧИ. У позиции ровно ключ и ничего больше (ТЗ CORE-11): ни объёма, ни цены, ни
    /// сроков, ни факта, ни статуса.
    ///
    /// <para>Список белый, а не правило: новое поле в позиции — это решение, и оно обязано пройти
    /// через человека. Молчаливое «ещё одна колонка» — это и есть свалка, от которой CORE-11
    /// защищает: сегодня статус, завтра объём, и позиция перестаёт быть общей точкой, потому что у
    /// каждого модуля появляется свой повод в неё писать.</para>
    ///
    /// <para>Спрашиваем МОДЕЛЬ EF, а не класс: колонку можно завести и конфигурацией (теневым
    /// свойством), и такую проверка по свойствам класса не увидела бы вовсе.</para>
    /// </summary>
    [Fact]
    public void В_позиции_ничего_кроме_ключа()
    {
        _ = fixture.CreateClient();

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.AppDbContext>();
        var entity = db.Model.FindEntityType(typeof(WorkPlanItem))!;

        var columns = entity.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(
            ["ConstructionId", "CreatedAt", "Id", "SectionId", "UnitId", "UpdatedAt", "WorkTypeId"],
            columns);

        // Навигаций тоже нет: позиция — якорь, а не агрегат, и «позиция со списком чего-нибудь»
        // означала бы, что у неё появилось содержимое.
        Assert.Empty(entity.GetNavigations());
    }

    // ── Ключ ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ключ уникален ЦЕЛИКОМ, и пустой раздел в нём — значение, а не «неизвестно».
    ///
    /// <para>Проверка не педантичная: по умолчанию PostgreSQL считает NULL в уникальном индексе
    /// разными значениями, и две позиции «стройка в целом» легли бы обе. А «стройка в целом» — это
    /// штатный случай (ТЗ CORE-Q2): смета у заказчика бывает на стройку без разделов. Дальше «найди
    /// или создай» находил бы то одну, то другую, и мнения модулей разошлись бы по двум позициям с
    /// одинаковым смыслом.</para>
    /// </summary>
    [Fact]
    public async Task Ключ_уникален_вместе_с_пустым_разделом()
    {
        var (construction, _, workType, unit) = await SeedAsync();

        await AddAsync(WorkPlanItem.Create(workType, construction, null, unit));

        var again = await Assert.ThrowsAsync<DbUpdateException>(
            () => AddAsync(WorkPlanItem.Create(workType, construction, null, unit)));
        Assert.Contains("IX_work_plan_items", again.InnerException?.Message ?? "");
    }

    /// <summary>
    /// А позиции, различающиеся хоть одной составляющей, — разные. Проверяется вместе с предыдущим:
    /// уникальность, запрещающая лишнее, так же плоха, как уникальность, не запрещающая нужного.
    /// </summary>
    [Fact]
    public async Task Различие_в_любой_составляющей_даёт_другую_позицию()
    {
        var (construction, section, workType, unit) = await SeedAsync();
        var otherUnit = await AddObjectAsync(await UnitTypeAsync(), "компл");

        await AddAsync(WorkPlanItem.Create(workType, construction, null, unit));
        await AddAsync(WorkPlanItem.Create(workType, construction, section, unit));       // раздел
        await AddAsync(WorkPlanItem.Create(workType, construction, null, otherUnit));     // единица

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>();
        Assert.Equal(3, (await repo.GetAllAsync()).Count);
    }

    // ── Удаление ──────────────────────────────────────────────────────────────

    /// <summary>
    /// СТОРОЖ ЗАДАЧИ. Удаление позиции, на которую ссылаются, отказывает — И НАЗЫВАЕТ ЧИСЛО ссылок
    /// (ТЗ CORE-11). Числа мало для красоты: «на позицию ссылаются» не говорит человеку, сколько
    /// работы его ждёт, а «позиции смет: 3» — говорит.
    /// </summary>
    [Fact]
    public async Task Удаление_позиции_со_ссылками_отказывает_с_числом()
    {
        var (construction, _, workType, unit) = await SeedAsync();
        var item = await AddAsync(WorkPlanItem.Create(workType, construction, null, unit));

        var referrer = new FakeReferrer("позиции смет", 3);

        using var scope = fixture.Services.CreateScope();
        var refusal = await Assert.ThrowsAsync<RecordHeldException>(() => new WorkPlanItemHandlers(
                scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>(), referrer)
            .Handle(new DeleteWorkPlanItemCommand(item.Id), default));

        Assert.Contains("позиции смет: 3", refusal.Message);
        // Спросили ровно про эту позицию, а не «про всё вообще».
        Assert.Equal([item.Id], referrer.Asked!);

        // И позиция на месте: отказ не оставляет половины дела.
        Assert.NotNull(await scope.ServiceProvider
            .GetRequiredService<IRepository<WorkPlanItem>>().GetByIdAsync(item.Id));
    }

    /// <summary>Без ссылок позиция удаляется — иначе правило означало бы «не удаляется никогда».</summary>
    [Fact]
    public async Task Без_ссылок_позиция_удаляется()
    {
        var (construction, _, workType, unit) = await SeedAsync();
        var item = await AddAsync(WorkPlanItem.Create(workType, construction, null, unit));

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>();
        await new WorkPlanItemHandlers(repo, new FakeReferrer("позиции смет", 0))
            .Handle(new DeleteWorkPlanItemCommand(item.Id), default);

        using var after = fixture.Services.CreateScope();
        Assert.Null(await after.ServiceProvider
            .GetRequiredService<IRepository<WorkPlanItem>>().GetByIdAsync(item.Id));
    }

    /// <summary>
    /// Каскад уровня обходил бы правило с фланга: удаление стройки уносит её перечень внешним
    /// ключом, и ссылки модулей повисли бы молча. Поэтому прикладной каскад спрашивает держателей —
    /// тем же приёмом, каким это уже сделано для объектов уровня (issue #739).
    /// </summary>
    [Fact]
    public async Task Удаление_стройки_с_занятым_перечнем_отказывает()
    {
        var (construction, section, workType, unit) = await SeedAsync();
        await AddAsync(WorkPlanItem.Create(workType, construction, section, unit));

        using var scope = fixture.Services.CreateScope();
        var cascade = CascadeWith(scope, new FakeReferrer("строки отчётов монтажников", 7));

        var plan = await cascade.PlanAsync(CatalogScope.Construction, construction);
        var refusal = Assert.Throws<RecordHeldException>(() => cascade.EnsureDeletable(plan, "стройку"));
        Assert.Contains("строки отчётов монтажников: 7", refusal.Message);

        // Раздел — тот же вопрос: позиция раздела уйдёт с ним, и держатель об этом не узнает.
        var forSection = await cascade.PlanAsync(CatalogScope.Section, section);
        Assert.Throws<RecordHeldException>(() => cascade.EnsureDeletable(forSection, "раздел"));
    }

    /// <summary>
    /// А без держателей ссылок уровень удаляется, и перечень уходит с ним каскадом базы: правило
    /// «только без ссылок» не должно превращаться в «стройку теперь не удалить».
    /// </summary>
    [Fact]
    public async Task Без_держателей_стройка_удаляется_вместе_с_перечнем()
    {
        var (construction, section, workType, unit) = await SeedAsync();
        await AddAsync(WorkPlanItem.Create(workType, construction, section, unit));

        using var scope = fixture.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new DeleteConstructionCommand(construction));

        using var after = fixture.Services.CreateScope();
        Assert.Empty(await after.ServiceProvider
            .GetRequiredService<IRepository<WorkPlanItem>>().GetAllAsync());
    }

    /// <summary>
    /// Запись классификатора, на которую ссылается перечень, не удаляется — и это ОТКАЗ, а не
    /// внутренняя ошибка сервера. Внешний ключ здесь последний рубеж, а человеку нужен текст:
    /// «поломка» отправила бы его писать в поддержку вместо того, чтобы убрать позиции.
    /// </summary>
    [Fact]
    public async Task Запись_из_перечня_не_удаляется_отказом_а_не_поломкой()
    {
        var (construction, _, workType, unit) = await SeedAsync();
        await AddAsync(WorkPlanItem.Create(workType, construction, null, unit));

        using var scope = fixture.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => mediator.Send(new DeleteCommonDataEntryCommand(workType)));
        Assert.Contains("перечень работ, позиций: 1", refusal.Message);

        // Единица измерения — тот же случай: без неё позиция означала бы «сколько-то чего-то».
        Assert.Contains("перечень работ",
            (await Assert.ThrowsAsync<ConflictException>(
                () => mediator.Send(new DeleteCommonDataEntryCommand(unit)))).Message);
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>Каскад уровня с подставным держателем ссылок на позиции.</summary>
    private static ScopeCascade CascadeWith(IServiceScope scope, IRecordHolders referrer) =>
        new(scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>(),
            scope.ServiceProvider.GetRequiredService<IRepository<QualityDocument>>(),
            scope.ServiceProvider.GetRequiredService<IRepository<MaterialQualityLink>>(),
            scope.ServiceProvider.GetRequiredService<IRepository<Section>>(),
            scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>(),
            referrer,
            scope.ServiceProvider.GetRequiredService<IReferenceIndex>(),
            scope.ServiceProvider.GetRequiredService<IScopeSubtree>());

    /// <summary>Стройка с разделом, запись классификатора и единица измерения — минимум для позиции.</summary>
    private async Task<(Guid Construction, Guid Section, Guid WorkType, Guid Unit)> SeedAsync()
    {
        _ = fixture.CreateClient();

        using var scope = fixture.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var construction = await mediator.Send(new CreateConstructionCommand("Стройка", Guid.NewGuid()));
        var section = await mediator.Send(new CreateSectionCommand(construction.Id, "ЭОМ-1"));

        var workType = await AddObjectAsync(
            await TypeAsync(CoreRecordTypes.WorkTypeCode, "Вид работы"), "Прокладка кабеля");
        var unit = await AddObjectAsync(await UnitTypeAsync(), "м");

        return (construction.Id, section.Id, workType, unit);
    }

    private Task<Guid> UnitTypeAsync() => TypeAsync(CoreRecordTypes.UnitCode, "Единица измерения");

    private async Task<Guid> TypeAsync(string code, string name)
    {
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        var found = await repo.FindAsync(t => t.Code == code);
        if (found.Count > 0) return found[0].Id;

        var type = DocumentType.Create(name, code, DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared);
        await repo.AddAsync(type);
        await repo.SaveChangesAsync();
        return type.Id;
    }

    private async Task<Guid> AddObjectAsync(
        Guid typeId, string name, CatalogScope scope = CatalogScope.System, Guid? scopeId = null)
    {
        using var s = fixture.Services.CreateScope();
        var repo = s.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
        var obj = DomainObject.Create(typeId, name, JsonDocument.Parse("{}"), scope, scopeId);
        await repo.AddAsync(obj);
        await repo.SaveChangesAsync();
        return obj.Id;
    }

    private async Task<WorkPlanItem> AddAsync(WorkPlanItem item)
    {
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<WorkPlanItem>>();
        await repo.AddAsync(item);
        await repo.SaveChangesAsync();
        return item;
    }

    // ── Находки ревью PR #1056 ────────────────────────────────────────────────

    /// <summary>
    /// Запись классификатора, лежащая НА УРОВНЕ УДАЛЯЕМОЙ СТРОЙКИ (ревью PR #1056, воспроизведено на
    /// живой базе).
    ///
    /// <para>Каскад внешнего ключа тут не спасал: EF удаляет <c>domain_objects</c> раньше строек, и
    /// запись уходила до того, как база унесёт ссылающуюся на неё позицию. Ключ на вид работы —
    /// RESTRICT, поэтому сохранение падало отказом базы, а человек видел внутреннюю ошибку сервера
    /// вместо удаления стройки — ровно то, что этот PR обещает исключить.</para>
    ///
    /// <para>Теперь позиции уносит прикладной каскад, и уносит ПЕРВЫМИ.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]   // запись на уровне стройки
    [InlineData(false)]  // запись на уровне раздела
    public async Task Удаление_уровня_с_записью_классификатора_на_нём_проходит(bool onConstruction)
    {
        var (construction, section, _, unit) = await SeedAsync();
        var scopeId = onConstruction ? construction : section;
        var level = onConstruction ? CatalogScope.Construction : CatalogScope.Section;

        var localWorkType = await AddObjectAsync(
            await TypeAsync(CoreRecordTypes.WorkTypeCode, "Вид работы"), "Своя работа", level, scopeId);
        await AddAsync(WorkPlanItem.Create(localWorkType, construction, section, unit));

        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new DeleteConstructionCommand(construction));

        using var after = fixture.Services.CreateScope();
        Assert.Empty(await after.ServiceProvider
            .GetRequiredService<IRepository<WorkPlanItem>>().GetAllAsync());
        Assert.Null(await after.ServiceProvider
            .GetRequiredService<IRepository<DomainObject>>().GetByIdAsync(localWorkType));
    }

    /// <summary>
    /// А позиция ЧУЖОЙ стройки, ссылающаяся на запись этого уровня, удаление запрещает: унести
    /// запись значило бы оставить чужой перечень без смысла, и внешний ключ её не отдаст. Отказ
    /// вместо отказа базы — то же различие «так нельзя» против «сломалось».
    /// </summary>
    [Fact]
    public async Task Позиция_чужой_стройки_держит_запись_уровня()
    {
        var (construction, section, _, unit) = await SeedAsync();
        var localWorkType = await AddObjectAsync(
            await TypeAsync(CoreRecordTypes.WorkTypeCode, "Вид работы"), "Своя работа",
            CatalogScope.Construction, construction);

        // Вторая стройка, и позиция ЕЁ перечня смотрит на запись первой.
        Guid other;
        using (var scope = fixture.Services.CreateScope())
            other = (await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new CreateConstructionCommand("Чужая стройка", Guid.NewGuid()))).Id;
        await AddAsync(WorkPlanItem.Create(localWorkType, other, null, unit));

        using var s = fixture.Services.CreateScope();
        var refusal = await Assert.ThrowsAsync<ConflictException>(
            () => s.ServiceProvider.GetRequiredService<IMediator>()
                .Send(new DeleteConstructionCommand(construction)));
        Assert.Contains("перечень работ ДРУГИХ строек", refusal.Message);
        Assert.Contains("позиций: 1", refusal.Message);
        _ = section;
    }

    /// <summary>
    /// Уборка сирот (issue #739) удаляет объекты ОДНИМ запросом, поэтому сирота, на которую
    /// ссылается позиция перечня, уронила бы всю операцию отказом базы — и не убралось бы НИЧЕГО,
    /// включая сирот, к перечню отношения не имеющих (ревью PR #1056). Скан ссылок её не видел:
    /// ссылка здесь колонкой, а не «$ref» в данных.
    /// </summary>
    [Fact]
    public async Task Уборка_сирот_не_спотыкается_о_перечень()
    {
        var (construction, _, _, unit) = await SeedAsync();

        // Сирота: объект на уровне комплекта, которого нет. На неё смотрит позиция перечня.
        var orphanHeld = await AddObjectAsync(
            await TypeAsync(CoreRecordTypes.WorkTypeCode, "Вид работы"), "Сирота в перечне",
            CatalogScope.Set, Guid.NewGuid());
        await AddAsync(WorkPlanItem.Create(orphanHeld, construction, null, unit));

        // И вторая сирота, к перечню отношения не имеющая: именно её потеря доказывала бы, что
        // уборка падала целиком.
        var orphanFree = await AddObjectAsync(
            await TypeAsync(CoreRecordTypes.UnitCode, "Единица измерения"), "Сирота свободная",
            CatalogScope.Set, Guid.NewGuid());

        using var scope = fixture.Services.CreateScope();
        var cleanup = scope.ServiceProvider
            .GetRequiredService<Infrastructure.Maintenance.OrphanObjectCleanup>();

        var dry = await cleanup.RunAsync(dryRun: true);
        Assert.Equal(2, dry.Objects);
        Assert.Equal(1, dry.Referenced);   // держит перечень
        Assert.Equal(1, dry.Total);        // уберётся только свободная

        var real = await cleanup.RunAsync(dryRun: false);
        Assert.Equal(1, real.Total);

        var objects = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
        Assert.Null(await objects.GetByIdAsync(orphanFree));
        Assert.NotNull(await objects.GetByIdAsync(orphanHeld));
    }
}
